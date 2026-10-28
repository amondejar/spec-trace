using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SpecTrace.Tool.Stories;

/// <summary>Reads "owner/repo" from the "origin" remote in .git/config, without running git.</summary>
public static class GitRemote
{
    private static readonly Regex OriginSection = new(@"^\s*\[remote\s+""origin""\]\s*$", RegexOptions.Compiled);
    private static readonly Regex UrlLine = new(@"^\s*url\s*=\s*(?<url>\S+)\s*$", RegexOptions.Compiled);

    // https://github.com/owner/repo(.git), git@github.com:owner/repo(.git), ssh://git@github.com/owner/repo(.git)
    private static readonly Regex Repo = new(@"[/:](?<owner>[A-Za-z0-9-_.]+)/(?<repo>[A-Za-z0-9-_.]+?)(?:\.git)?/?$", RegexOptions.Compiled);

    public static string? OriginRepository(string startDir, string host)
    {
        var url = OriginUrl(startDir);
        if (url == null || url.IndexOf(host, StringComparison.OrdinalIgnoreCase) < 0) return null;
        var m = Repo.Match(url);
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
    }

    public static string? OriginUrl(string startDir)
    {
        var config = FindConfig(startDir);
        if (config == null) return null;

        var inOrigin = false;
        foreach (var line in File.ReadAllLines(config))
        {
            if (line.TrimStart().StartsWith("[", StringComparison.Ordinal)) inOrigin = OriginSection.IsMatch(line);
            else if (inOrigin && UrlLine.Match(line) is { Success: true } m) return m.Groups["url"].Value;
        }

        return null;
    }

    private static string? FindConfig(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git)) return Existing(Path.Combine(git, "config"));
            if (File.Exists(git))
            {
                // Worktree or submodule: ".git" is a file with "gitdir: <path>".
                var pointer = File.ReadAllText(git).Trim();
                if (pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var gitDir = Path.GetFullPath(Path.Combine(dir.FullName, pointer.Substring(7).Trim()));
                    var common = Path.Combine(gitDir, "commondir");
                    if (File.Exists(common)) gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
                    return Existing(Path.Combine(gitDir, "config"));
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
