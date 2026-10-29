using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Automation;

/// <summary>An approved requirement that still has work to do, and what is missing.</summary>
/// <param name="Changes">What changed in its spec since the reference given (empty without one).</param>
public sealed record PendingWork(RequirementTrace Trace, IReadOnlyList<string> Missing, IReadOnlyList<string> Changes)
{
    public SpecRequirement Requirement => Trace.Requirement;
}

/// <summary>
/// The work queue of the spec: requirements a person approved whose implementation or tests are not there yet.
/// An agent (or a person) takes one, and the build tells when it is done. With a reference (the commit before a merge),
/// only the requirements whose spec changed since then: what the merge just approved or extended.
/// </summary>
public static class WorkQueue
{
    public static IReadOnlyList<PendingWork> Compute(SpecTraceConfig config, TraceModel model, string? since = null,
        IReadOnlyCollection<string>? only = null)
    {
        if (only is { Count: > 0 })
        {
            foreach (var id in only.Where(id => !model.Catalog.TryGet(id, out _)))
            {
                var closest = SpecNaming.Closest(model.Catalog.Requirements.Select(r => r.Id), id);
                throw new ArgumentException($"Requirement '{id}' does not exist." +
                                            (closest.Count > 0 ? $" Did you mean {string.Join(", ", closest)}?" : ""));
            }
        }

        Dictionary<string, IReadOnlyList<string>>? changed = null;
        if (since != null)
        {
            changed = Impact.Compute(config, model, since).Touched
                .Where(t => t.SpecChanges.Count > 0)
                .ToDictionary(t => t.Requirement.Id, t => t.SpecChanges, StringComparer.OrdinalIgnoreCase);
        }

        return model.Requirements
            .Where(t => t.HasGap)
            .Where(t => only is not { Count: > 0 } || only.Contains(t.Requirement.Id, StringComparer.OrdinalIgnoreCase))
            .Where(t => changed == null || changed.ContainsKey(t.Requirement.Id))
            .Select(t => new PendingWork(t, Missing(t),
                changed != null && changed.TryGetValue(t.Requirement.Id, out var c) ? c : Array.Empty<string>()))
            .ToList();
    }

    /// <summary>"implementation", "tests", "tests for AC-2, AC-3".</summary>
    public static IReadOnlyList<string> Missing(RequirementTrace t)
    {
        var missing = new List<string>();
        if (!t.IsImplemented) missing.Add("implementation");
        var uncovered = t.UncoveredCriteria.Select(c => c.Id).ToList();
        if (!t.IsVerified) missing.Add("tests");
        else if (uncovered.Count > 0) missing.Add("tests for " + string.Join(", ", uncovered));
        return missing;
    }

    public static string Markdown(IReadOnlyList<PendingWork> work, string? since)
    {
        var sb = new StringBuilder();
        var scope = since != null ? $" whose spec changed since {Short(since)}" : "";
        if (work.Count == 0)
        {
            sb.Append($"No approved requirement{scope} is waiting for implementation or tests.");
            return sb.ToString();
        }

        sb.AppendLine($"Approved requirements{scope} waiting for work ({work.Count}):");
        sb.AppendLine();
        foreach (var w in work)
        {
            var r = w.Requirement;
            var changes = w.Changes.Count > 0 ? $"; spec: {string.Join(", ", w.Changes)}" : "";
            sb.AppendLine($"- {r.Id} {r.Title} ({r.FilePath}:{r.Line + 1}): missing {string.Join(", ", w.Missing)}{changes}");
        }

        sb.AppendLine();
        sb.Append("Implement one with an agent: dotnet spectrace prompt implement_requirement <ID>, or /mcp__spectrace__implement_requirement in Claude Code.");
        return sb.ToString();
    }

    public static object Json(IReadOnlyList<PendingWork> work) => work.Select(w => new
    {
        id = w.Requirement.Id,
        title = w.Requirement.Title,
        status = w.Requirement.Status,
        spec = $"{w.Requirement.FilePath}:{w.Requirement.Line + 1}",
        source = w.Requirement.Source,
        missing = w.Missing,
        criteria = w.Requirement.Criteria.Select(c => new
        {
            id = c.Id,
            tested = w.Trace.Verifications.Any(v => v.Criterion == c.Id),
        }),
        specChanges = w.Changes,
    }).ToList();

    /// <summary>One ID per line, for shell loops in CI.</summary>
    public static string Ids(IReadOnlyList<PendingWork> work) => string.Join("\n", work.Select(w => w.Requirement.Id));

    private static string Short(string reference) =>
        reference.Length == 40 && reference.All(Uri.IsHexDigit) ? reference.Substring(0, 7) : reference;
}
