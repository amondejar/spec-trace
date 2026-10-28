using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Stories;

/// <summary>Stories next to the specs: which requirements cite each story, as text for people and agents.</summary>
public static class StoryReports
{
    private static readonly string[] NarrativeHeadings = { "as a", "as an", "como", "i want", "quiero", "so that", "para", "para que" };
    private static readonly string[] CriteriaHeadings = { "acceptance criteria", "criterios de aceptación", "criterios de aceptacion" };

    /// <summary>Requirements whose <c>Source:</c> points at <paramref name="reference"/>.</summary>
    public static IReadOnlyList<SpecRequirement> RequirementsFrom(TraceModel model, IStoryTracker tracker, StoryRef reference) =>
        model.Requirements
            .Select(t => t.Requirement)
            .Where(r => r.Source != null && tracker.TryParse(r.Source, out var source) && source.SameAs(reference))
            .ToList();

    public static string Markdown(Story story, IReadOnlyList<SpecRequirement> specified)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Story {story.Ref.Display}: {story.Title}");
        sb.AppendLine();
        var labels = story.Labels.Count > 0 ? " · labels: " + string.Join(", ", story.Labels) : "";
        var author = string.IsNullOrEmpty(story.Author) ? "" : $" · by {story.Author}";
        sb.AppendLine($"{story.Ref.Url} · {story.State}{labels}{author}");
        sb.AppendLine();

        if (story.Narrative != null)
        {
            sb.AppendLine("## User story");
            sb.AppendLine();
            sb.AppendLine(story.Narrative);
            sb.AppendLine();
        }

        if (story.Sections.Count > 0)
        {
            sb.AppendLine("## Acceptance criteria in the story");
            sb.AppendLine();
            if (story.AcceptanceCriteria.Count == 0) sb.AppendLine("None: the story gives no acceptance criteria.");
            foreach (var c in story.AcceptanceCriteria) sb.AppendLine($"- {c}");
            sb.AppendLine();

            // The rest of the sections (Notes, Context...), without repeating what is already shown above.
            foreach (var section in story.Sections)
            {
                var key = StoryText.Key(section.Heading);
                if (CriteriaHeadings.Contains(key) || section.Text.Length == 0) continue;
                if (story.Narrative != null && NarrativeHeadings.Contains(key)) continue;
                sb.AppendLine($"## {section.Heading}");
                sb.AppendLine();
                sb.AppendLine(section.Text);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(story.Body.Trim().Length > 0 ? story.Body.Trim() : "(empty)");
            sb.AppendLine();
        }

        if (story.Comments.Count > 0)
        {
            sb.AppendLine($"## Comments ({story.Comments.Count})");
            sb.AppendLine();
            foreach (var c in story.Comments)
            {
                sb.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}):");
                sb.AppendLine();
                foreach (var line in c.Body.Trim().Replace("\r\n", "\n").Split('\n')) sb.AppendLine("> " + line);
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Requirements that come from this story");
        sb.AppendLine();
        if (specified.Count == 0)
        {
            sb.AppendLine("None yet: the story has not been turned into a spec.");
        }
        else
        {
            foreach (var r in specified) sb.AppendLine($"- {r.Id}: {r.Title} ({r.Status}) — {r.FilePath}:{r.Line + 1}");
        }

        return sb.ToString();
    }

    public static string List(IReadOnlyList<(StorySummary Story, IReadOnlyList<SpecRequirement> Specified)> rows, string repository, string? label)
    {
        var sb = new StringBuilder();
        var filter = string.IsNullOrEmpty(label) ? "all issues" : $"label `{label}`";
        var pending = rows.Count(r => r.Specified.Count == 0);
        sb.AppendLine($"# Stories in {repository} ({filter})");
        sb.AppendLine();
        sb.AppendLine($"{rows.Count} stories, {rows.Count - pending} with a spec, {pending} without.");
        sb.AppendLine();
        if (rows.Count == 0) return sb.ToString();

        sb.AppendLine("| Story | Title | State | Requirements |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var (story, specified) in rows)
        {
            var reqs = specified.Count == 0 ? "**none**" : string.Join(", ", specified.Select(r => $"{r.Id} ({r.Status})"));
            sb.AppendLine($"| [{story.Ref.Display}]({story.Ref.Url}) | {story.Title.Replace("|", "\\|")} | {story.State} | {reqs} |");
        }

        return sb.ToString();
    }

    /// <summary>Short label for a <c>Source:</c> value: "#3" for an issue of the default repository, else the value.</summary>
    public static string SourceLabel(string source, IStoryTracker? tracker) =>
        tracker != null && tracker.TryParse(source, out var reference) ? reference.Display : source;
}
