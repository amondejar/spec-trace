using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Authoring;

/// <summary>A requirement ready to be written into a spec.</summary>
public sealed record RequirementDraft(string Id, string Title, string Status, string Statement, IReadOnlyList<string> Criteria, string? Note = null)
{
    /// <summary>Where it comes from (a story URL or reference): written as a <c>Source:</c> line.</summary>
    public string? Source { get; init; }

    /// <summary>What the source leaves undecided: written in the note, for the person who approves it.</summary>
    public IReadOnlyList<string> OpenQuestions { get; init; } = Array.Empty<string>();
}

/// <summary>Writes requirements in the SpecTrace format and computes new IDs.</summary>
public static class SpecWriter
{
    private static readonly Regex IdParts = new(@"^(?<prefix>.+)-(?<number>\d+)$", RegexOptions.Compiled);

    public static string Render(RequirementDraft r)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(r.Id).Append(": ").AppendLine(OneLine(r.Title));
        sb.Append("Status: ").AppendLine(r.Status);
        if (r.Source != null) sb.Append("Source: ").AppendLine(OneLine(r.Source));
        sb.AppendLine();

        var questions = r.OpenQuestions.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        if (r.Note != null || questions.Count > 0)
        {
            if (r.Note != null) sb.Append("> ").AppendLine(OneLine(r.Note));
            if (questions.Count > 0)
            {
                sb.AppendLine("> Open questions:");
                foreach (var q in questions) sb.Append("> - ").AppendLine(OneLine(q));
            }

            sb.AppendLine();
        }

        sb.AppendLine(r.Statement.Trim());
        if (r.Criteria.Count > 0)
        {
            sb.AppendLine();
            for (var i = 0; i < r.Criteria.Count; i++)
            {
                sb.Append("- AC-").Append(i + 1).Append(": ").AppendLine(OneLine(r.Criteria[i]));
            }
        }

        return sb.ToString();
    }

    public static string RenderDocument(string specId, string title, string? sourceNote, IEnumerable<RequirementDraft> requirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.Append("spec: ").AppendLine(specId);
        sb.Append("title: ").AppendLine(OneLine(title));
        sb.AppendLine("---");
        sb.Append("# ").AppendLine(OneLine(title));
        sb.AppendLine();
        if (sourceNote != null)
        {
            sb.AppendLine(sourceNote);
            sb.AppendLine();
        }

        foreach (var r in requirements)
        {
            sb.Append(Render(r));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Next ID with the document's most used prefix (or <paramref name="prefix"/>), keeping the numeric
    /// width: ORD-001..ORD-005 → ORD-006.
    /// </summary>
    public static string NextId(IEnumerable<string> existingIds, string? prefix = null)
    {
        var parsed = existingIds
            .Select(id => IdParts.Match(id))
            .Where(m => m.Success)
            .Select(m => (Prefix: m.Groups["prefix"].Value, Number: m.Groups["number"].Value))
            .ToList();

        var chosen = prefix ?? parsed.GroupBy(p => p.Prefix).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? throw new ArgumentException("The spec has no requirements to infer the prefix from; give the ID or the prefix.");

        var same = parsed.Where(p => p.Prefix == chosen).ToList();
        var width = same.Count > 0 ? same.Max(p => p.Number.Length) : 3;
        var next = same.Count > 0 ? same.Max(p => int.Parse(p.Number)) + 1 : 1;
        return $"{chosen}-{next.ToString().PadLeft(width, '0')}";
    }

    public static bool IsValidId(string id) =>
        Regex.IsMatch(id, @"^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+$");

    /// <summary>Valid ID prefix from a free-text name: "photo-albums" → "PHOTO-ALBUMS".</summary>
    public static string ToPrefix(string name)
    {
        var cleaned = Regex.Replace(SpecNaming.ToPascalSlug(name.Replace('-', ' ').Replace('_', ' '), 4), "(?<=[a-z0-9])(?=[A-Z])", "-").ToUpperInvariant();
        cleaned = Regex.Replace(cleaned, "[^A-Z0-9-]", "");
        if (cleaned.Length == 0 || !char.IsLetter(cleaned[0])) cleaned = "S" + cleaned;
        return cleaned;
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
