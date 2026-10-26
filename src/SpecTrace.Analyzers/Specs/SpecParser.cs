using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpecTrace.Specs
{
    /// <summary>
    /// Lee specs Markdown con requisitos identificados. Sin dependencias de Roslyn: lo comparten
    /// el analizador, el generador y la herramienta de línea de comandos.
    /// </summary>
    public static class SpecParser
    {
        public const string FileSuffix = ".spec.md";

        private static readonly Regex AnyHeading = new Regex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);

        private static readonly Regex RequirementHeading = new Regex(
            @"^(?<id>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+)\s*:\s*(?<title>.+?)\s*#*\s*$",
            RegexOptions.Compiled);

        private static readonly Regex StatusLine = new Regex(
            @"^\s*(?:[-*>]\s*)?\*{0,2}status\*{0,2}\s*:\s*\*{0,2}\s*`?(?<status>[A-Za-z-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CriterionLine = new Regex(
            @"^\s*[-*]\s+(?:\[[ xX]\]\s+)?\*{0,2}(?<id>AC-\d+)\*{0,2}\s*:\s*(?<text>.+?)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex FrontMatterLine = new Regex(@"^(?<key>[A-Za-z_-]+)\s*:\s*(?<value>.*)$", RegexOptions.Compiled);

        private static readonly Regex StatementPrefix = new Regex(@"^\s*(?:\*\*)?(?:EARS|Statement|Enunciado)(?:\*\*)?\s*:\s*(?:\*\*)?\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsSpecFile(string path) =>
            path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase);

        public static SpecDocument Parse(string filePath, string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var requirements = new List<SpecRequirement>();
            var issues = new List<SpecIssue>();
            var start = 0;
            string? specId = null, title = null;

            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                for (var i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == "---")
                    {
                        start = i + 1;
                        break;
                    }

                    var m = FrontMatterLine.Match(lines[i]);
                    if (!m.Success) continue;
                    var key = m.Groups["key"].Value.ToLowerInvariant();
                    var value = m.Groups["value"].Value.Trim().Trim('"', '\'');
                    if (key == "spec" || key == "id") specId = value;
                    else if (key == "title") title = value;
                }
            }

            Builder? current = null;
            var inFence = false;

            for (var i = start; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence) continue;

                var heading = AnyHeading.Match(line);
                if (heading.Success)
                {
                    var level = heading.Groups["hashes"].Value.Length;
                    var headingText = heading.Groups["text"].Value.Trim();

                    if (title == null && level == 1) title = headingText;

                    if (current != null && level <= current.Level)
                    {
                        Finish(current, filePath, requirements, issues);
                        current = null;
                    }

                    var req = level >= 2 && level <= 4 ? RequirementHeading.Match(headingText) : Match.Empty;
                    if (req.Success)
                    {
                        current = new Builder(req.Groups["id"].Value, req.Groups["title"].Value.Trim(), level, i);
                    }

                    continue;
                }

                if (current == null) continue;

                var status = StatusLine.Match(line);
                if (status.Success && current.Status == null)
                {
                    current.Status = status.Groups["status"].Value.ToLowerInvariant();
                    current.StatusLine = i;
                    continue;
                }

                var criterion = CriterionLine.Match(line);
                if (criterion.Success)
                {
                    var id = criterion.Groups["id"].Value.ToUpperInvariant();
                    if (current.Criteria.Any(c => c.Id == id))
                    {
                        issues.Add(new SpecIssue(SpecIssueKind.DuplicateCriterion,
                            $"El criterio '{id}' aparece dos veces en el requisito '{current.Id}'", filePath, i));
                    }
                    else
                    {
                        current.Criteria.Add(new SpecCriterion(id, criterion.Groups["text"].Value, i));
                    }

                    current.StatementClosed = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (current.Statement.Count > 0) current.StatementClosed = true;
                    continue;
                }

                // Las citas (> ...) son notas para personas, no parte del enunciado.
                if (line.TrimStart().StartsWith(">", StringComparison.Ordinal)) continue;

                if (!current.StatementClosed)
                {
                    current.Statement.Add(StatementPrefix.Replace(line.Trim(), string.Empty));
                }
            }

            if (current != null) Finish(current, filePath, requirements, issues);

            return new SpecDocument(filePath, specId, title, requirements, issues);
        }

        private static void Finish(Builder b, string filePath, List<SpecRequirement> requirements, List<SpecIssue> issues)
        {
            var status = b.Status ?? SpecStatus.Approved;
            if (!SpecStatus.All.Contains(status))
            {
                issues.Add(new SpecIssue(SpecIssueKind.UnknownStatus,
                    $"Estado '{status}' desconocido en '{b.Id}'. Usa: {string.Join(", ", SpecStatus.All)}",
                    filePath, b.StatusLine ?? b.Line));
                status = SpecStatus.Approved;
            }

            if (requirements.Any(r => string.Equals(r.Id, b.Id, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new SpecIssue(SpecIssueKind.DuplicateRequirement,
                    $"El requisito '{b.Id}' está definido más de una vez en este fichero", filePath, b.Line));
                return;
            }

            var statement = string.Join(" ", b.Statement).Trim();
            if (statement.Length == 0 && status != SpecStatus.Deprecated)
            {
                issues.Add(new SpecIssue(SpecIssueKind.MissingStatement,
                    $"El requisito '{b.Id}' no tiene enunciado (se recomienda formato EARS: When/While/If ... the system shall ...)",
                    filePath, b.Line));
            }

            if (b.Criteria.Count == 0 && (status == SpecStatus.Approved || status == SpecStatus.Implemented))
            {
                issues.Add(new SpecIssue(SpecIssueKind.MissingCriteria,
                    $"El requisito '{b.Id}' está aprobado pero no tiene criterios de aceptación (- AC-1: ...)",
                    filePath, b.Line));
            }

            requirements.Add(new SpecRequirement(b.Id, b.Title, statement, status, b.Criteria, filePath, b.Line));
        }

        private sealed class Builder
        {
            public Builder(string id, string title, int level, int line)
            {
                Id = id;
                Title = title;
                Level = level;
                Line = line;
            }

            public string Id { get; }
            public string Title { get; }
            public int Level { get; }
            public int Line { get; }
            public string? Status { get; set; }
            public int? StatusLine { get; set; }
            public List<string> Statement { get; } = new List<string>();
            public bool StatementClosed { get; set; }
            public List<SpecCriterion> Criteria { get; } = new List<SpecCriterion>();
        }
    }
}
