using System.Collections.Generic;

namespace SpecTrace.Tool.Stories;

public sealed record StoryQuery(string? Label, string State = "open", int Limit = 100);

public sealed record StorySummary(StoryRef Ref, string Title, string State, IReadOnlyList<string> Labels);

/// <summary>
/// An issue tracker that user stories come from. GitHub Issues in v0.5; Jira comes next with the same contract,
/// so specs, the CLI and the MCP tools do not depend on the tracker.
/// </summary>
public interface IStoryTracker
{
    /// <summary>"github", "jira"...</summary>
    string Name { get; }

    /// <summary>
    /// Recognizes a reference to one of this tracker's stories ("#3", "owner/repo#3", its web address...).
    /// It never calls the network, so it can be used to match <c>Source:</c> lines in specs.
    /// </summary>
    bool TryParse(string text, out StoryRef reference);

    Story Get(StoryRef reference);

    IReadOnlyList<StorySummary> List(StoryQuery query);
}
