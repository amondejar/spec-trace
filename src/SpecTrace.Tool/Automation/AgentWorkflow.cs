using System;
using System.Collections.Generic;
using System.IO;

namespace SpecTrace.Tool.Automation;

/// <summary>
/// The GitHub Actions workflow written by <c>dotnet spectrace init --agent</c>: when a push approves a requirement, an
/// agent (the Claude Code GitHub Action with the SpecTrace MCP server) implements it and opens a pull request.
/// </summary>
public static class AgentWorkflow
{
    public const string FileName = "spectrace-implement.yml";

    /// <summary>The pull request label that asks the agent to implement what the pull request approved.</summary>
    public const string Label = "spectrace:implement";

    /// <summary>Fills the template: the branch specs are merged into and the specs folder, relative to the repository.</summary>
    public static string Render(string template, string specs, string branch, string repositoryPrefix = "")
    {
        var folder = (repositoryPrefix + specs.Replace('\\', '/')).Trim('/');
        return template
            .Replace("{{branch}}", branch)
            .Replace("{{specs}}", folder.Length == 0 ? "." : folder)
            .Replace("{{workflow}}", FileName)
            .Replace("{{label}}", Label)
            .Replace("{{workdir}}", repositoryPrefix.Trim('/').Length == 0 ? "." : repositoryPrefix.Trim('/'));
    }

    /// <summary>origin's default branch, else main or master as they exist, else the current branch, else "main".</summary>
    public static string DefaultBranch(string root)
    {
        var git = Git.Open(root);
        if (git == null) return "main";
        var remote = git.RemoteDefaultBranch();
        if (remote != null) return remote;
        var local = git.DefaultBase();
        if (local != null) return local.StartsWith("origin/", StringComparison.Ordinal) ? local.Substring(7) : local;
        // A repository with another main branch name (trunk, develop...) and no remote yet.
        return git.CurrentBranch() ?? "main";
    }

    /// <summary>What the person still has to do on GitHub for the workflow to run.</summary>
    public static IEnumerable<string> Checklist(string root)
    {
        yield return "";
        yield return $"Agent workflow (.github/workflows/{FileName}): before it runs, on GitHub:";
        yield return "  1. Install the Claude GitHub App on the repository: https://github.com/apps/claude";
        yield return "  2. Run `claude setup-token` and save the token as the Actions secret CLAUDE_CODE_OAUTH_TOKEN";
        yield return "     (or an API key as ANTHROPIC_API_KEY: swap the \"auth\" lines in the workflow).";
        yield return "     Do not paste the token anywhere else, and never into a file of the repository.";
        if (!File.Exists(Path.Combine(root, ".config", "dotnet-tools.json")))
        {
            yield return "  3. The workflow runs `dotnet tool restore`: add the tool to the repository with";
            yield return "     dotnet new tool-manifest && dotnet tool install SpecTrace.Tool --prerelease";
        }

        yield return $"  {(File.Exists(Path.Combine(root, ".config", "dotnet-tools.json")) ? 3 : 4)}. Create the label \"{Label}\" (Issues → Labels), or: gh label create \"{Label}\"";
        yield return $"Then, on a spec pull request that approves a requirement, add the label \"{Label}\": the agent pushes the";
        yield return "code and tests to that same pull request. Or start it by hand: Actions → SpecTrace implement → Run workflow.";
    }
}
