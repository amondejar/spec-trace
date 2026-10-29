using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SpecTrace.Specs;

namespace SpecTrace.Tool;

/// <summary>A requirement that a change touches: in its spec text, its implementation or its tests.</summary>
public sealed record RequirementImpact(SpecRequirement Requirement, IReadOnlyList<string> SpecChanges, IReadOnlyList<TraceLink> Code, IReadOnlyList<TraceLink> Tests);

public sealed record ImpactReport(string Base, IReadOnlyList<RequirementImpact> Touched, IReadOnlyList<SpecRequirement> Removed, IReadOnlyList<string> UntracedFiles)
{
    public bool Any => Touched.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Which requirements a change touches, compared with a base branch: requirements whose spec changed, and members with
/// [Implements] or tests with [Verifies] on lines the change adds, edits or removes. Reviewers see the change in terms
/// of behaviour, and the product owner knows what to check again.
/// </summary>
public static class Impact
{
    public static ImpactReport Compute(SpecTraceConfig config, TraceModel model, string? baseRef)
    {
        var git = Git.Open(config.Root) ?? throw new ArgumentException("The impact of a change needs a git repository (and git on the PATH).");
        var @base = baseRef ?? git.DefaultBase() ?? throw new ArgumentException("No base branch found (origin/main, origin/master, main or master): use --base <ref>.");
        if (!git.RefExists(@base)) throw new ArgumentException($"'{@base}' is not a commit or branch of this repository. In CI, fetch it first: git fetch origin <branch>.");

        // A branch compares with where it started; a pull request's merge commit (shallow, without that history)
        // compares with the base directly, which is the same thing.
        string since;
        try
        {
            since = git.MergeBase(@base, "HEAD");
        }
        catch (InvalidOperationException)
        {
            since = @base;
        }

        var prefix = git.Prefix;
        var changed = git.ChangedLines(since);

        // The spec as it was on the base, to say what changed in each requirement.
        var before = new Dictionary<string, SpecRequirement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in git.ListFiles(since, prefix + config.Specs.Replace('\\', '/').TrimEnd('/')).Where(SpecParser.IsSpecFile))
        {
            var relative = path.StartsWith(prefix, StringComparison.Ordinal) ? path.Substring(prefix.Length) : path;
            foreach (var r in SpecParser.Parse(relative, git.Show(since, path)).Requirements) before[r.Id] = r;
        }

        bool Touches(TraceLink l) =>
            changed.TryGetValue(prefix + l.File, out var lines) && lines.Any(n => n >= l.SpanStart && n <= l.SpanEnd);

        var touched = new List<RequirementImpact>();
        foreach (var t in model.Requirements)
        {
            before.TryGetValue(t.Requirement.Id, out var old);
            var spec = SpecChanges(old, t.Requirement);
            var code = t.Implementations.Where(Touches).ToList();
            var tests = t.Verifications.Where(Touches).ToList();
            if (spec.Count > 0 || code.Count > 0 || tests.Count > 0) touched.Add(new RequirementImpact(t.Requirement, spec, code, tests));
        }

        var removed = before.Values.Where(r => !model.Catalog.TryGet(r.Id, out _)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

        // Changed code that no [Implements] or [Verifies] covers: nothing says which requirement it serves.
        var links = model.Requirements.SelectMany(t => t.Implementations.Concat(t.Verifications)).Concat(model.UnknownLinks).ToList();
        var untraced = TraceScanner.SourceFiles(config)
            .Select(f => TraceScanner.Relative(config.Root, f))
            .Where(f => changed.TryGetValue(prefix + f, out var lines) &&
                        lines.Any(n => !links.Any(l => l.File == f && n >= l.SpanStart && n <= l.SpanEnd) && n <= LineCount(Path.Combine(config.Root, f))))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new ImpactReport(@base, touched, removed, untraced);
    }

    /// <summary>"new", "statement", "AC-2 changed", "AC-3 added", "status proposed → approved"...</summary>
    public static IReadOnlyList<string> SpecChanges(SpecRequirement? before, SpecRequirement now)
    {
        if (before == null) return new[] { "new" };
        var changes = new List<string>();
        if (before.Status != now.Status) changes.Add($"status {before.Status} → {now.Status}");
        if (SpecFingerprint.Of(before.Statement) != SpecFingerprint.Of(now.Statement)) changes.Add("statement changed");
        foreach (var c in now.Criteria)
        {
            var old = before.FindCriterion(c.Id);
            if (old == null) changes.Add($"{c.Id} added");
            else if (SpecFingerprint.Of(old.Text) != SpecFingerprint.Of(c.Text)) changes.Add($"{c.Id} changed");
        }

        changes.AddRange(before.Criteria.Where(c => now.FindCriterion(c.Id) == null).Select(c => $"{c.Id} removed"));
        if (changes.Count == 0 && !string.Equals(before.Title, now.Title, StringComparison.Ordinal)) changes.Add("title");
        return changes;
    }

    public static string Markdown(ImpactReport impact, Func<string, int, string>? href = null)
    {
        string Link(TraceLink l) => href == null ? $"`{l.Symbol}`" : $"[`{l.Symbol}`]({href(l.File, l.Line)})";

        var sb = new StringBuilder();
        sb.AppendLine("## Requirements touched by this change");
        sb.AppendLine();
        if (!impact.Any)
        {
            sb.AppendLine($"Compared with `{impact.Base}`: no requirement is touched.");
        }
        else
        {
            sb.AppendLine($"Compared with `{impact.Base}`: **{impact.Touched.Count}** requirement(s) touched" +
                          (impact.Removed.Count > 0 ? $", **{impact.Removed.Count}** removed." : "."));
            if (impact.Touched.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Requirement | Spec | Code | Tests | Source |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var i in impact.Touched)
                {
                    var r = i.Requirement;
                    var title = href == null ? $"{r.Id} {Escape(r.Title)}" : $"[{r.Id}]({href(r.FilePath, r.Line + 1)}) {Escape(r.Title)}";
                    var spec = i.SpecChanges.Count == 0 ? "–" : string.Join(", ", i.SpecChanges);
                    var code = i.Code.Count == 0 ? "–" : string.Join("<br>", i.Code.Select(Link).Distinct());
                    var tests = i.Tests.Count == 0 ? "–" : string.Join("<br>", i.Tests.Select(t => Link(t) + (t.Criterion != null ? $" ({t.Criterion})" : "")).Distinct());
                    sb.AppendLine($"| {title} | {spec} | {code} | {tests} | {SourceLabel(r.Source)} |");
                }
            }

            if (impact.Removed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Removed from the specs: " + string.Join(", ", impact.Removed.Select(r => $"{r.Id} {r.Title}")) + ".");
            }
        }

        if (impact.UntracedFiles.Count > 0)
        {
            sb.AppendLine();
            var shown = impact.UntracedFiles.Take(10).Select(f => $"`{f}`");
            sb.AppendLine("Code changed outside any member with `[Implements]` or `[Verifies]`: " + string.Join(", ", shown) +
                          (impact.UntracedFiles.Count > 10 ? $" and {impact.UntracedFiles.Count - 10} more" : "") + ".");
        }

        return sb.ToString();
    }

    public static object Json(ImpactReport impact) => new
    {
        @base = impact.Base,
        touched = impact.Touched.Select(i => new
        {
            id = i.Requirement.Id,
            title = i.Requirement.Title,
            source = i.Requirement.Source,
            spec = i.SpecChanges,
            code = i.Code.Select(l => new { symbol = l.Symbol, location = $"{l.File}:{l.Line}" }),
            tests = i.Tests.Select(l => new { symbol = l.Symbol, criterion = l.Criterion, location = $"{l.File}:{l.Line}" }),
        }),
        removed = impact.Removed.Select(r => new { id = r.Id, title = r.Title }),
        untracedFiles = impact.UntracedFiles,
    };

    private static string SourceLabel(string? source)
    {
        if (source == null) return "–";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return Escape(source);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var label = segments.Length >= 2 && segments[^2] == "issues" ? "#" + segments[^1] : segments.LastOrDefault() ?? uri.Host;
        return $"[{Escape(label)}]({uri.AbsoluteUri})";
    }

    private static int LineCount(string path) => File.Exists(path) ? File.ReadAllLines(path).Length + 1 : 0;

    private static string Escape(string text) => text.Replace("|", "\\|");
}
