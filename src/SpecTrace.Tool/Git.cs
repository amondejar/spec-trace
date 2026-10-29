using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpecTrace.Tool;

/// <summary>
/// The few git operations SpecTrace needs to compare a branch with its base, through the git command line.
/// Only used by explicit commands (ids, propose_requirement), never by the build.
/// </summary>
public sealed class Git
{
    private readonly string _dir;

    public Git(string dir) => _dir = dir;

    /// <summary>Git is installed and <paramref name="dir"/> is inside a work tree.</summary>
    public static Git? Open(string dir)
    {
        var git = new Git(dir);
        var (code, output) = git.TryRun("rev-parse", "--is-inside-work-tree");
        return code == 0 && output.Trim() == "true" ? git : null;
    }

    public string TopLevel => Run("rev-parse", "--show-toplevel").Trim();

    /// <summary>Where <c>dir</c> is inside the work tree: "" at the top level, "samples/Orders/" below it.</summary>
    public string Prefix => Run("rev-parse", "--show-prefix").Trim();

    public bool RefExists(string reference) => TryRun("rev-parse", "--verify", "--quiet", reference + "^{commit}").Code == 0;

    /// <summary>The branch others merge into: the first of origin/main, origin/master, main, master that exists.</summary>
    public string? DefaultBase() => new[] { "origin/main", "origin/master", "main", "master" }.FirstOrDefault(RefExists);

    /// <summary>The default branch of origin as git knows it ("main"), or null if origin/HEAD is not set.</summary>
    public string? RemoteDefaultBranch()
    {
        var (code, output) = TryRun("symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        var name = output.Trim();
        return code == 0 && name.StartsWith("origin/", StringComparison.Ordinal) ? name.Substring("origin/".Length) : null;
    }

    /// <summary>The checked-out branch (also before its first commit), or null with a detached HEAD.</summary>
    public string? CurrentBranch()
    {
        var (code, output) = TryRun("symbolic-ref", "--quiet", "--short", "HEAD");
        return code == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    public string MergeBase(string a, string b) => Run("merge-base", a, b).Trim();

    /// <summary>Files under <paramref name="directory"/> (relative to the top level) at <paramref name="reference"/>.</summary>
    public IReadOnlyList<string> ListFiles(string reference, string directory) =>
        Run("ls-tree", "-r", "--name-only", "--full-tree", reference, "--", directory)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    public string Show(string reference, string path) => Run("show", $"{reference}:{path}");

    private static readonly Regex DiffFile = new(@"^\+\+\+ b/(?<path>.+)$", RegexOptions.Compiled);
    private static readonly Regex Hunk = new(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@", RegexOptions.Compiled);

    /// <summary>
    /// Lines (1-based) that the working tree adds or changes since <paramref name="since"/>, by path relative to the top
    /// level. Includes uncommitted changes and new files not yet added.
    /// </summary>
    public IReadOnlyDictionary<string, HashSet<int>> AddedLines(string since) => ChangedLines(since, includeDeletions: false);

    /// <summary>
    /// Like <see cref="AddedLines"/>, and also where lines were removed (the lines around each removal), so a change
    /// that only deletes code still touches the member it was in.
    /// </summary>
    public IReadOnlyDictionary<string, HashSet<int>> ChangedLines(string since, bool includeDeletions = true)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? file = null;
        foreach (var line in Run("diff", "-U0", "--no-color", "--no-ext-diff", since).Split('\n'))
        {
            var f = DiffFile.Match(line);
            if (f.Success)
            {
                file = f.Groups["path"].Value.Trim();
                continue;
            }

            var h = Hunk.Match(line);
            if (h.Success && file != null)
            {
                var start = int.Parse(h.Groups["start"].Value);
                var count = h.Groups["count"].Success ? int.Parse(h.Groups["count"].Value) : 1;
                if (!result.TryGetValue(file, out var set)) result[file] = set = new HashSet<int>();
                for (var i = 0; i < count; i++) set.Add(start + i);
                if (count == 0 && includeDeletions)
                {
                    set.Add(Math.Max(start, 1));
                    set.Add(start + 1);
                }
            }
        }

        // Untracked files are new in full.
        foreach (var path in Run("ls-files", "--others", "--exclude-standard", "--full-name").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(TopLevel, path.Trim());
            if (!File.Exists(full)) continue;
            result[path.Trim()] = new HashSet<int>(Enumerable.Range(1, File.ReadAllLines(full).Length));
        }

        return result;
    }

    private string Run(params string[] args)
    {
        var (code, output) = TryRun(args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {output.Trim()}");
        return output;
    }

    private (int Code, string Output) TryRun(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        // Paths with non-ASCII characters as they are, not quoted and escaped.
        start.ArgumentList.Insert(0, "core.quotePath=false");
        start.ArgumentList.Insert(0, "-c");

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, process.ExitCode == 0 ? stdout.Result.Replace("\r\n", "\n") : stderr.Result + stdout.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (-1, "git is not installed or not on the PATH.");
        }
    }
}
