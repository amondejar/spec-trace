using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Authoring;

/// <summary>Un requisito listo para escribir en una spec.</summary>
public sealed record RequirementDraft(string Id, string Title, string Status, string Statement, IReadOnlyList<string> Criteria, string? Note = null);

/// <summary>Escribe requisitos en el formato de SpecTrace y calcula IDs nuevos.</summary>
public static class SpecWriter
{
    private static readonly Regex IdParts = new(@"^(?<prefix>.+)-(?<number>\d+)$", RegexOptions.Compiled);

    public static string Render(RequirementDraft r)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(r.Id).Append(": ").AppendLine(OneLine(r.Title));
        sb.Append("Status: ").AppendLine(r.Status);
        sb.AppendLine();
        if (r.Note != null)
        {
            sb.Append("> ").AppendLine(OneLine(r.Note));
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
    /// Siguiente ID con el prefijo más usado del documento (o <paramref name="prefix"/>), conservando el ancho
    /// numérico: ORD-001..ORD-005 → ORD-006.
    /// </summary>
    public static string NextId(IEnumerable<string> existingIds, string? prefix = null)
    {
        var parsed = existingIds
            .Select(id => IdParts.Match(id))
            .Where(m => m.Success)
            .Select(m => (Prefix: m.Groups["prefix"].Value, Number: m.Groups["number"].Value))
            .ToList();

        var chosen = prefix ?? parsed.GroupBy(p => p.Prefix).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? throw new ArgumentException("La spec no tiene requisitos de los que deducir el prefijo; indica el ID o el prefijo.");

        var same = parsed.Where(p => p.Prefix == chosen).ToList();
        var width = same.Count > 0 ? same.Max(p => p.Number.Length) : 3;
        var next = same.Count > 0 ? same.Max(p => int.Parse(p.Number)) + 1 : 1;
        return $"{chosen}-{next.ToString().PadLeft(width, '0')}";
    }

    public static bool IsValidId(string id) =>
        Regex.IsMatch(id, @"^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+$");

    /// <summary>Prefijo válido para IDs a partir de un nombre libre: "photo-albums" → "PHOTO-ALBUMS".</summary>
    public static string ToPrefix(string name)
    {
        var cleaned = Regex.Replace(SpecNaming.ToPascalSlug(name.Replace('-', ' ').Replace('_', ' '), 4), "(?<=[a-z0-9])(?=[A-Z])", "-").ToUpperInvariant();
        cleaned = Regex.Replace(cleaned, "[^A-Z0-9-]", "");
        if (cleaned.Length == 0 || !char.IsLetter(cleaned[0])) cleaned = "S" + cleaned;
        return cleaned;
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
