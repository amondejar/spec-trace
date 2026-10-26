using System;
using System.Collections.Generic;
using System.Linq;

namespace SpecTrace.Specs
{
    /// <summary>Criterio de aceptación de un requisito (<c>- AC-1: ...</c>).</summary>
    public sealed class SpecCriterion
    {
        public SpecCriterion(string id, string text, int line)
        {
            Id = id;
            Text = text;
            Line = line;
        }

        public string Id { get; }
        public string Text { get; }

        /// <summary>Línea en el fichero, empezando en 0.</summary>
        public int Line { get; }
    }

    /// <summary>Un requisito con ID, enunciado (idealmente EARS), estado y criterios.</summary>
    public sealed class SpecRequirement
    {
        public SpecRequirement(string id, string title, string statement, string status,
            IReadOnlyList<SpecCriterion> criteria, string filePath, int line)
        {
            Id = id;
            Title = title;
            Statement = statement;
            Status = status;
            Criteria = criteria;
            FilePath = filePath;
            Line = line;
        }

        public string Id { get; }
        public string Title { get; }
        public string Statement { get; }
        public string Status { get; }
        public IReadOnlyList<SpecCriterion> Criteria { get; }
        public string FilePath { get; }

        /// <summary>Línea del encabezado, empezando en 0.</summary>
        public int Line { get; }

        public bool IsDeprecated => Status == SpecStatus.Deprecated;

        /// <summary>Approved o implemented: se exige implementación y test.</summary>
        public bool IsEnforced => Status == SpecStatus.Approved || Status == SpecStatus.Implemented;

        public SpecCriterion? FindCriterion(string id) =>
            Criteria.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Nombre de identificador C# para el ID: <c>ORD-001</c> → <c>ORD_001</c>.</summary>
        public string Identifier => SpecNaming.ToIdentifier(Id);
    }

    public static class SpecStatus
    {
        public const string Draft = "draft";
        public const string Proposed = "proposed";
        public const string Approved = "approved";
        public const string Implemented = "implemented";
        public const string Deprecated = "deprecated";

        public static readonly IReadOnlyList<string> All = new[] { Draft, Proposed, Approved, Implemented, Deprecated };
    }

    public enum SpecIssueKind
    {
        DuplicateRequirement,
        DuplicateCriterion,
        MissingCriteria,
        MissingStatement,
        UnknownStatus,
    }

    /// <summary>Problema de formato detectado al leer una spec.</summary>
    public sealed class SpecIssue
    {
        public SpecIssue(SpecIssueKind kind, string message, string filePath, int line)
        {
            Kind = kind;
            Message = message;
            FilePath = filePath;
            Line = line;
        }

        public SpecIssueKind Kind { get; }
        public string Message { get; }
        public string FilePath { get; }
        public int Line { get; }
    }

    public sealed class SpecDocument
    {
        public SpecDocument(string filePath, string? specId, string? title,
            IReadOnlyList<SpecRequirement> requirements, IReadOnlyList<SpecIssue> issues)
        {
            FilePath = filePath;
            SpecId = specId;
            Title = title;
            Requirements = requirements;
            Issues = issues;
        }

        public string FilePath { get; }
        public string? SpecId { get; }
        public string? Title { get; }
        public IReadOnlyList<SpecRequirement> Requirements { get; }
        public IReadOnlyList<SpecIssue> Issues { get; }
    }

    /// <summary>Todas las specs de un proyecto, indexadas por ID de requisito.</summary>
    public sealed class SpecCatalog
    {
        private readonly Dictionary<string, SpecRequirement> _byId;

        private SpecCatalog(IReadOnlyList<SpecDocument> documents, Dictionary<string, SpecRequirement> byId,
            IReadOnlyList<SpecIssue> issues)
        {
            Documents = documents;
            _byId = byId;
            Issues = issues;
        }

        public IReadOnlyList<SpecDocument> Documents { get; }

        /// <summary>Problemas de formato de todos los ficheros, incluidos IDs duplicados entre ficheros.</summary>
        public IReadOnlyList<SpecIssue> Issues { get; }

        public IEnumerable<SpecRequirement> Requirements => _byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal);

        public bool TryGet(string id, out SpecRequirement requirement) => _byId.TryGetValue(id, out requirement!);

        public SpecRequirement? FindByIdentifier(string identifier) =>
            _byId.Values.FirstOrDefault(r => r.Identifier == identifier);

        public static SpecCatalog Build(IEnumerable<SpecDocument> documents)
        {
            var docs = documents.OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList();
            var byId = new Dictionary<string, SpecRequirement>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<SpecIssue>();

            foreach (var doc in docs)
            {
                issues.AddRange(doc.Issues);
                foreach (var req in doc.Requirements)
                {
                    if (byId.TryGetValue(req.Id, out var first))
                    {
                        if (first.FilePath != req.FilePath)
                        {
                            issues.Add(new SpecIssue(SpecIssueKind.DuplicateRequirement,
                                $"El requisito '{req.Id}' ya está definido en {first.FilePath} (línea {first.Line + 1})",
                                req.FilePath, req.Line));
                        }

                        continue;
                    }

                    byId[req.Id] = req;
                }
            }

            return new SpecCatalog(docs, byId, issues);
        }
    }

    public static class SpecNaming
    {
        public static string ToIdentifier(string id)
        {
            var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            var name = new string(chars);
            return name.Length > 0 && char.IsDigit(name[0]) ? "_" + name : name;
        }

        /// <summary>Convierte un título libre en un fragmento de identificador PascalCase sin acentos.</summary>
        public static string ToPascalSlug(string text, int maxWords = 6)
        {
            var normalized = RemoveDiacritics(text);
            var words = normalized
                .Split(new[] { ' ', '-', '_', ',', '.', ';', ':', '/', '(', ')', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
                .Where(w => w.Length > 0)
                .Take(maxWords)
                .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
            return string.Concat(words);
        }

        /// <summary>
        /// Los IDs más parecidos a <paramref name="target"/> (distancia de edición, sin distinguir mayúsculas),
        /// para sugerir correcciones de erratas como ORD-01 → ORD-001.
        /// </summary>
        public static IReadOnlyList<string> Closest(IEnumerable<string> candidates, string target, int max = 3)
        {
            var t = target.ToUpperInvariant();
            var limit = Math.Max(2, t.Length / 3);
            return candidates
                .Select(c => new { Id = c, Distance = Distance(c.ToUpperInvariant(), t) })
                .Where(x => x.Distance <= limit)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .Take(max)
                .Select(x => x.Id)
                .ToList();
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }

                var tmp = prev;
                prev = cur;
                cur = tmp;
            }

            return prev[b.Length];
        }

        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
            var kept = decomposed.Where(c =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark);
            return new string(kept.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
