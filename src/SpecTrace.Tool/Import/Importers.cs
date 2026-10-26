using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SpecTrace.Specs;
using SpecTrace.Tool.Authoring;

namespace SpecTrace.Tool.Import;

/// <summary>A *.spec.md file that an importer is going to write.</summary>
public sealed record ImportedSpec(string OutputPath, string SpecId, string Title, string Source, IReadOnlyList<RequirementDraft> Requirements);

public sealed record ImportResult(string OutputPath, int Requirements, int Added, int Kept);

/// <summary>
/// Common rules when reimporting: the source wins for titles and text; the SpecTrace file wins for statuses
/// (someone may have approved or deprecated a requirement) and for criteria added by hand to requirements the source does not detail.
/// </summary>
public static class ImportWriter
{
    public static ImportResult Write(ImportedSpec spec, bool dryRun = false)
    {
        var existing = File.Exists(spec.OutputPath)
            ? SpecParser.Parse(spec.OutputPath, File.ReadAllText(spec.OutputPath)).Requirements.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SpecRequirement>(StringComparer.OrdinalIgnoreCase);

        var merged = new List<RequirementDraft>();
        int added = 0, kept = 0;
        foreach (var draft in spec.Requirements)
        {
            if (existing.TryGetValue(draft.Id, out var old))
            {
                kept++;
                var criteria = draft.Criteria.Count == 0 && old.Criteria.Count > 0 ? old.Criteria.Select(c => c.Text).ToList() : draft.Criteria;
                var draftNote = draft.Criteria.Count == 0 && old.Criteria.Count > 0 ? null : draft.Note;
                merged.Add(draft with { Status = old.Status, Criteria = criteria, Note = draftNote });
            }
            else
            {
                added++;
                merged.Add(draft);
            }
        }

        var note = $"Imported from `{spec.Source}` with `dotnet spectrace import`. Edit the source and import again: " +
                   "IDs, statuses and any criteria you add here to requirements without criteria are kept.";
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);
            File.WriteAllText(spec.OutputPath, SpecWriter.RenderDocument(spec.SpecId, spec.Title, note, merged));
        }

        return new ImportResult(spec.OutputPath, merged.Count, added, kept);
    }

    internal static string StripMarkdown(string text) =>
        Regex.Replace(Regex.Replace(text, @"\*\*|__|`", ""), @"\s+", " ").Trim();

    internal static string ShortTitle(string text, int words = 8)
    {
        var plain = StripMarkdown(text);
        plain = Regex.Replace(plain, @"^(The\s+)?(System|Users?)\s+(MUST|SHALL|SHOULD)(\s+be\s+able\s+to)?\s+", "", RegexOptions.IgnoreCase);
        var parts = plain.TrimEnd('.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', parts.Take(words)) + (parts.Length > words ? "…" : "");
        return title.Length > 0 ? char.ToUpperInvariant(title[0]) + title[1..] : "Untitled";
    }
}

/// <summary>
/// Imports specs from GitHub Spec Kit (<c>specs/NNN-feature/spec.md</c>).
/// User stories → requirements with their Acceptance Scenarios as criteria.
/// Functional Requirements (FR-NNN) → proposed requirements (Spec Kit gives them no criteria of their own).
/// </summary>
public static class SpecKitImporter
{
    private static readonly Regex FeatureTitle = new(@"^#\s+Feature Specification:\s*(?<t>.+)$", RegexOptions.Compiled);
    private static readonly Regex UserStory = new(@"^###\s+User Story\s+(?<n>\d+)\s*[-–—:]\s*(?<t>.+?)(?:\s*\(Priority:\s*(?<p>P\d+)\))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Scenario = new(@"^\s*\d+\.\s+(?<s>.*\bGiven\b.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Functional = new(@"^\s*[-*]\s+\*\*(?<id>FR-\d+)\*\*\s*:\s*(?<t>.+)$", RegexOptions.Compiled);
    private static readonly Regex AnyHeading = new(@"^#{1,6}\s", RegexOptions.Compiled);

    public static IEnumerable<string> FindSources(string path)
    {
        if (File.Exists(path)) return new[] { Path.GetFullPath(path) };
        if (!Directory.Exists(path)) throw new ArgumentException($"No existe '{path}'.");
        return Directory.EnumerateFiles(path, "spec.md", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git" or "node_modules"))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    public static ImportedSpec Parse(string sourcePath, string text, string outputPath, string? prefix = null, string status = SpecStatus.Approved, string? sourceLabel = null)
    {
        var feature = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sourcePath))) ?? "feature";
        prefix ??= DerivePrefix(feature);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var title = feature;
        var drafts = new List<RequirementDraft>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = FeatureTitle.Match(line);
            if (t.Success)
            {
                title = ImportWriter.StripMarkdown(t.Groups["t"].Value);
                continue;
            }

            var us = UserStory.Match(line);
            if (us.Success)
            {
                // Description: the text between the heading and the first bold block (**Why this priority**, ...).
                var description = new List<string>();
                var scenarios = new List<string>();
                var inDescription = true;
                var j = i + 1;
                for (; j < lines.Length && !AnyHeading.IsMatch(lines[j]); j++)
                {
                    var l = lines[j].Trim();
                    var sc = Scenario.Match(lines[j]);
                    if (sc.Success)
                    {
                        scenarios.Add(ImportWriter.StripMarkdown(sc.Groups["s"].Value));
                        inDescription = false;
                    }
                    else if (l.StartsWith("**", StringComparison.Ordinal))
                    {
                        inDescription = false;
                    }
                    else if (inDescription && l.Length > 0)
                    {
                        description.Add(l);
                    }
                }

                var statement = ImportWriter.StripMarkdown(string.Join(" ", description));
                var priority = us.Groups["p"].Success ? us.Groups["p"].Value.ToUpperInvariant() : null;
                if (priority != null) statement = (statement.Length > 0 ? statement + " " : "") + $"(Priority {priority})";
                var unclear = statement.Contains("NEEDS CLARIFICATION", StringComparison.OrdinalIgnoreCase) || scenarios.Any(s => s.Contains("NEEDS CLARIFICATION", StringComparison.OrdinalIgnoreCase));

                drafts.Add(new RequirementDraft(
                    $"{prefix}-US-{us.Groups["n"].Value}",
                    ImportWriter.StripMarkdown(us.Groups["t"].Value),
                    unclear || scenarios.Count == 0 ? SpecStatus.Proposed : status,
                    statement.Length > 0 ? statement : "(no description in the source spec)",
                    scenarios,
                    unclear ? "The source spec has [NEEDS CLARIFICATION] markers: resolve them before approving it." : null));
                i = j - 1;
                continue;
            }

            var fr = Functional.Match(line);
            if (fr.Success)
            {
                var body = ImportWriter.StripMarkdown(fr.Groups["t"].Value);
                drafts.Add(new RequirementDraft(
                    $"{prefix}-{fr.Groups["id"].Value}",
                    ImportWriter.ShortTitle(body),
                    SpecStatus.Proposed,
                    body,
                    Array.Empty<string>(),
                    "Spec Kit functional requirement without criteria of its own: add criteria (- AC-1: ...) and approve it to enforce it."));
            }
        }

        return new ImportedSpec(outputPath, prefix, title, sourceLabel ?? sourcePath, drafts);
    }

    /// <summary>"001-photo-albums" → "PA1"; "checkout" → "C".</summary>
    public static string DerivePrefix(string featureFolder)
    {
        var m = Regex.Match(featureFolder, @"^(?<n>\d+)[-_]?(?<name>.*)$");
        var number = m.Success ? m.Groups["n"].Value.TrimStart('0') : "";
        var name = m.Success ? m.Groups["name"].Value : featureFolder;
        var initials = string.Concat(name.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetter(w[0]))
            .Select(w => char.ToUpperInvariant(w[0])));
        var prefix = (initials.Length > 0 ? initials : "F") + number;
        return prefix;
    }
}

/// <summary>
/// Imports specs from OpenSpec (<c>openspec/specs/&lt;capability&gt;/spec.md</c>).
/// Each "### Requirement:" is a requirement; each "#### Scenario:" an acceptance criterion.
/// OpenSpec has no IDs: they are assigned in order and reused by title when reimporting.
/// </summary>
public static class OpenSpecImporter
{
    private static readonly Regex RequirementHeading = new(@"^###\s+Requirement:\s*(?<t>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ScenarioHeading = new(@"^####\s+Scenario:\s*(?<t>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Step = new(@"^\s*[-*]\s+(?<s>.+)$", RegexOptions.Compiled);
    private static readonly Regex AnyHeading = new(@"^(?<h>#{1,6})\s", RegexOptions.Compiled);

    public static IEnumerable<(string Capability, string Path)> FindSources(string path)
    {
        if (File.Exists(path))
        {
            var full = Path.GetFullPath(path);
            return new[] { (Path.GetFileName(Path.GetDirectoryName(full)) ?? "spec", full) };
        }

        if (!Directory.Exists(path)) throw new ArgumentException($"No existe '{path}'.");
        var specsRoot = Directory.Exists(Path.Combine(path, "specs")) && !File.Exists(Path.Combine(path, "spec.md"))
            ? Path.Combine(path, "specs")
            : path;
        // Current specs only: openspec/changes/ holds proposals (deltas) that are not archived yet.
        return Directory.EnumerateFiles(specsRoot, "spec.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}changes{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(Path.GetDirectoryName(f)) ?? "spec", f));
    }

    public static ImportedSpec Parse(string capability, string sourcePath, string text, string outputPath,
        string? prefix = null, string status = SpecStatus.Approved, string? sourceLabel = null)
    {
        prefix ??= SpecWriter.ToPrefix(capability);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var title = capability;
        var parsed = new List<(string Title, string Statement, List<string> Criteria)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var h1 = Regex.Match(lines[i], @"^#\s+(?<t>.+)$");
            if (h1.Success && parsed.Count == 0) title = ImportWriter.StripMarkdown(h1.Groups["t"].Value);

            var req = RequirementHeading.Match(lines[i]);
            if (!req.Success) continue;

            var statement = new List<string>();
            var criteria = new List<string>();
            string? scenario = null;
            var steps = new List<string>();

            void FlushScenario()
            {
                if (scenario == null) return;
                criteria.Add(steps.Count > 0 ? $"{scenario}: {string.Join(", ", steps)}" : scenario);
                scenario = null;
                steps.Clear();
            }

            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var heading = AnyHeading.Match(lines[j]);
                if (heading.Success && heading.Groups["h"].Value.Length <= 3) break;

                var sc = ScenarioHeading.Match(lines[j]);
                if (sc.Success)
                {
                    FlushScenario();
                    scenario = ImportWriter.StripMarkdown(sc.Groups["t"].Value);
                    continue;
                }

                var step = Step.Match(lines[j]);
                if (scenario != null && step.Success)
                {
                    steps.Add(ImportWriter.StripMarkdown(step.Groups["s"].Value));
                }
                else if (scenario == null && lines[j].Trim().Length > 0 && !heading.Success)
                {
                    statement.Add(lines[j].Trim());
                }
            }

            FlushScenario();
            parsed.Add((ImportWriter.StripMarkdown(req.Groups["t"].Value), ImportWriter.StripMarkdown(string.Join(" ", statement)), criteria));
            i = j - 1;
        }

        // Stable IDs: reuse the ID of a requirement with the same title in the already imported file.
        var previous = File.Exists(outputPath)
            ? SpecParser.Parse(outputPath, File.ReadAllText(outputPath)).Requirements
                .GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new List<string>(previous.Values);

        var drafts = new List<RequirementDraft>();
        foreach (var (reqTitle, statement, criteria) in parsed)
        {
            if (!previous.TryGetValue(reqTitle, out var id))
            {
                id = SpecWriter.NextId(used, prefix);
                used.Add(id);
            }

            drafts.Add(new RequirementDraft(id, reqTitle, criteria.Count == 0 ? SpecStatus.Proposed : status,
                statement.Length > 0 ? statement : "(no statement in the source spec)", criteria));
        }

        return new ImportedSpec(outputPath, prefix, title, sourceLabel ?? sourcePath, drafts);
    }
}
