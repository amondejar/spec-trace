using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SpecTrace.Specs;

namespace SpecTrace.Tool;

public static class Reports
{
    /// <param name="linkBase">
    /// Prefix that turns paths into absolute links (e.g. https://github.com/org/repo/blob/&lt;sha&gt;/samples/Orders/),
    /// needed when the report is posted as a pull request comment.
    /// </param>
    public static string Markdown(TraceModel model, string? linkBase = null)
    {
        string Href(string file, int line) => $"{linkBase}{file}#L{line}";
        string Link(TraceLink l) => linkBase == null
            ? $"`{l.Symbol}` ({l.File}:{l.Line})"
            : $"[`{l.Symbol}`]({Href(l.File, l.Line)})";

        var sb = new StringBuilder();
        var active = model.Requirements.Where(r => r.Requirement.IsEnforced).ToList();
        var implemented = active.Count(r => r.IsImplemented);
        var verified = active.Count(r => r.IsVerified);
        var criteria = active.Sum(r => r.Requirement.Criteria.Count);
        var covered = active.Sum(r => r.CoveredCriteria);

        sb.AppendLine("# Traceability report");
        sb.AppendLine();
        sb.AppendLine($"Generated on {DateTime.Now:yyyy-MM-dd HH:mm} by SpecTrace.");
        sb.AppendLine();
        sb.AppendLine($"- Active requirements: **{active.Count}** (approved or implemented)");
        sb.AppendLine($"- Implemented: **{implemented}/{active.Count}**");
        sb.AppendLine($"- With at least one test: **{verified}/{active.Count}**");
        sb.AppendLine($"- Acceptance criteria with tests: **{covered}/{criteria}**");
        var drifted = model.Requirements.Where(t => t.HasDrift).ToList();
        sb.AppendLine(model.HasLock
            ? $"- Unreviewed spec changes: **{drifted.Count}**"
            : "- No `spectrace.lock`: run `dotnet spectrace lock` to detect spec changes");
        sb.AppendLine();
        sb.AppendLine("| Requirement | Status | Implemented in | Verified by | Criteria |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var t in model.Requirements)
        {
            var r = t.Requirement;
            var impl = t.IsImplemented ? string.Join("<br>", t.Implementations.Select(Link)) : r.IsEnforced ? "**missing**" : "–";
            var tests = t.IsVerified ? string.Join("<br>", t.Verifications.Select(v => Link(v) + (v.Criterion != null ? $" ({v.Criterion})" : ""))) : r.IsEnforced ? "**missing**" : "–";
            var crit = r.Criteria.Count == 0 ? "–" : $"{t.CoveredCriteria}/{r.Criteria.Count}";
            sb.AppendLine($"| [{r.Id}]({Href(r.FilePath, r.Line + 1)}) {Escape(r.Title)}{SourceSuffix(r)} | {r.Status} | {impl} | {tests} | {crit} |");
        }

        var gaps = model.Requirements.Where(t => t.HasGap).ToList();
        if (gaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Gaps");
            sb.AppendLine();
            foreach (var t in gaps)
            {
                var r = t.Requirement;
                if (!t.IsImplemented) sb.AppendLine($"- {r.Id} {r.Title}: not implemented");
                if (!t.IsVerified) sb.AppendLine($"- {r.Id} {r.Title}: no tests");
                else foreach (var c in t.UncoveredCriteria) sb.AppendLine($"- {r.Id} {c.Id}: no test ({c.Text})");
            }
        }

        if (drifted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Spec changes since the last review");
            sb.AppendLine();
            sb.AppendLine("The text changed after `dotnet spectrace lock`. Review what is listed and, once it is up to date, run `dotnet spectrace lock --requirement <ID>`.");
            sb.AppendLine();
            foreach (var t in drifted)
            {
                var r = t.Requirement;
                if (t.CodeNeedsReview)
                {
                    sb.AppendLine($"- [{r.Id}]({Href(r.FilePath, r.Line + 1)}) statement → review {string.Join(", ", t.Implementations.Select(Link))}");
                }

                foreach (var c in t.CriteriaNeedingReview)
                {
                    var tests = t.Verifications.Where(v => v.Criterion == c.Id).Select(Link);
                    sb.AppendLine($"- [{r.Id} {c.Id}]({Href(r.FilePath, c.Line + 1)}) \"{Escape(c.Text)}\" → review {string.Join(", ", tests)}");
                }
            }
        }

        var unlocked = model.HasLock
            ? model.Requirements.Where(t => t.Requirement.IsEnforced && !t.DriftInfo.IsLocked && (t.IsImplemented || t.IsVerified)).ToList()
            : new List<RequirementTrace>();
        if (unlocked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"No review recorded in `spectrace.lock`: {string.Join(", ", unlocked.Select(t => t.Requirement.Id))}.");
        }

        if (model.UnknownLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## References to requirements that do not exist");
            sb.AppendLine();
            foreach (var l in model.UnknownLinks) sb.AppendLine($"- `{l.RequirementId}` in {Link(l)}");
        }

        if (model.Catalog.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Spec issues");
            sb.AppendLine();
            foreach (var i in model.Catalog.Issues) sb.AppendLine($"- {i.FilePath}:{i.Line + 1} {i.Message}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// GitHub Actions workflow commands (<c>::warning file=...,line=...::message</c>):
    /// GitHub shows them as annotations in the pull request's "Files changed" tab.
    /// </summary>
    /// <param name="baseDir">Repository root (GITHUB_WORKSPACE); paths are expressed relative to it.</param>
    public static string GitHub(TraceModel model, string baseDir)
    {
        var sb = new StringBuilder();
        string Path(string relative) => TraceScanner.Relative(baseDir, System.IO.Path.Combine(model.Root, relative));

        void Emit(string level, string file, int line, string code, string title, string message) =>
            sb.Append("::").Append(level)
              .Append(" file=").Append(Property(Path(file)))
              .Append(",line=").Append(line)
              .Append(",title=").Append(Property($"{code} {title}"))
              .Append("::").AppendLine(Data(message));

        foreach (var t in model.Requirements.Where(t => t.Requirement.IsEnforced))
        {
            var r = t.Requirement;
            if (!t.IsImplemented)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0003", "Requirement not implemented",
                    $"{r.Id} ({r.Title}) is {r.Status} but nothing has [Implements(\"{r.Id}\")]");
            }
            else if (!t.IsVerified)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0004", "Requirement without tests",
                    $"{r.Id} ({r.Title}) is implemented but no test has [Verifies(\"{r.Id}\")]");
            }
            else
            {
                foreach (var c in t.UncoveredCriteria)
                {
                    Emit("notice", r.FilePath, c.Line + 1, "ST0005", "Criterion without a test", $"{r.Id} {c.Id}: {c.Text}");
                }
            }
        }

        foreach (var t in model.Requirements.Where(t => t.HasDrift))
        {
            var r = t.Requirement;
            if (t.CodeNeedsReview)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0010", "Statement changed",
                    $"The statement of {r.Id} changed after the last review. Review {string.Join(", ", t.Implementations.Select(l => l.Symbol).Distinct())} and run: dotnet spectrace lock --requirement {r.Id}");
            }

            foreach (var c in t.CriteriaNeedingReview)
            {
                var tests = t.Verifications.Where(v => v.Criterion == c.Id).Select(v => v.Symbol).Distinct();
                Emit("warning", r.FilePath, c.Line + 1, "ST0011", "Criterion changed",
                    $"Criterion {c.Id} of {r.Id} changed after the last review. Review {string.Join(", ", tests)} and run: dotnet spectrace lock --requirement {r.Id}");
            }
        }

        foreach (var l in model.UnknownLinks)
        {
            Emit("error", l.File, l.Line, "ST0001", "Unknown requirement", $"'{l.RequirementId}' does not exist in any spec");
        }

        foreach (var i in model.Catalog.Issues)
        {
            var dup = i.Kind is SpecIssueKind.DuplicateRequirement or SpecIssueKind.DuplicateCriterion;
            Emit(dup ? "error" : "warning", i.FilePath, i.Line + 1, dup ? "ST0007" : "ST0008",
                dup ? "Duplicate ID" : "Spec format", i.Message);
        }

        return sb.ToString();
    }

    // https://docs.github.com/actions/using-workflows/workflow-commands-for-github-actions
    private static string Data(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string Property(string s) => Data(s).Replace(":", "%3A").Replace(",", "%2C");

    public static string Json(TraceModel model)
    {
        var payload = new
        {
            generatedAt = DateTimeOffset.Now,
            hasLock = model.HasLock,
            requirements = model.Requirements.Select(t => new
            {
                id = t.Requirement.Id,
                title = t.Requirement.Title,
                status = t.Requirement.Status,
                source = t.Requirement.Source,
                statement = t.Requirement.Statement,
                spec = $"{t.Requirement.FilePath}:{t.Requirement.Line + 1}",
                implementedBy = t.Implementations.Select(l => new { symbol = l.Symbol, location = $"{l.File}:{l.Line}" }),
                verifiedBy = t.Verifications.Select(l => new { symbol = l.Symbol, criterion = l.Criterion, location = $"{l.File}:{l.Line}" }),
                criteria = t.Requirement.Criteria.Select(c => new
                {
                    id = c.Id,
                    text = c.Text,
                    verified = t.Verifications.Any(v => v.Criterion == c.Id),
                }),
                gap = t.HasGap,
                drift = new
                {
                    locked = t.DriftInfo.IsLocked,
                    statementChanged = t.DriftInfo.StatementChanged,
                    criteriaChanged = t.DriftInfo.ChangedCriteria.Select(c => c.Id),
                    pending = t.HasDrift,
                },
            }),
            unknownReferences = model.UnknownLinks.Select(l => new { id = l.RequirementId, location = $"{l.File}:{l.Line}" }),
            specIssues = model.Catalog.Issues.Select(i => new { file = i.FilePath, line = i.Line + 1, kind = i.Kind.ToString(), message = i.Message }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Requirement context for a coding agent: what to build, what already exists, what is missing and how to
    /// mark the work.
    /// </summary>
    public static string Context(TraceModel model, string id)
    {
        var t = model.Requirements.FirstOrDefault(r => string.Equals(r.Requirement.Id, id, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            var known = string.Join(", ", model.Requirements.Select(r => r.Requirement.Id));
            throw new ArgumentException($"Requirement '{id}' does not exist. Available requirements: {known}");
        }

        var r = t.Requirement;
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Id}: {r.Title}");
        sb.AppendLine();
        sb.AppendLine($"Status: {r.Status} · Spec: {r.FilePath}:{r.Line + 1}" + (r.Source != null ? $" · Source: {r.Source}" : ""));
        sb.AppendLine();
        if (t.HasDrift || (t.DriftInfo.Any && t.Requirement.IsEnforced))
        {
            sb.AppendLine("## Changes since the last review");
            sb.AppendLine();
            if (t.DriftInfo.StatementChanged)
            {
                sb.AppendLine(t.IsImplemented
                    ? $"- The statement changed: check that {string.Join(", ", t.Implementations.Select(Link))} still satisfies it."
                    : "- The statement changed (there is no implementation yet).");
            }

            foreach (var c in t.DriftInfo.ChangedCriteria)
            {
                var tests = t.Verifications.Where(v => v.Criterion == c.Id).ToList();
                sb.AppendLine(tests.Count > 0
                    ? $"- {c.Id} changed: review {string.Join(", ", tests.Select(Link))}."
                    : $"- {c.Id} changed (it has no test yet).");
            }

            sb.AppendLine("- Once a person has reviewed the code and the tests, it is recorded with `dotnet spectrace lock --requirement " + r.Id + "`. Do not run it yourself without their approval.");
            sb.AppendLine();
        }

        sb.AppendLine("## Statement");
        sb.AppendLine();
        sb.AppendLine(r.Statement.Length > 0 ? r.Statement : "(no statement)");
        sb.AppendLine();
        sb.AppendLine("## Acceptance criteria");
        sb.AppendLine();
        foreach (var c in r.Criteria)
        {
            var tests = t.Verifications.Where(v => v.Criterion == c.Id).ToList();
            var state = tests.Count > 0 ? "tested by: " + string.Join(", ", tests.Select(Link)) : "NO TEST";
            sb.AppendLine($"- {c.Id}: {c.Text} — {state}");
        }

        sb.AppendLine();
        sb.AppendLine("## Current implementation");
        sb.AppendLine();
        if (t.IsImplemented) foreach (var l in t.Implementations) sb.AppendLine($"- {Link(l)}");
        else sb.AppendLine("- None yet.");
        sb.AppendLine();
        AppendCodeContext(sb, model, t);
        sb.AppendLine("## How to mark the work");
        sb.AppendLine();
        sb.AppendLine($"- On the code that implements it: `[Implements(Requirements.{r.Identifier})]`");
        foreach (var c in r.Criteria)
        {
            sb.AppendLine($"- On the test for {c.Id}: `[Verifies(Requirements.{r.Identifier}, AcceptanceCriteria.{r.Identifier}.{SpecNaming.ToIdentifier(c.Id)})]`");
        }

        sb.AppendLine("- Check with `dotnet build`: no ST0003/ST0004/ST0005 should remain for this requirement.");
        return sb.ToString();
    }

    /// <summary>Files that already work with requirements of the same spec, with the IDs they implement or verify.</summary>
    public static IReadOnlyList<(string File, LinkKind Kind, IReadOnlyList<string> Ids)> RelatedFiles(TraceModel model, RequirementTrace t)
    {
        var sameSpec = model.Requirements.Where(x => x.Requirement.FilePath == t.Requirement.FilePath).ToList();
        return sameSpec
            .SelectMany(x => x.Implementations.Concat(x.Verifications))
            .GroupBy(l => (l.File, l.Kind))
            .Select(g => (g.Key.File, g.Key.Kind, (IReadOnlyList<string>)g.Select(l => l.RequirementId).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList()))
            // Files of this requirement first; then implementation before tests.
            .OrderByDescending(f => f.Item3.Contains(t.Requirement.Id))
            .ThenBy(f => f.Kind)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Where the code goes and what to read before writing it: projects with their catalog namespace, and related files.
    /// Without this, an agent that cannot explore the repository invents types and namespaces.
    /// </summary>
    private static void AppendCodeContext(StringBuilder sb, TraceModel model, RequirementTrace t)
    {
        var related = RelatedFiles(model, t);
        sb.AppendLine("## Where the code goes");
        sb.AppendLine();

        IEnumerable<ProjectInfo> ProjectsFor(LinkKind kind, bool isTest)
        {
            var used = related.Where(f => f.Kind == kind).Select(f => model.ProjectOf(f.File)).Where(p => p != null && p.IsTest == isTest).Distinct().ToList();
            return used.Count > 0 ? used! : model.Projects.Where(p => p.IsTest == isTest);
        }

        var impl = ProjectsFor(LinkKind.Implements, isTest: false).ToList();
        var tests = ProjectsFor(LinkKind.Verifies, isTest: true).ToList();
        if (impl.Count == 0 && tests.Count == 0)
        {
            sb.AppendLine("- No .csproj was found in the code folders.");
        }

        foreach (var p in impl) sb.AppendLine($"- Implementation in `{p.Name}` ({p.Directory}): `using SpecTrace;` and `using {p.CatalogNamespace};`");
        foreach (var p in tests) sb.AppendLine($"- Tests in `{p.Name}` ({p.Directory}): `using SpecTrace;` and `using {p.CatalogNamespace};`");
        sb.AppendLine("- `Requirements` and `AcceptanceCriteria` are generated per project (internal): use each project's namespace.");
        sb.AppendLine();

        sb.AppendLine("## Related code");
        sb.AppendLine();
        if (related.Count == 0)
        {
            sb.AppendLine("- No file works with requirements of this spec yet. Look up the domain types with get_code (type).");
        }
        else
        {
            sb.AppendLine("Files that already work with requirements of this spec. Read them (get_code) before proposing changes and reuse their types:");
            sb.AppendLine();
            foreach (var (file, kind, ids) in related)
            {
                sb.AppendLine($"- {file} — {(kind == LinkKind.Implements ? "implements" : "verifies")} {string.Join(", ", ids)}");
            }
        }

        sb.AppendLine();
    }

    private static string Link(TraceLink l) => $"`{l.Symbol}` ({l.File}:{l.Line})";

    /// <summary>" · [#3](url)" after the title when the requirement has a Source: line.</summary>
    private static string SourceSuffix(SpecRequirement r)
    {
        if (r.Source == null) return "";
        if (!Uri.TryCreate(r.Source, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return $" · {Escape(r.Source)}";
        }

        // .../issues/12 → #12 (GitHub); otherwise the last segment of the address (a Jira key such as RB-42).
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var label = segments.Length >= 2 && segments[^2] == "issues" ? "#" + segments[^1]
            : segments.Length > 0 && segments[^1].Length > 0 ? segments[^1]
            : uri.Host;
        return $" · [{Escape(label)}]({uri.AbsoluteUri})";
    }

    private static string Escape(string text) => text.Replace("|", "\\|");
}
