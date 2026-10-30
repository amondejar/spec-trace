using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecTrace.Specs
{
    /// <summary>
    /// An architecture rule (<c>## ARC-001: ...</c> in a spec with <c>kind: architecture</c>): code in some namespaces must
    /// not use some namespaces, types or members. The analyzer checks it on every build (ST0015).
    /// </summary>
    public sealed class ArchitectureRule
    {
        public ArchitectureRule(string id, string title, string status, IReadOnlyList<string> scope, IReadOnlyList<string> forbidden,
            string? useInstead, string rationale, string filePath, int line)
        {
            Id = id;
            Title = title;
            Status = status;
            Scope = scope;
            Forbidden = forbidden;
            UseInstead = useInstead;
            Rationale = rationale;
            FilePath = filePath;
            Line = line;
        }

        public string Id { get; }
        public string Title { get; }
        public string Status { get; }

        /// <summary>Namespaces the rule applies to (<c>In:</c>), including the namespaces below them.</summary>
        public IReadOnlyList<string> Scope { get; }

        /// <summary>
        /// What that code must not use (<c>Must not use:</c>): a namespace (and everything in it), a type (and its members)
        /// or a member, by full name.
        /// </summary>
        public IReadOnlyList<string> Forbidden { get; }

        /// <summary>The alternative, for the message (<c>Use instead:</c>).</summary>
        public string? UseInstead { get; }

        /// <summary>The free text of the rule: why it exists.</summary>
        public string Rationale { get; }

        public string FilePath { get; }

        /// <summary>Zero-based line of the heading.</summary>
        public int Line { get; }

        /// <summary>Approved or implemented rules are checked; proposed and draft ones are only shown to agents and people.</summary>
        public bool IsEnforced => Status == SpecStatus.Approved || Status == SpecStatus.Implemented;

        /// <summary>The rule applies to code in <paramref name="codeNamespace"/>.</summary>
        public bool AppliesTo(string codeNamespace) => Scope.Any(s => ArchitectureNames.IsWithin(codeNamespace, s));

        /// <summary>The forbidden entry that <paramref name="qualifiedName"/> falls under, or null.</summary>
        public string? Forbids(string qualifiedName) => Forbidden.FirstOrDefault(f => ArchitectureNames.IsWithin(qualifiedName, f));
    }

    /// <summary>The architecture of a project: principles for whoever writes code, and rules the build checks.</summary>
    public sealed class ArchitectureSpec
    {
        private ArchitectureSpec(IReadOnlyList<(string FilePath, string Text)> principles, IReadOnlyList<ArchitectureRule> rules,
            IReadOnlyList<SpecIssue> issues)
        {
            Principles = principles;
            Rules = rules;
            Issues = issues;
        }

        public static readonly ArchitectureSpec Empty = new ArchitectureSpec(
            Array.Empty<(string, string)>(), Array.Empty<ArchitectureRule>(), Array.Empty<SpecIssue>());

        /// <summary>The text of each architecture file outside its rules (Markdown), for agents and people.</summary>
        public IReadOnlyList<(string FilePath, string Text)> Principles { get; }

        public IReadOnlyList<ArchitectureRule> Rules { get; }

        public IReadOnlyList<SpecIssue> Issues { get; }

        public bool IsEmpty => Rules.Count == 0 && Principles.All(p => p.Text.Length == 0);

        public IEnumerable<ArchitectureRule> Enforced => Rules.Where(r => r.IsEnforced);

        /// <summary>Combines the architecture files, reporting rule IDs defined twice.</summary>
        public static ArchitectureSpec Build(IEnumerable<ArchitectureDocument> documents)
        {
            var principles = new List<(string, string)>();
            var rules = new List<ArchitectureRule>();
            var issues = new List<SpecIssue>();
            foreach (var doc in documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
            {
                if (doc.Principles.Length > 0) principles.Add((doc.FilePath, doc.Principles));
                issues.AddRange(doc.Issues);
                foreach (var rule in doc.Rules)
                {
                    var first = rules.FirstOrDefault(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
                    if (first != null)
                    {
                        issues.Add(new SpecIssue(SpecIssueKind.DuplicateRequirement,
                            $"Architecture rule '{rule.Id}' is already defined in {first.FilePath} (line {first.Line + 1})", rule.FilePath, rule.Line));
                        continue;
                    }

                    rules.Add(rule);
                }
            }

            return new ArchitectureSpec(principles, rules, issues);
        }

        /// <summary>Principles and rules as Markdown, for agents (get_architecture, context) and people.</summary>
        public string Markdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Architecture");
            if (IsEmpty)
            {
                sb.AppendLine();
                sb.Append("No architecture spec (a *.spec.md with `kind: architecture` in its front matter). Follow the patterns of the existing code.");
                return sb.ToString();
            }

            foreach (var (path, text) in Principles)
            {
                sb.AppendLine();
                sb.AppendLine($"From {path}:");
                sb.AppendLine();
                sb.AppendLine(text);
            }

            if (Rules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Rules");
                sb.AppendLine();
                sb.AppendLine("The build checks the approved rules (ST0015). Proposed ones are not enforced yet, but follow them.");
                foreach (var r in Rules)
                {
                    sb.AppendLine();
                    sb.AppendLine($"- **{r.Id}: {r.Title}** ({r.Status}). Code in {string.Join(", ", r.Scope)} must not use {string.Join(", ", r.Forbidden)}." +
                                  (r.UseInstead != null ? $" Use instead: {r.UseInstead}." : ""));
                    if (r.Rationale.Length > 0) sb.AppendLine("  " + r.Rationale.Replace("\n", "\n  "));
                }
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>One architecture file.</summary>
    public sealed class ArchitectureDocument
    {
        public ArchitectureDocument(string filePath, string principles, IReadOnlyList<ArchitectureRule> rules, IReadOnlyList<SpecIssue> issues)
        {
            FilePath = filePath;
            Principles = principles;
            Rules = rules;
            Issues = issues;
        }

        public string FilePath { get; }
        public string Principles { get; }
        public IReadOnlyList<ArchitectureRule> Rules { get; }
        public IReadOnlyList<SpecIssue> Issues { get; }
    }

    /// <summary>
    /// Reads architecture specs: a <c>*.spec.md</c> whose front matter says <c>kind: architecture</c>. Rules are headings
    /// with an ID (<c>## ARC-001: Title</c>) followed by <c>In:</c>, <c>Must not use:</c> and optionally <c>Status:</c> and
    /// <c>Use instead:</c>; everything else is principles.
    /// </summary>
    public static class ArchitectureParser
    {
        private static readonly Regex AnyHeading = new Regex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);

        private static readonly Regex RuleHeading = new Regex(
            @"^(?<id>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+)\s*:\s*(?<title>.+?)\s*#*\s*$", RegexOptions.Compiled);

        private static readonly Regex Field = new Regex(
            @"^\s*(?:[-*]\s+)?\*{0,2}(?<key>status|in|must not use|must not depend on|use instead)\*{0,2}\s*:\s*\*{0,2}\s*(?<value>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FrontMatterLine = new Regex(@"^(?<key>[A-Za-z_-]+)\s*:\s*(?<value>.*)$", RegexOptions.Compiled);

        /// <summary>The front matter says <c>kind: architecture</c>.</summary>
        public static bool IsArchitecture(string text) =>
            string.Equals(FrontMatter(text.Replace("\r\n", "\n").Split('\n'), out _).TryGetValue("kind", out var kind) ? kind : null,
                "architecture", StringComparison.OrdinalIgnoreCase);

        public static ArchitectureDocument Parse(string filePath, string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            FrontMatter(lines, out var start);
            var rules = new List<ArchitectureRule>();
            var issues = new List<SpecIssue>();
            var principles = new List<string>();
            Builder? current = null;
            var inFence = false;

            for (var i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    (current?.Text ?? principles).Add(line);
                    continue;
                }

                var heading = inFence ? Match.Empty : AnyHeading.Match(line);
                if (heading.Success)
                {
                    var level = heading.Groups["hashes"].Value.Length;
                    var text2 = heading.Groups["text"].Value.Trim();
                    if (current != null && level <= current.Level)
                    {
                        Finish(current, filePath, rules, issues);
                        current = null;
                    }

                    var rule = level >= 2 && level <= 4 ? RuleHeading.Match(text2) : Match.Empty;
                    if (rule.Success)
                    {
                        current = new Builder(rule.Groups["id"].Value, rule.Groups["title"].Value.Trim(), level, i);
                        continue;
                    }

                    // The document title is not a principle.
                    if (level == 1 && current == null) continue;
                    (current?.Text ?? principles).Add(line);
                    continue;
                }

                if (current == null)
                {
                    principles.Add(line);
                    continue;
                }

                var field = inFence ? Match.Empty : Field.Match(line);
                if (field.Success)
                {
                    var value = field.Groups["value"].Value;
                    switch (field.Groups["key"].Value.ToLowerInvariant())
                    {
                        case "status":
                            current.Status ??= value.Trim('`', '*').ToLowerInvariant();
                            current.StatusLine ??= i;
                            break;
                        case "in":
                            current.Scope.AddRange(List(value));
                            break;
                        case "use instead":
                            current.UseInstead = value.Trim('`').Trim();
                            break;
                        default:
                            current.Forbidden.AddRange(List(value));
                            break;
                    }

                    continue;
                }

                current.Text.Add(line);
            }

            if (current != null) Finish(current, filePath, rules, issues);
            return new ArchitectureDocument(filePath, Trim(principles), rules, issues);
        }

        private static void Finish(Builder b, string filePath, List<ArchitectureRule> rules, List<SpecIssue> issues)
        {
            var status = string.IsNullOrEmpty(b.Status) ? SpecStatus.Approved : b.Status!;
            if (!SpecStatus.All.Contains(status))
            {
                issues.Add(new SpecIssue(SpecIssueKind.UnknownStatus,
                    $"Unknown status '{status}' in '{b.Id}'. Use one of: {string.Join(", ", SpecStatus.All)}", filePath, b.StatusLine ?? b.Line));
                status = SpecStatus.Approved;
            }

            if (rules.Any(r => string.Equals(r.Id, b.Id, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new SpecIssue(SpecIssueKind.DuplicateRequirement, $"Architecture rule '{b.Id}' is defined more than once in this file", filePath, b.Line));
                return;
            }

            if (b.Scope.Count == 0 || b.Forbidden.Count == 0)
            {
                issues.Add(new SpecIssue(SpecIssueKind.MissingStatement,
                    $"Architecture rule '{b.Id}' needs the namespaces it applies to (In: MyApp.Domain) and what they must not use " +
                    "(Must not use: Microsoft.AspNetCore, System.DateTime.Now)", filePath, b.Line));
                return;
            }

            rules.Add(new ArchitectureRule(b.Id, b.Title, status, b.Scope.Distinct(StringComparer.Ordinal).ToList(),
                b.Forbidden.Distinct(StringComparer.Ordinal).ToList(), string.IsNullOrEmpty(b.UseInstead) ? null : b.UseInstead,
                Trim(b.Text), filePath, b.Line));
        }

        /// <summary>"`A`, B and C" → A, B, C, as names without "global::" or generic arguments.</summary>
        private static IEnumerable<string> List(string value) =>
            value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(v => Regex.Split(v, @"\s+and\s+|\s+y\s+"))
                .Select(ArchitectureNames.Normalize)
                .Where(v => v.Length > 0);

        private static string Trim(List<string> lines) => string.Join("\n", lines).Trim('\n', ' ', '\t');

        private static Dictionary<string, string> FrontMatter(string[] lines, out int start)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            start = 0;
            if (lines.Length == 0 || lines[0].Trim() != "---") return values;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    start = i + 1;
                    return values;
                }

                var m = FrontMatterLine.Match(lines[i]);
                if (m.Success) values[m.Groups["key"].Value] = m.Groups["value"].Value.Trim().Trim('"', '\'');
            }

            start = 0;
            return values;
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
            public List<string> Scope { get; } = new List<string>();
            public List<string> Forbidden { get; } = new List<string>();
            public string? UseInstead { get; set; }
            public List<string> Text { get; } = new List<string>();
        }
    }

    public static class ArchitectureNames
    {
        /// <summary><paramref name="name"/> is <paramref name="container"/> or something inside it (by '.'-separated segments).</summary>
        public static bool IsWithin(string name, string container) =>
            container.Length > 0 &&
            (string.Equals(name, container, StringComparison.Ordinal) ||
             name.StartsWith(container + ".", StringComparison.Ordinal));

        /// <summary>"`global::System.Collections.Generic.List&lt;T&gt;`" → "System.Collections.Generic.List".</summary>
        public static string Normalize(string name)
        {
            var n = name.Trim().Trim('`', '*', '.').Trim();
            if (n.StartsWith("global::", StringComparison.Ordinal)) n = n.Substring("global::".Length);
            var generic = n.IndexOfAny(new[] { '<', '(' });
            if (generic >= 0) n = n.Substring(0, generic);
            return n.Trim();
        }
    }
}
