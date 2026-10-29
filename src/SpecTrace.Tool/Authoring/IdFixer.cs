using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Authoring;

/// <summary>A requirement heading in a spec file, with the requirement it introduces.</summary>
public sealed record IdOccurrence(string Id, string Title, string File, int Line, SpecRequirement Requirement, bool AddedByBranch);

/// <summary>A requirement that has to change ID. <see cref="NewId"/> is null when it cannot be decided automatically.</summary>
public sealed record IdCollision(IdOccurrence Occurrence, string Reason, string? NewId);

public sealed record IdPlan(string? Base, IReadOnlyList<IdCollision> Collisions, IReadOnlyList<string> Notes)
{
    public bool HasWork => Collisions.Count > 0;
}

public sealed record IdChange(string OldId, string NewId, IReadOnlyList<string> Files, int CodeReferences, int LockLines);

/// <summary>
/// Requirement IDs used twice, typically because two branches proposed "the next ID" of the same spec at the same time.
/// Compared with the base branch through git, so it renumbers only what the current branch added: its requirement,
/// its spectrace.lock line and the references in the code and tests the branch added.
/// </summary>
public static class IdFixer
{
    private static readonly Regex Heading = new(
        @"^\s{0,3}(?<hashes>#{2,4})\s+(?<id>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+)\s*:\s*(?<title>.+?)\s*#*\s*$", RegexOptions.Compiled);

    private static readonly Regex AnyHeading = new(@"^\s{0,3}(?<hashes>#{1,6})\s", RegexOptions.Compiled);

    public static IdPlan Plan(SpecTraceConfig config, string? baseRef)
    {
        var notes = new List<string>();
        var git = Git.Open(config.Root);
        if (git == null) notes.Add("Not a git repository (or git is not installed): only duplicates inside the working tree are detected, and code references are not updated.");

        string? @base = null;
        IReadOnlyDictionary<string, HashSet<int>> added = new Dictionary<string, HashSet<int>>();
        var baseRequirements = new Dictionary<string, SpecRequirement>(StringComparer.OrdinalIgnoreCase);
        string? prefix = null;

        if (git != null)
        {
            prefix = git.Prefix;
            @base = baseRef ?? git.DefaultBase();
            if (@base == null)
            {
                notes.Add("No base branch found (origin/main, origin/master, main or master): use --base <ref>.");
            }
            else if (!git.RefExists(@base))
            {
                throw new ArgumentException($"'{@base}' is not a commit or branch of this repository.");
            }
            else
            {
                added = git.AddedLines(git.MergeBase(@base, "HEAD"));
                var specsDir = prefix + config.Specs.Replace('\\', '/').TrimEnd('/');
                foreach (var path in git.ListFiles(@base, specsDir).Where(SpecParser.IsSpecFile))
                {
                    foreach (var r in SpecParser.Parse(path, git.Show(@base, path)).Requirements) baseRequirements[r.Id] = r;
                }
            }
        }

        var occurrences = Scan(config, prefix, added);
        var used = new HashSet<string>(occurrences.Select(o => o.Id).Concat(baseRequirements.Keys), StringComparer.OrdinalIgnoreCase);
        var collisions = new List<IdCollision>();

        foreach (var group in occurrences.GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase))
        {
            var all = group.ToList();
            baseRequirements.TryGetValue(group.Key, out var onBase);

            if (all.Count > 1)
            {
                var ours = all.Where(o => o.AddedByBranch).ToList();
                var theirs = all.Where(o => !o.AddedByBranch).ToList();
                if (theirs.Count == 0 && ours.Count > 0)
                {
                    // Both new in this branch (or no base to compare with): the first one in file order keeps the ID.
                    theirs = ours.Take(1).ToList();
                    ours = ours.Skip(1).ToList();
                }

                if (ours.Count == 0)
                {
                    collisions.AddRange(all.Skip(1).Select(o => new IdCollision(o,
                        $"{o.Id} is also \"{all[0].Title}\" ({Where(all[0])}) and neither was added by this branch: rename one by hand", null)));
                    continue;
                }

                foreach (var o in ours)
                {
                    collisions.Add(new IdCollision(o, $"{o.Id} is also \"{theirs[0].Title}\" ({Where(theirs[0])})", Next(o.Id, used)));
                }
            }
            else if (onBase != null && all[0].AddedByBranch && IsDifferent(all[0].Requirement, onBase))
            {
                // Not merged yet: the base already has another requirement with this ID.
                var o = all[0];
                collisions.Add(new IdCollision(o, $"{o.Id} is \"{onBase.Title}\" on {@base}", Next(o.Id, used)));
            }
        }

        return new IdPlan(@base, collisions, notes);
    }

    /// <summary>Applies the plan: spec heading, spectrace.lock line and the code references the branch added.</summary>
    public static IReadOnlyList<IdChange> Apply(SpecTraceConfig config, IdPlan plan)
    {
        var git = Git.Open(config.Root);
        var prefix = git?.Prefix;
        IReadOnlyDictionary<string, HashSet<int>> added = git != null && plan.Base != null
            ? git.AddedLines(git.MergeBase(plan.Base, "HEAD"))
            : new Dictionary<string, HashSet<int>>();

        var changes = new List<IdChange>();
        foreach (var c in plan.Collisions.Where(c => c.NewId != null))
        {
            var o = c.Occurrence;
            var files = new List<string> { o.File };

            // 1. The heading of this requirement only.
            var specPath = Path.Combine(config.Root, o.File);
            var lines = ReadLines(specPath, out var newline);
            lines[o.Line] = new Regex(Regex.Escape(o.Id) + @"(?=\s*:)").Replace(lines[o.Line], c.NewId!, 1);
            File.WriteAllText(specPath, string.Join(newline, lines));

            // 2. Its review in spectrace.lock: the line whose fingerprint is this requirement's statement.
            var lockLines = 0;
            var lockPath = TraceScanner.LockPath(config);
            if (File.Exists(lockPath))
            {
                var hash = "statement:" + SpecFingerprint.Of(o.Requirement.Statement);
                var lockText = ReadLines(lockPath, out var lockNewline);
                for (var i = 0; i < lockText.Length; i++)
                {
                    var parts = lockText[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && string.Equals(parts[0], o.Id, StringComparison.OrdinalIgnoreCase) && parts[1] == hash)
                    {
                        lockText[i] = c.NewId + lockText[i].TrimStart().Substring(parts[0].Length);
                        lockLines++;
                    }
                }

                if (lockLines > 0)
                {
                    File.WriteAllText(lockPath, string.Join(lockNewline, lockText));
                    files.Add(SpecLock.FileName);
                }
            }

            // 3. References in code and tests, only on the lines this branch added: the other requirement keeps its own.
            var references = 0;
            if (prefix != null)
            {
                var oldIdent = SpecNaming.ToIdentifier(o.Id);
                var newIdent = SpecNaming.ToIdentifier(c.NewId!);
                var patterns = new (Regex Pattern, string Replacement)[]
                {
                    (new Regex(@"\b(Requirements|AcceptanceCriteria)\." + Regex.Escape(oldIdent) + @"\b"), "$1." + newIdent),
                    (new Regex("\"" + Regex.Escape(o.Id) + "\""), "\"" + c.NewId + "\""),
                };

                foreach (var source in config.Sources.Select(s => Path.Combine(config.Root, s)).Where(Directory.Exists))
                {
                    foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
                    {
                        if (!added.TryGetValue(prefix + TraceScanner.Relative(config.Root, file), out var addedLines)) continue;
                        var code = ReadLines(file, out var codeNewline);
                        var count = 0;
                        foreach (var n in addedLines.Where(n => n >= 1 && n <= code.Length))
                        {
                            foreach (var (pattern, replacement) in patterns)
                            {
                                var matches = pattern.Matches(code[n - 1]).Count;
                                if (matches == 0) continue;
                                code[n - 1] = pattern.Replace(code[n - 1], replacement);
                                count += matches;
                            }
                        }

                        if (count == 0) continue;
                        File.WriteAllText(file, string.Join(codeNewline, code));
                        references += count;
                        files.Add(TraceScanner.Relative(config.Root, file));
                    }
                }
            }

            changes.Add(new IdChange(o.Id, c.NewId!, files, references, lockLines));
        }

        return changes;
    }

    private static List<IdOccurrence> Scan(SpecTraceConfig config, string? prefix, IReadOnlyDictionary<string, HashSet<int>> added)
    {
        var result = new List<IdOccurrence>();
        var specsDir = Path.Combine(config.Root, config.Specs);
        if (!Directory.Exists(specsDir)) return result;

        foreach (var file in Directory.EnumerateFiles(specsDir, "*" + SpecParser.FileSuffix, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = TraceScanner.Relative(config.Root, file);
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            added.TryGetValue(prefix == null ? "" : prefix + relative, out var addedLines);
            var inFence = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal) || lines[i].TrimStart().StartsWith("~~~", StringComparison.Ordinal)) inFence = !inFence;
                if (inFence) continue;
                var m = Heading.Match(lines[i]);
                if (!m.Success) continue;

                // The requirement's own block, up to the next heading of the same or a higher level.
                var level = m.Groups["hashes"].Value.Length;
                var end = i + 1;
                while (end < lines.Length && !(AnyHeading.Match(lines[end]) is { Success: true } h && h.Groups["hashes"].Value.Length <= level)) end++;
                var block = string.Join("\n", lines.Skip(i).Take(end - i));
                var requirement = SpecParser.Parse(relative, block).Requirements.FirstOrDefault();
                if (requirement == null) continue;

                result.Add(new IdOccurrence(m.Groups["id"].Value, m.Groups["title"].Value.Trim(), relative, i, requirement,
                    addedLines != null && addedLines.Contains(i + 1)));
            }
        }

        return result;
    }

    /// <summary>A different requirement, not the same one with an edited title or statement.</summary>
    private static bool IsDifferent(SpecRequirement a, SpecRequirement b) =>
        !string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) &&
        SpecFingerprint.Of(a.Statement) != SpecFingerprint.Of(b.Statement);

    private static string Next(string id, HashSet<string> used)
    {
        var prefix = id.Substring(0, id.LastIndexOf('-'));
        var next = SpecWriter.NextId(used, prefix);
        used.Add(next);
        return next;
    }

    private static string Where(IdOccurrence o) => $"{o.File}:{o.Line + 1}";

    private static string[] ReadLines(string path, out string newline)
    {
        var text = File.ReadAllText(path);
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Replace("\r\n", "\n").Split('\n');
    }
}
