using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpecTrace.Tool.Stories;

/// <summary>A story in a tracker: <c>github</c> + <c>amondejar/room-booking#3</c> + its web address.</summary>
public sealed record StoryRef(string Tracker, string Key, string Url)
{
    /// <summary>Short form for people: <c>#3</c> for the default repository, the key otherwise.</summary>
    public string Display { get; init; } = Key;

    public bool SameAs(StoryRef other) =>
        string.Equals(Tracker, other.Tracker, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}

public sealed record StoryComment(string Author, DateTimeOffset CreatedAt, string Body);

/// <summary>A "### Heading" block of the story body (GitHub issue forms render their fields that way).</summary>
public sealed record StorySection(string Heading, string Text);

/// <summary>A user story read from a tracker, normalized so every tracker looks the same to specs and agents.</summary>
public sealed class Story
{
    public Story(StoryRef reference, string title, string state, IReadOnlyList<string> labels, string? author, string body,
        IReadOnlyList<StoryComment> comments)
    {
        Ref = reference;
        Title = title;
        State = state;
        Labels = labels;
        Author = author;
        Body = body;
        Comments = comments;
        Sections = StoryText.Sections(body);
        AcceptanceCriteria = StoryText.Criteria(Section("acceptance criteria", "criterios de aceptación", "criterios de aceptacion"));
    }

    public StoryRef Ref { get; }
    public string Title { get; }
    public string State { get; }
    public IReadOnlyList<string> Labels { get; }
    public string? Author { get; }

    /// <summary>The description as written (Markdown).</summary>
    public string Body { get; }

    public IReadOnlyList<StoryComment> Comments { get; }

    /// <summary>"### Heading" blocks; empty for free-form descriptions.</summary>
    public IReadOnlyList<StorySection> Sections { get; }

    /// <summary>Criteria listed in an "Acceptance criteria" section, one per item.</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; }

    public string? AsA => Section("as a", "as an", "como");
    public string? IWant => Section("i want", "quiero");
    public string? SoThat => Section("so that", "para", "para que");

    /// <summary>"As a ..., I want ..., so that ..." when the story has the three parts.</summary>
    public string? Narrative =>
        AsA != null && IWant != null && SoThat != null
            ? $"As {Article(AsA)}, I want {StoryText.OneLine(IWant)}, so that {StoryText.OneLine(SoThat)}."
            : null;

    public string? Section(params string[] headings) =>
        Sections.FirstOrDefault(s => headings.Any(h => string.Equals(StoryText.Key(s.Heading), h, StringComparison.OrdinalIgnoreCase)))?.Text;

    private static string Article(string role)
    {
        var r = StoryText.OneLine(role);
        if (Regex.IsMatch(r, @"^(a|an|the)\s", RegexOptions.IgnoreCase)) return r;
        return ("aeiou".Contains(char.ToLowerInvariant(r.FirstOrDefault())) ? "an " : "a ") + r;
    }
}

/// <summary>Plain-text helpers for story bodies.</summary>
public static class StoryText
{
    private static readonly Regex Heading = new(@"^\s{0,3}#{2,4}\s+(?<text>.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(?<text>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>GitHub issue forms write this for optional fields left empty.</summary>
    public const string NoResponse = "_No response_";

    public static IReadOnlyList<StorySection> Sections(string body)
    {
        var sections = new List<StorySection>();
        string? heading = null;
        var text = new List<string>();
        var inFence = false;

        void Flush()
        {
            if (heading == null) return;
            var value = string.Join("\n", text).Trim();
            sections.Add(new StorySection(heading, value == NoResponse ? string.Empty : value));
        }

        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            var m = inFence ? Match.Empty : Heading.Match(line);
            if (m.Success)
            {
                Flush();
                heading = m.Groups["text"].Value.Trim();
                text.Clear();
            }
            else if (heading != null)
            {
                text.Add(line);
            }
        }

        Flush();
        return sections;
    }

    /// <summary>One criterion per list item; without a list, one per non-empty line.</summary>
    public static IReadOnlyList<string> Criteria(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return Array.Empty<string>();
        var lines = section!.Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var bullets = lines.Select(l => Bullet.Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value).ToList();
        return bullets.Count > 0 ? bullets : lines.Select(l => l.Trim()).ToList();
    }

    public static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>"I want" → "i want": headings compared without case, trailing colon or emphasis.</summary>
    public static string Key(string heading) => OneLine(heading.Trim().Trim('*', '_', ':').Trim()).ToLowerInvariant();
}

/// <summary>A story could not be read: not found, no access, rate limit, network. The message says what to do.</summary>
public sealed class StoryException : Exception
{
    public StoryException(string message, Exception? inner = null) : base(message, inner) { }
}
