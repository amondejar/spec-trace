using System;
using System.IO;
using System.Text.Json;

namespace SpecTrace.Tool;

/// <summary>Contents of spectrace.json, which marks the repository root for SpecTrace.</summary>
public sealed class SpecTraceConfig
{
    public const string FileName = "spectrace.json";

    public string Root { get; set; } = ".";
    public string Specs { get; set; } = "specs";
    public string[] Sources { get; set; } = { "src", "tests" };

    /// <summary>Issue trackers that user stories come from (v0.5: GitHub Issues).</summary>
    public TrackersConfig Trackers { get; set; } = new();

    /// <summary>Looks for spectrace.json from <paramref name="start"/> upwards. Without a file: defaults.</summary>
    public static SpecTraceConfig Load(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            var file = Path.Combine(dir.FullName, FileName);
            if (File.Exists(file))
            {
                var config = JsonSerializer.Deserialize<SpecTraceConfig>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                    ?? new SpecTraceConfig();
                config.Root = dir.FullName;
                return config;
            }

            dir = dir.Parent;
        }

        return new SpecTraceConfig { Root = Path.GetFullPath(start) };
    }
}

public sealed class TrackersConfig
{
    public GitHubTrackerConfig GitHub { get; set; } = new();
}

public sealed class GitHubTrackerConfig
{
    /// <summary>"owner/repo". Default: GITHUB_REPOSITORY, then the "origin" remote of the git repository.</summary>
    public string? Repository { get; set; }

    /// <summary>Label of the issues that are user stories (dotnet spectrace stories). Empty: all issues.</summary>
    public string Label { get; set; } = "user-story";

    /// <summary>Web address, for GitHub Enterprise Server (https://github.example.com). Default https://github.com.</summary>
    public string Url { get; set; } = "https://github.com";

    /// <summary>REST API address. Default https://api.github.com, or &lt;url&gt;/api/v3 for GitHub Enterprise Server.</summary>
    public string? ApiUrl { get; set; }
}
