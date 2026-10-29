using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpecTrace.Specs;

namespace SpecTrace.Tool;

public enum LinkKind { Implements, Verifies }

/// <summary>An [Implements] or [Verifies] mark found in the code.</summary>
public sealed record TraceLink(LinkKind Kind, string RequirementId, string? Criterion, string Symbol, string File, int Line);

/// <summary>Traceability status of a requirement.</summary>
public sealed record RequirementTrace(SpecRequirement Requirement, IReadOnlyList<TraceLink> Implementations, IReadOnlyList<TraceLink> Verifications,
    SpecDrift? Drift = null)
{
    /// <summary>Text changes since the last review (spectrace.lock).</summary>
    public SpecDrift DriftInfo => Drift ?? SpecDrift.NotLocked;

    /// <summary>The statement changed and there is code implementing it: it must be reviewed (ST0010).</summary>
    public bool CodeNeedsReview => Requirement.IsEnforced && DriftInfo.StatementChanged && IsImplemented;

    /// <summary>Changed criteria that have tests: those tests must be reviewed (ST0011).</summary>
    public IEnumerable<SpecCriterion> CriteriaNeedingReview =>
        Requirement.IsEnforced
            ? DriftInfo.ChangedCriteria.Where(c => Verifications.Any(v => v.Criterion == c.Id))
            : Enumerable.Empty<SpecCriterion>();

    public bool HasDrift => CodeNeedsReview || CriteriaNeedingReview.Any();

    public bool IsImplemented => Implementations.Count > 0;
    public bool IsVerified => Verifications.Count > 0;

    public IEnumerable<SpecCriterion> UncoveredCriteria =>
        Requirement.Criteria.Where(c => !Verifications.Any(v => v.Criterion == c.Id));

    public int CoveredCriteria => Requirement.Criteria.Count - UncoveredCriteria.Count();

    public bool HasGap => Requirement.IsEnforced && (!IsImplemented || !IsVerified || UncoveredCriteria.Any());
}

/// <summary>A .csproj project in the code folders, with the namespace of its generated catalog.</summary>
/// <param name="Directory">Project folder, relative to the root and with '/' ("." for the root itself).</param>
public sealed record ProjectInfo(string Name, string Directory, string CatalogNamespace, bool IsTest)
{
    public bool Contains(string relativeFile) =>
        Directory == "." || relativeFile.StartsWith(Directory + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed record TraceModel(string Root, SpecCatalog Catalog, IReadOnlyList<RequirementTrace> Requirements,
    IReadOnlyList<TraceLink> UnknownLinks, IReadOnlyList<ProjectInfo> Projects, SpecLock? Lock = null)
{
    public bool HasLock => Lock != null;

    /// <summary>The innermost project containing the file (or null).</summary>
    public ProjectInfo? ProjectOf(string relativeFile) =>
        Projects.Where(p => p.Contains(relativeFile)).OrderByDescending(p => p.Directory.Length).FirstOrDefault();
}

/// <summary>
/// Builds the traceability model by reading the specs and parsing the C# code at the syntax level only,
/// without compiling or restoring packages: it is fast and works on any branch or in CI.
/// </summary>
public static class TraceScanner
{
    private static readonly string[] Skip = { "bin", "obj", ".git", "node_modules", ".vs" };

    public static TraceModel Scan(SpecTraceConfig config)
    {
        var specsDir = Path.Combine(config.Root, config.Specs);
        var docs = Directory.Exists(specsDir)
            ? Directory.EnumerateFiles(specsDir, "*" + SpecParser.FileSuffix, SearchOption.AllDirectories)
                .Select(f => SpecParser.Parse(Relative(config.Root, f), File.ReadAllText(f)))
            : Enumerable.Empty<SpecDocument>();
        var catalog = SpecCatalog.Build(docs);

        var links = new List<TraceLink>();
        foreach (var source in config.Sources.Select(s => Path.Combine(config.Root, s)).Where(Directory.Exists))
        {
            foreach (var file in EnumerateCs(source))
            {
                links.AddRange(ScanFile(config.Root, file, catalog));
            }
        }

        var specLock = LoadLock(config);
        var traces = catalog.Requirements
            .Select(r => new RequirementTrace(r,
                links.Where(l => l.Kind == LinkKind.Implements && Same(l.RequirementId, r.Id)).ToList(),
                links.Where(l => l.Kind == LinkKind.Verifies && Same(l.RequirementId, r.Id)).ToList(),
                specLock == null ? null : SpecDrift.Compare(r, specLock.FindFor(r))))
            .ToList();
        var unknown = links.Where(l => !catalog.TryGet(l.RequirementId, out _)).ToList();

        return new TraceModel(config.Root, catalog, traces, unknown, FindProjects(config), specLock);
    }

    public static string LockPath(SpecTraceConfig config) => Path.Combine(config.Root, SpecLock.FileName);

    public static SpecLock? LoadLock(SpecTraceConfig config)
    {
        var path = LockPath(config);
        return File.Exists(path) ? SpecLock.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>.cs files of the code folders (without bin/obj), as absolute paths.</summary>
    public static IEnumerable<string> SourceFiles(SpecTraceConfig config, string pattern = "*.cs") =>
        config.Sources.Select(s => Path.Combine(config.Root, s)).Where(Directory.Exists).SelectMany(d => Enumerate(d, pattern));

    /// <summary>
    /// .csproj projects of the code folders. The catalog namespace follows the same rule as the generator:
    /// SpecTraceNamespace, otherwise RootNamespace + ".Specs", and RootNamespace defaults to the project name.
    /// Only the .csproj itself is read (not Directory.Build.props).
    /// </summary>
    public static IReadOnlyList<ProjectInfo> FindProjects(SpecTraceConfig config)
    {
        var projects = new List<ProjectInfo>();
        foreach (var file in SourceFiles(config, "*.csproj"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            string? Prop(System.Xml.Linq.XDocument doc, string p) =>
                doc.Descendants().FirstOrDefault(e => e.Name.LocalName == p)?.Value.Trim() is { Length: > 0 } v ? v : null;

            System.Xml.Linq.XDocument xml;
            try
            {
                xml = System.Xml.Linq.XDocument.Load(file);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var root = Prop(xml, "RootNamespace") ?? name;
            var ns = Prop(xml, "SpecTraceNamespace") ?? root + ".Specs";
            var role = Prop(xml, "SpecTraceRole");
            var isTest = role != null
                ? string.Equals(role, "Tests", StringComparison.OrdinalIgnoreCase)
                : string.Equals(Prop(xml, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                  || xml.Descendants().Any(e => e.Name.LocalName == "PackageReference" &&
                         (e.Attribute("Include")?.Value is "Microsoft.NET.Test.Sdk" or "xunit" or "NUnit" or "MSTest.TestFramework" or "xunit.v3"))
                  || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);

            var dir = Relative(config.Root, Path.GetDirectoryName(file)!);
            projects.Add(new ProjectInfo(name, dir.Length == 0 ? "." : dir, ns, isTest));
        }

        return projects.OrderBy(p => p.Directory, StringComparer.Ordinal).ToList();
    }

    /// <summary>Files (relative to the root) that declare a type with that name: class, record, struct, interface or enum.</summary>
    public static IReadOnlyList<string> FindTypeFiles(SpecTraceConfig config, string typeName)
    {
        var name = typeName.Split('<')[0].Split('.').Last().Trim();
        var found = new List<string>();
        foreach (var file in SourceFiles(config))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(name, StringComparison.Ordinal)) continue;

            var declared = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes()
                .Any(n => n is BaseTypeDeclarationSyntax t && t.Identifier.Text == name
                       || n is DelegateDeclarationSyntax d && d.Identifier.Text == name);
            if (declared) found.Add(Relative(config.Root, file));
        }

        return found.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> EnumerateCs(string dir) => Enumerate(dir, "*.cs");

    private static IEnumerable<string> Enumerate(string dir, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(dir, pattern)) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (Skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in Enumerate(sub, pattern)) yield return file;
        }
    }

    private static IEnumerable<TraceLink> ScanFile(string root, string path, SpecCatalog catalog)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        foreach (var attr in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
        {
            var kind = SimpleName(attr.Name) switch
            {
                "Implements" or "ImplementsAttribute" => LinkKind.Implements,
                "Verifies" or "VerifiesAttribute" => (LinkKind?)LinkKind.Verifies,
                _ => null,
            };
            if (kind == null || attr.ArgumentList is not { Arguments.Count: > 0 } args) continue;
            if (kind == LinkKind.Verifies && IsSkipped(attr)) continue;

            var id = ResolveRequirement(args.Arguments[0].Expression, catalog);
            if (id == null) continue;
            var criterion = args.Arguments.Count > 1 ? ResolveCriterion(args.Arguments[1].Expression) : null;
            var line = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            yield return new TraceLink(kind.Value, id, criterion, Owner(attr), Relative(root, path), line);
        }
    }

    private static string? ResolveRequirement(ExpressionSyntax expr, SpecCatalog catalog) => expr switch
    {
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => lit.Token.ValueText,
        MemberAccessExpressionSyntax m => catalog.FindByIdentifier(m.Name.Identifier.Text)?.Id ?? m.Name.Identifier.Text,
        IdentifierNameSyntax i => catalog.FindByIdentifier(i.Identifier.Text)?.Id ?? i.Identifier.Text,
        _ => null,
    };

    private static string? ResolveCriterion(ExpressionSyntax expr) => expr switch
    {
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => lit.Token.ValueText.ToUpperInvariant(),
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text.Replace('_', '-'),
        IdentifierNameSyntax i => i.Identifier.Text.Replace('_', '-'),
        _ => null,
    };

    /// <summary>A test with Skip or [Ignore] does not count as verification (same rule as the analyzer).</summary>
    private static bool IsSkipped(AttributeSyntax attr)
    {
        var owner = attr.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (owner == null) return false;
        return owner.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
            SimpleName(a.Name) is "Ignore" or "IgnoreAttribute" ||
            (a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Skip") ?? false));
    }

    private static string SimpleName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => string.Empty,
    };

    private static string Owner(AttributeSyntax attr)
    {
        var member = attr.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        var type = attr.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var memberName = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            _ => null,
        };
        return memberName != null && type != null ? $"{type.Identifier.Text}.{memberName}" : type?.Identifier.Text ?? "?";
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
