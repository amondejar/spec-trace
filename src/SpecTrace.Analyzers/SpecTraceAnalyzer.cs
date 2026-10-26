using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SpecTrace.Specs;

namespace SpecTrace.Analyzers
{
    /// <summary>
    /// Comprueba la trazabilidad spec → código → test en cada compilación.
    /// Proyectos de implementación: IDs válidos (ST0001/2/6) y requisitos sin implementar (ST0003).
    /// Proyectos de test (IsTestProject=true): IDs válidos y requisitos/criterios implementados sin test (ST0004/5).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SpecTraceAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            Diagnostics.UnknownRequirement,
            Diagnostics.UnknownCriterion,
            Diagnostics.NotImplemented,
            Diagnostics.NotVerified,
            Diagnostics.CriterionNotVerified,
            Diagnostics.DeprecatedRequirement,
            Diagnostics.DuplicateInSpec,
            Diagnostics.SpecFormat,
            Diagnostics.NotConstant,
            Diagnostics.StatementChanged,
            Diagnostics.CriterionChanged);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext start)
        {
            var compilation = start.Compilation;
            var implementsType = compilation.GetTypeByMetadataName("SpecTrace.ImplementsAttribute");
            var verifiesType = compilation.GetTypeByMetadataName("SpecTrace.VerifiesAttribute");
            var implementedAsmType = compilation.GetTypeByMetadataName("SpecTrace.SpecTraceImplementedAttribute");
            if (implementsType == null && verifiesType == null) return;

            var specFiles = start.Options.AdditionalFiles.Where(SpecFiles.IsSpec).ToImmutableArray();
            if (specFiles.IsEmpty) return;

            var catalog = SpecFiles.LoadCatalog(specFiles, start.CancellationToken);
            var texts = specFiles.ToDictionary(f => f.Path, f => f.GetText(start.CancellationToken), StringComparer.Ordinal);
            var options = SpecTraceOptions.From(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);

            // spectrace.lock (opcional): versión revisada de cada requisito, para detectar cambios en la spec.
            var lockFile = start.Options.AdditionalFiles.FirstOrDefault(f =>
                string.Equals(System.IO.Path.GetFileName(f.Path), SpecLock.FileName, StringComparison.OrdinalIgnoreCase));
            var specLock = lockFile == null ? null : SpecLock.Parse(lockFile.GetText(start.CancellationToken)?.ToString() ?? string.Empty);

            var implementations = new ConcurrentBag<(string Id, string Symbol)>();
            var verifications = new ConcurrentBag<(string Id, string? Criterion, string Symbol)>();

            start.RegisterSymbolAction(ctx =>
            {
                // Un test saltado ([Fact(Skip = "...")], [Ignore], ...) no cuenta como verificación.
                var skipped = IsSkipped(ctx.Symbol);

                foreach (var attr in ctx.Symbol.GetAttributes())
                {
                    var cls = attr.AttributeClass;
                    if (cls == null) continue;

                    var isImpl = SymbolEqualityComparer.Default.Equals(cls, implementsType);
                    var isVerify = SymbolEqualityComparer.Default.Equals(cls, verifiesType);
                    if (!isImpl && !isVerify) continue;

                    var syntax = attr.ApplicationSyntaxReference?.GetSyntax(ctx.CancellationToken) as AttributeSyntax;
                    var args = syntax?.ArgumentList?.Arguments;
                    var idLocation = args is { Count: > 0 } ? args.Value[0].GetLocation() : syntax?.GetLocation() ?? Location.None;

                    if (attr.ConstructorArguments.Length == 0 || !(attr.ConstructorArguments[0].Value is string id))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotConstant, idLocation));
                        continue;
                    }

                    if (!catalog.TryGet(id, out var requirement))
                    {
                        var suggestions = SpecNaming.Closest(catalog.Requirements.Where(r => !r.IsDeprecated).Select(r => r.Id), id);
                        var props = ImmutableDictionary<string, string?>.Empty.Add(DiagnosticProperties.Suggestions, string.Join(";", suggestions));
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnknownRequirement, idLocation, props, id));
                        continue;
                    }

                    if (requirement.IsDeprecated)
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DeprecatedRequirement, idLocation, requirement.Id));
                    }

                    if (isImpl)
                    {
                        implementations.Add((requirement.Id, ctx.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                        continue;
                    }

                    var criterion = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as string : null;
                    if (criterion != null && requirement.FindCriterion(criterion) == null)
                    {
                        var critLocation = args is { Count: > 1 } ? args.Value[1].GetLocation() : idLocation;
                        var known = requirement.Criteria.Count == 0 ? "ninguno" : string.Join(", ", requirement.Criteria.Select(c => c.Id));
                        var props = ImmutableDictionary<string, string?>.Empty
                            .Add(DiagnosticProperties.Suggestions, string.Join(";", requirement.Criteria.Select(c => c.Id)));
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnknownCriterion, critLocation, props, requirement.Id, criterion, known));
                        continue;
                    }

                    if (!skipped)
                    {
                        verifications.Add((requirement.Id, requirement.FindCriterion(criterion ?? "")?.Id,
                            ctx.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                    }
                }
            }, SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property);

            start.RegisterCompilationEndAction(end =>
            {
                Location At(string path, int line) => SpecLocation(texts, path, line);

                // Los problemas de formato se reportan solo en proyectos de implementación para no duplicarlos.
                if (!options.IsTestProject)
                {
                    foreach (var issue in catalog.Issues)
                    {
                        var rule = issue.Kind == SpecIssueKind.DuplicateRequirement || issue.Kind == SpecIssueKind.DuplicateCriterion
                            ? Diagnostics.DuplicateInSpec
                            : Diagnostics.SpecFormat;
                        end.ReportDiagnostic(Diagnostic.Create(rule, At(issue.FilePath, issue.Line), issue.Message));
                    }
                }

                var implementedHere = new HashSet<string>(implementations.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);

                if (!options.IsTestProject)
                {
                    foreach (var req in catalog.Requirements.Where(r => r.IsEnforced && !implementedHere.Contains(r.Id)))
                    {
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotImplemented, At(req.FilePath, req.Line),
                            req.Id, req.Title, req.Status));
                    }

                    // ST0010: el enunciado cambió después de revisarse y este proyecto lo implementa.
                    if (specLock != null)
                    {
                        foreach (var req in catalog.Requirements.Where(r => r.IsEnforced && implementedHere.Contains(r.Id)))
                        {
                            if (!SpecDrift.Compare(req, specLock.Find(req.Id)).StatementChanged) continue;
                            var symbols = string.Join(", ", implementations.Where(i => string.Equals(i.Id, req.Id, StringComparison.OrdinalIgnoreCase))
                                .Select(i => i.Symbol).Distinct().OrderBy(n => n, StringComparer.Ordinal));
                            end.ReportDiagnostic(Diagnostic.Create(Diagnostics.StatementChanged, At(req.FilePath, req.Line), req.Id, symbols));
                        }
                    }

                    return;
                }

                // Proyecto de test: lo implementado viene de los ensamblados referenciados (y del propio proyecto).
                var implementedWhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var impl in implementations) implementedWhere[impl.Id] = impl.Symbol;

                if (implementedAsmType != null)
                {
                    foreach (var asm in end.Compilation.SourceModule.ReferencedAssemblySymbols)
                    {
                        foreach (var attr in asm.GetAttributes())
                        {
                            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, implementedAsmType)) continue;
                            if (attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is string id &&
                                !implementedWhere.ContainsKey(id))
                            {
                                implementedWhere[id] = asm.Name;
                            }
                        }
                    }
                }

                var verified = verifications.ToList();

                // ST0011: un criterio cambió después de revisarse y este proyecto tiene tests que lo verifican.
                if (specLock != null)
                {
                    foreach (var req in catalog.Requirements.Where(r => r.IsEnforced))
                    {
                        foreach (var c in SpecDrift.Compare(req, specLock.Find(req.Id)).ChangedCriteria)
                        {
                            var tests = verified
                                .Where(v => string.Equals(v.Id, req.Id, StringComparison.OrdinalIgnoreCase) && v.Criterion == c.Id)
                                .Select(v => v.Symbol).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
                            if (tests.Count == 0) continue;
                            end.ReportDiagnostic(Diagnostic.Create(Diagnostics.CriterionChanged, At(req.FilePath, c.Line),
                                req.Id, c.Id, string.Join(", ", tests)));
                        }
                    }
                }

                foreach (var req in catalog.Requirements.Where(r => r.IsEnforced && implementedWhere.ContainsKey(r.Id)))
                {
                    var forReq = verified.Where(v => string.Equals(v.Id, req.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (forReq.Count == 0)
                    {
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotVerified, At(req.FilePath, req.Line),
                            req.Id, req.Title, implementedWhere[req.Id]));
                        continue;
                    }

                    foreach (var c in req.Criteria.Where(c => !forReq.Any(v => v.Criterion == c.Id)))
                    {
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.CriterionNotVerified, At(req.FilePath, c.Line),
                            req.Id, c.Id, c.Text));
                    }
                }
            });
        }

        private static bool IsSkipped(ISymbol symbol) =>
            symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "IgnoreAttribute" ||
                a.NamedArguments.Any(n => n.Key == "Skip" && n.Value.Value is string s && s.Length > 0));

        private static Location SpecLocation(IReadOnlyDictionary<string, SourceText?> texts, string path, int line)
        {
            if (texts.TryGetValue(path, out var text) && text != null && line >= 0 && line < text.Lines.Count)
            {
                var span = text.Lines[line].Span;
                return Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
            }

            var pos = new LinePosition(Math.Max(line, 0), 0);
            return Location.Create(path, default, new LinePositionSpan(pos, pos));
        }
    }
}
