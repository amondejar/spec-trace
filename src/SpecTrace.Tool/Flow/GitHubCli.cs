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

    /// <summary>The pull request of a branch (the newest one, open or not), or null.</summary>
    public PullRequest? ForBranch(string branch)
    {
        var (code, output) = _run(new[] { "pr", "list", "--head", branch, "--state", "all", "--limit", "1", "--json", "number,url,state,labels" });
        if (code != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            var pr = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (pr.ValueKind != JsonValueKind.Object) return null;
            var labels = pr.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(n => n.Length > 0).ToList()
                : new List<string>();
            return new PullRequest(pr.GetProperty("number").GetInt32(), pr.GetProperty("url").GetString() ?? "",
                pr.GetProperty("state").GetString() ?? "", labels);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Opens a pull request from <paramref name="branch"/> and returns it.</summary>
    public PullRequest Create(string branch, string baseBranch, string title, string body)
    {
        var (code, output) = _run(new[] { "pr", "create", "--head", branch, "--base", baseBranch, "--title", title, "--body", body });
        if (code != 0) throw new InvalidOperationException("gh pr create failed: " + output.Trim());
        return ForBranch(branch) ?? throw new InvalidOperationException("The pull request was created but gh cannot find it: " + output.Trim());
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
