using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace SpecTrace.Tool.Flow;

/// <summary>A pull request as the flow needs it.</summary>
public sealed record PullRequest(int Number, string Url, string State, IReadOnlyList<string> Labels)
{
    public bool IsOpen => string.Equals(State, "OPEN", StringComparison.OrdinalIgnoreCase);
    public bool IsMerged => string.Equals(State, "MERGED", StringComparison.OrdinalIgnoreCase);
    public bool HasLabel(string label) => Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The GitHub CLI (gh), for the steps of the flow that happen on GitHub: open the pull request and add the label that
/// starts the agent. Optional: without gh the flow says what to do on github.com instead.
/// </summary>
public sealed class GitHubCli
{
    private readonly Func<string[], (int Code, string Output)> _run;
    private bool? _available;

    /// <param name="run">Runs gh with the given arguments in the repository; replaceable in tests.</param>
    public GitHubCli(string workingDirectory, Func<string[], (int Code, string Output)>? run = null)
    {
        _run = run ?? (args => Run(workingDirectory, args));
    }

    /// <summary>gh is installed and logged in.</summary>
    public bool Available => _available ??= _run(new[] { "auth", "status" }).Code == 0;

    /// <summary>
    /// The pull request of a branch (open first, else the newest merged or closed one), or null. `gh pr view` reads it
    /// directly; `gh pr list` (a search) is only the fallback, since a pull request created seconds ago may not be in it yet.
    /// </summary>
    public PullRequest? ForBranch(string branch)
    {
        var (code, output) = _run(new[] { "pr", "view", branch, "--json", "number,url,state,labels" });
        if (code == 0 && Parse(output) is { } viewed) return viewed;
        (code, output) = _run(new[] { "pr", "list", "--head", branch, "--state", "all", "--limit", "1", "--json", "number,url,state,labels" });
        return code == 0 ? Parse(output) : null;
    }

    /// <summary>Opens a pull request from <paramref name="branch"/> and returns it (from the URL gh prints).</summary>
    public PullRequest Create(string branch, string baseBranch, string title, string body)
    {
        var (code, output) = _run(new[] { "pr", "create", "--head", branch, "--base", baseBranch, "--title", title, "--body", body });
        if (code != 0) throw new InvalidOperationException("gh pr create failed: " + output.Trim());
        var url = System.Text.RegularExpressions.Regex.Match(output, @"https?://\S+/pull/(?<n>\d+)");
        if (url.Success) return new PullRequest(int.Parse(url.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), url.Value, "OPEN", new List<string>());
        return ForBranch(branch) ?? throw new InvalidOperationException("The pull request was created but gh cannot find it: " + output.Trim());
    }

    /// <summary>One pull request object, or the first of an array.</summary>
    private static PullRequest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().FirstOrDefault() : doc.RootElement;
            if (pr.ValueKind != JsonValueKind.Object || !pr.TryGetProperty("number", out var number)) return null;
            var labels = pr.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(n => n.Length > 0).ToList()
                : new List<string>();
            return new PullRequest(number.GetInt32(), pr.GetProperty("url").GetString() ?? "", pr.GetProperty("state").GetString() ?? "", labels);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    public void AddLabel(int number, string label)
    {
        var (code, output) = _run(new[] { "pr", "edit", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--add-label", label });
        if (code != 0)
        {
            throw new InvalidOperationException($"gh could not add the label \"{label}\": {output.Trim()}" +
                (output.Contains("not found", StringComparison.OrdinalIgnoreCase) ? $" Create it first: gh label create \"{label}\"" : ""));
        }
    }

    private static (int, string) Run(string dir, string[] args)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["NO_COLOR"] = "1";
        try
        {
            using var p = Process.Start(start)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, "gh is not installed");
        }
    }
}
