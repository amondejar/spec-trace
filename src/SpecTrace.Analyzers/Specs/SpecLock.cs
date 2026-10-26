using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecTrace.Specs
{
    /// <summary>
    /// Huella del texto de un requisito. Solo cuenta lo que cambia el comportamiento exigido: el enunciado y cada
    /// criterio de aceptación. El título y el estado no forman parte de la huella; los espacios y saltos de línea se normalizan.
    /// </summary>
    public static class SpecFingerprint
    {
        public static string Of(string text)
        {
            var normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(12);
                for (var i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static SpecLockEntry Entry(SpecRequirement requirement) =>
            new SpecLockEntry(requirement.Id, Of(requirement.Statement),
                requirement.Criteria.ToDictionary(c => c.Id, c => Of(c.Text), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Versión revisada de un requisito: huella del enunciado y de cada criterio.</summary>
    public sealed class SpecLockEntry
    {
        public SpecLockEntry(string id, string statement, IReadOnlyDictionary<string, string> criteria)
        {
            Id = id;
            Statement = statement;
            Criteria = criteria;
        }

        public string Id { get; }
        public string Statement { get; }
        public IReadOnlyDictionary<string, string> Criteria { get; }
    }

    /// <summary>
    /// Fichero <c>spectrace.lock</c>: la versión de cada requisito contra la que se revisaron su código y sus tests.
    /// Formato de texto, una línea por requisito, ordenado por ID para que los diffs en git sean legibles y no haya
    /// conflictos más allá de la línea del requisito tocado. Sin JSON a propósito: el analizador corre en netstandard2.0
    /// sin dependencias.
    /// <code>ORD-001 statement:3f2a9c1e5b7d AC-1:9b1c0d2e3f4a AC-2:77aa01bc23de</code>
    /// </summary>
    public sealed class SpecLock
    {
        public const string FileName = "spectrace.lock";

        private const string Header =
            "# spectrace.lock: versión de cada requisito revisada contra su código y sus tests.\n" +
            "# La genera `dotnet spectrace lock`. Si el texto de un requisito cambia, el build avisa con ST0010/ST0011\n" +
            "# hasta que alguien revise el código y los tests y vuelva a ejecutar `dotnet spectrace lock --requirement <ID>`.\n";

        private readonly Dictionary<string, SpecLockEntry> _entries;

        public SpecLock(IEnumerable<SpecLockEntry> entries)
        {
            _entries = new Dictionary<string, SpecLockEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) _entries[e.Id] = e;
        }

        public IEnumerable<SpecLockEntry> Entries => _entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal);

        public int Count => _entries.Count;

        public SpecLockEntry? Find(string id) => _entries.TryGetValue(id, out var e) ? e : null;

        public SpecLock With(SpecLockEntry entry)
        {
            var copy = _entries.Values.Where(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            copy.Add(entry);
            return new SpecLock(copy);
        }

        /// <summary>Lee el fichero; ignora comentarios y líneas que no entiende.</summary>
        public static SpecLock Parse(string text)
        {
            var entries = new List<SpecLockEntry>();
            foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string? statement = null;
                var criteria = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in parts.Skip(1))
                {
                    var colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = part.Substring(0, colon);
                    var value = part.Substring(colon + 1);
                    if (key == "statement") statement = value;
                    else criteria[key.ToUpperInvariant()] = value;
                }

                if (statement != null) entries.Add(new SpecLockEntry(parts[0], statement, criteria));
            }

            return new SpecLock(entries);
        }

        public string Render()
        {
            var sb = new StringBuilder(Header);
            foreach (var e in Entries)
            {
                sb.Append(e.Id).Append(" statement:").Append(e.Statement);
                foreach (var c in e.Criteria.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    sb.Append(' ').Append(c.Key).Append(':').Append(c.Value);
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }
    }

    /// <summary>Qué ha cambiado en un requisito desde su última revisión.</summary>
    public sealed class SpecDrift
    {
        public static readonly SpecDrift NotLocked = new SpecDrift(false, false, Array.Empty<SpecCriterion>());

        private SpecDrift(bool isLocked, bool statementChanged, IReadOnlyList<SpecCriterion> changedCriteria)
        {
            IsLocked = isLocked;
            StatementChanged = statementChanged;
            ChangedCriteria = changedCriteria;
        }

        /// <summary>El requisito tiene una entrada en spectrace.lock.</summary>
        public bool IsLocked { get; }

        public bool StatementChanged { get; }

        /// <summary>
        /// Criterios cuyo texto cambió. Un criterio nuevo (sin huella) no cuenta: si no tiene test ya lo avisa ST0005.
        /// </summary>
        public IReadOnlyList<SpecCriterion> ChangedCriteria { get; }

        public bool Any => StatementChanged || ChangedCriteria.Count > 0;

        public static SpecDrift Compare(SpecRequirement requirement, SpecLockEntry? entry)
        {
            if (entry == null) return NotLocked;

            var statementChanged = entry.Statement != SpecFingerprint.Of(requirement.Statement);
            var changed = requirement.Criteria
                .Where(c => entry.Criteria.TryGetValue(c.Id, out var hash) && hash != SpecFingerprint.Of(c.Text))
                .ToList();
            return new SpecDrift(true, statementChanged, changed);
        }
    }
}
