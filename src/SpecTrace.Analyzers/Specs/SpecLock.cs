using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecTrace.Specs
{
    /// <summary>
    /// Fingerprint of a requirement's text. Only what changes the required behavior counts: the statement and each
    /// acceptance criterion. Title and status are not part of the fingerprint; whitespace and line breaks are normalized.
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

    /// <summary>Reviewed version of a requirement: fingerprint of the statement and of each criterion.</summary>
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

        /// <summary>The entry as written in the file: equal lines are the same review.</summary>
        public string Line =>
            Id.ToUpperInvariant() + " statement:" + Statement +
            string.Concat(Criteria.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => " " + c.Key + ":" + c.Value));
    }

    /// <summary>
    /// The <c>spectrace.lock</c> file: the version of each requirement its code and tests were reviewed against.
    /// Plain text, one line per requirement, sorted by ID so git diffs stay readable and conflicts never go beyond the
    /// line of the requirement that changed. No JSON on purpose: the analyzer runs on netstandard2.0 without dependencies.
    /// <code>ORD-001 statement:3f2a9c1e5b7d AC-1:9b1c0d2e3f4a AC-2:77aa01bc23de</code>
    /// </summary>
    public sealed class SpecLock
    {
        public const string FileName = "spectrace.lock";

        private const string Header =
            "# spectrace.lock: the version of each requirement that its code and tests were reviewed against.\n" +
            "# Written by `dotnet spectrace lock`. If a requirement's text changes, the build reports ST0010/ST0011\n" +
            "# until someone reviews the code and tests and runs `dotnet spectrace lock --requirement <ID>` again.\n";

        // Every entry of each ID, in file order. After a merge with "merge=union" a requirement can have one line per
        // branch: identical lines collapse, different ones are all kept and the one matching the spec text is used.
        private readonly Dictionary<string, List<SpecLockEntry>> _entries;

        public SpecLock(IEnumerable<SpecLockEntry> entries, IReadOnlyList<int>? conflictMarkerLines = null)
        {
            _entries = new Dictionary<string, List<SpecLockEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (!_entries.TryGetValue(e.Id, out var list)) _entries[e.Id] = list = new List<SpecLockEntry>();
                if (!list.Any(x => x.Line == e.Line)) list.Add(e);
            }

            ConflictMarkerLines = conflictMarkerLines ?? Array.Empty<int>();
        }

        /// <summary>One entry per requirement (the last one when there are several).</summary>
        public IEnumerable<SpecLockEntry> Entries => _entries.Values.Select(l => l[l.Count - 1]).OrderBy(e => e.Id, StringComparer.Ordinal);

        public int Count => _entries.Count;

        /// <summary>IDs with more than one different entry, usually left by merging two branches.</summary>
        public IReadOnlyList<string> Duplicates => _entries.Where(e => e.Value.Count > 1).Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        /// <summary>Zero-based lines with git conflict markers (&lt;&lt;&lt;&lt;&lt;&lt;&lt;, =======, &gt;&gt;&gt;&gt;&gt;&gt;&gt;).</summary>
        public IReadOnlyList<int> ConflictMarkerLines { get; }

        public SpecLockEntry? Find(string id) => _entries.TryGetValue(id, out var l) ? l[l.Count - 1] : null;

        /// <summary>
        /// The entry to compare <paramref name="requirement"/> with: the one with the fewest differences from its current
        /// text. With a single entry, that one. A version is reviewed if any recorded review matches it.
        /// </summary>
        public SpecLockEntry? FindFor(SpecRequirement requirement)
        {
            if (!_entries.TryGetValue(requirement.Id, out var list)) return null;
            return list
                .Select((e, i) => (Entry: e, Index: i, Changes: Changes(SpecDrift.Compare(requirement, e))))
                .OrderBy(x => x.Changes)
                .ThenByDescending(x => x.Index)
                .First().Entry;
        }

        public SpecLock With(SpecLockEntry entry)
        {
            var copy = _entries.Values.SelectMany(l => l).Where(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            copy.Add(entry);
            return new SpecLock(copy);
        }

        /// <summary>
        /// Keeps a single entry per requirement: for the ones in <paramref name="requirements"/>, the entry that matches their
        /// text best; for the rest, the last one. Used before writing the file, so duplicates left by a merge are tidied
        /// without losing a review.
        /// </summary>
        public SpecLock Resolve(IEnumerable<SpecRequirement> requirements)
        {
            var byId = requirements.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return new SpecLock(_entries.Values.Select(l =>
                l.Count > 1 && byId.TryGetValue(l[0].Id, out var r) ? FindFor(r)! : l[l.Count - 1]));
        }

        private static int Changes(SpecDrift drift) => (drift.StatementChanged ? 1 : 0) + drift.ChangedCriteria.Count;

        /// <summary>Reads the file; ignores comments and lines it does not understand.</summary>
        public static SpecLock Parse(string text)
        {
            var entries = new List<SpecLockEntry>();
            var markers = new List<int>();
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith(">>>>>>>", StringComparison.Ordinal) ||
                    line.StartsWith("|||||||", StringComparison.Ordinal) || line == "=======")
                {
                    markers.Add(i);
                    continue;
                }

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

            return new SpecLock(entries, markers);
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

    /// <summary>What changed in a requirement since its last review.</summary>
    public sealed class SpecDrift
    {
        public static readonly SpecDrift NotLocked = new SpecDrift(false, false, Array.Empty<SpecCriterion>());

        private SpecDrift(bool isLocked, bool statementChanged, IReadOnlyList<SpecCriterion> changedCriteria)
        {
            IsLocked = isLocked;
            StatementChanged = statementChanged;
            ChangedCriteria = changedCriteria;
        }

        /// <summary>The requirement has an entry in spectrace.lock.</summary>
        public bool IsLocked { get; }

        public bool StatementChanged { get; }

        /// <summary>
        /// Criteria whose text changed. A new criterion (without a fingerprint) does not count: if it has no test, ST0005 reports it.
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
