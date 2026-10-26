using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SpecTrace.Specs;
using SpecTrace.Tool.Authoring;

namespace SpecTrace.Tool.Import;

/// <summary>Un fichero *.spec.md que va a escribir un importador.</summary>
public sealed record ImportedSpec(string OutputPath, string SpecId, string Title, string Source, IReadOnlyList<RequirementDraft> Requirements);

public sealed record ImportResult(string OutputPath, int Requirements, int Added, int Kept);

/// <summary>
/// Reglas comunes al reimportar: la fuente manda en títulos y textos; el fichero de SpecTrace manda en los estados
/// (alguien pudo aprobar o retirar un requisito) y en los criterios añadidos a mano a requisitos que la fuente no detalla.
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

        var note = $"Importado de `{spec.Source}` con `dotnet spectrace import`. Edita la fuente y vuelve a importar: " +
                   "se conservan los IDs, los estados y los criterios que añadas aquí a requisitos sin criterios.";
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
        return title.Length > 0 ? char.ToUpperInvariant(title[0]) + title[1..] : "Sin título";
    }
}

/// <summary>
/// Importa specs de GitHub Spec Kit (<c>specs/NNN-feature/spec.md</c>).
/// User stories → requisitos con sus Acceptance Scenarios como criterios.
/// Functional Requirements (FR-NNN) → requisitos proposed (Spec Kit no les da criterios propios).
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
                // Descripción: el texto entre el encabezado y el primer bloque en negrita (**Why this priority**, ...).
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
                if (priority != null) statement = (statement.Length > 0 ? statement + " " : "") + $"(Prioridad {priority})";
                var unclear = statement.Contains("NEEDS CLARIFICATION", StringComparison.OrdinalIgnoreCase) || scenarios.Any(s => s.Contains("NEEDS CLARIFICATION", StringComparison.OrdinalIgnoreCase));

                drafts.Add(new RequirementDraft(
                    $"{prefix}-US-{us.Groups["n"].Value}",
                    ImportWriter.StripMarkdown(us.Groups["t"].Value),
                    unclear || scenarios.Count == 0 ? SpecStatus.Proposed : status,
                    statement.Length > 0 ? statement : "(sin descripción en la spec de origen)",
                    scenarios,
                    unclear ? "La spec de origen tiene puntos [NEEDS CLARIFICATION]: resuélvelos antes de aprobarlo." : null));
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
                    "Requisito funcional de Spec Kit sin criterios propios: añade criterios (- AC-1: ...) y apruébalo para exigirlo."));
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
/// Importa specs de OpenSpec (<c>openspec/specs/&lt;capability&gt;/spec.md</c>).
/// Cada "### Requirement:" es un requisito; cada "#### Scenario:" un criterio de aceptación.
/// OpenSpec no usa IDs: se asignan por orden y se reutilizan por título al reimportar.
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
        // Solo las specs vigentes: openspec/changes/ contiene propuestas (deltas) aún no archivadas.
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

        // IDs estables: se reutiliza el ID de un requisito con el mismo título en el fichero ya importado.
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
                statement.Length > 0 ? statement : "(sin enunciado en la spec de origen)", criteria));
        }

        return new ImportedSpec(outputPath, prefix, title, sourceLabel ?? sourcePath, drafts);
    }
}
