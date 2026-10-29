using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpecTrace.Tool.Results;

/// <summary>How many mutants in a requirement's code each criterion's tests detect.</summary>
public sealed record CriterionKills(string CriterionId, int Detected);

/// <summary>
/// Mutation testing seen per requirement: the mutants inside the members with [Implements] for it, how many its tests
/// detect, and which ones survive.
/// </summary>
/// <param name="Exact">
/// The report lists every test that detects each mutant (Stryker with --disable-bail). Otherwise only the first failing
/// test is known, and a criterion whose tests are never first may still detect the changes.
/// </param>
public sealed record RequirementMutation(IReadOnlyList<Mutant> Mutants, IReadOnlyList<CriterionKills> Criteria, bool HasKillData, bool Exact = false)
{
    public int Detected => Mutants.Count(m => m.Detected);
    public int Undetected => Mutants.Count(m => m.Undetected);
    public int Total => Detected + Undetected;

    /// <summary>Percentage of mutants detected, or null when there are none in the requirement's code.</summary>
    public double? Score => Total == 0 ? null : Math.Floor(100.0 * Detected / Total);

    public IEnumerable<Mutant> Survivors => Mutants.Where(m => m.Undetected).OrderBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.StartLine);

    /// <summary>Criteria with tests that detect none of the requirement's mutants (only known when the report says who killed what).</summary>
    public IEnumerable<string> CriteriaDetectingNothing => HasKillData ? Criteria.Where(c => c.Detected == 0).Select(c => c.CriterionId) : Enumerable.Empty<string>();
}

public static class MutationAnalysis
{
    public static RequirementMutation? For(TraceModel model, RequirementTrace trace)
    {
        if (model.Mutations == null) return null;

        // Mutants inside a member (or type) marked [Implements] for this requirement. Errors, ignored and pending
        // mutants say nothing about the tests and are left out, as in Stryker's own score.
        var mutants = model.Mutations.Mutants
            .Where(m => (m.Detected || m.Undetected) && trace.Implementations.Any(l =>
                m.StartLine >= l.SpanStart && m.StartLine <= l.SpanEnd && MutationReport.SameFile(m.File, l.File)))
            .GroupBy(m => (m.File, m.Id, m.StartLine))
            .Select(g => g.First())
            .ToList();

        var hasKillData = mutants.Any(m => m.KilledBy.Count > 0);
        var criteria = trace.Requirement.Criteria
            .Select(c => (c.Id, Tests: trace.Verifications.Where(v => v.Criterion == c.Id).Select(v => v.Symbol).ToList()))
            .Where(c => c.Tests.Count > 0)
            .Select(c => new CriterionKills(c.Id, mutants.Count(m => m.Detected && m.KilledBy.Any(k => c.Tests.Any(t => MutationReport.SameTest(k, t))))))
            .ToList();

        return new RequirementMutation(mutants, criteria, hasKillData, model.Mutations.ListsEveryKiller);
    }

    /// <summary>
    /// The newest mutation-report.json in a StrykerOutput folder of the repository (Stryker.NET writes it where it
    /// runs, usually the test project), or null. Used by the MCP tools, which have no command line to receive it.
    /// </summary>
    public static MutationReport? Discover(SpecTraceConfig config)
    {
        var folders = new List<string> { Path.Combine(config.Root, "StrykerOutput") };
        foreach (var source in config.Sources.Select(s => Path.Combine(config.Root, s)).Where(Directory.Exists))
        {
            folders.AddRange(Directory.EnumerateDirectories(source, "StrykerOutput", SearchOption.AllDirectories)
                .Where(d => !d.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules")));
        }

        var newest = folders.Select(MutationReport.Newest).Where(f => f != null).OrderByDescending(f => File.GetLastWriteTimeUtc(f!)).FirstOrDefault();
        if (newest == null) return null;
        try
        {
            return MutationReport.Load(newest);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The line about criteria whose tests detect none of the mutants, worded for what the report can tell.</summary>
    public static string? SilentCriteriaLine(RequirementMutation m)
    {
        var silent = m.CriteriaDetectingNothing.ToList();
        if (silent.Count == 0) return null;
        var list = string.Join(", ", silent);
        return m.Exact
            ? $"The tests of {list} detect none of the changes in this code."
            : $"The tests of {list} were never the first to fail with these changes. Stryker stops at the first failing test: " +
              "run it with --disable-bail to know whether they detect any.";
    }

    public static string Describe(Mutant m) =>
        $"{m.MutatorName}" + (m.Replacement != null ? $": `{OneLine(m.Replacement)}`" : "") +
        (m.Status == MutantStatus.NoCoverage ? " (no test runs this code)" : " (survived)");

    private static string OneLine(string text)
    {
        var s = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
    }
}
