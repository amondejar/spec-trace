using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SpecTrace.Specs;

namespace SpecTrace.Tool.Flow;

/// <summary>Small, line-based edits of a requirement in its spec file: what approve needs, and nothing else.</summary>
public static class SpecEdits
{
    private static readonly Regex Heading = new(@"^\s{0,3}(?<hashes>#{1,6})\s", RegexOptions.Compiled);
    private static readonly Regex Status = new(@"^(?<lead>\s*(?:[-*>]\s*)?\*{0,2}status\*{0,2}\s*:\s*\*{0,2}\s*`?)(?<status>[A-Za-z-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuestionsHeader = new(@"^\s*>\s*\*{0,2}(open questions|preguntas abiertas)\*{0,2}\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuestionItem = new(@"^\s*>\s*[-*]\s+(?<text>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex AgentNote = new(@"^\s*>\s*Proposed by an agent\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The questions still listed under "Open questions:" in the requirement's notes.</summary>
    public static IReadOnlyList<string> OpenQuestions(string text, SpecRequirement requirement)
    {
        var lines = Lines(text);
        var (start, end) = Block(lines, requirement.Line);
        var questions = new List<string>();
        for (var i = start; i < end; i++)
        {
            if (!QuestionsHeader.IsMatch(lines[i])) continue;
            for (var j = i + 1; j < end; j++)
            {
                var item = QuestionItem.Match(lines[j]);
                if (!item.Success) break;
                questions.Add(item.Groups["text"].Value);
            }
        }

        return questions;
    }

    /// <summary>
    /// Sets the requirement's status, adding the Status line if it has none, and drops the "Proposed by an agent" note,
    /// which no longer applies once a person approves it.
    /// </summary>
    public static string Approve(string text, SpecRequirement requirement)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Lines(text).ToList();
        var (start, end) = Block(lines, requirement.Line);

        var statusLine = -1;
        for (var i = start + 1; i < end; i++)
        {
            if (Status.IsMatch(lines[i]))
            {
                statusLine = i;
                break;
            }
        }

        if (statusLine >= 0) lines[statusLine] = Status.Replace(lines[statusLine], m => m.Groups["lead"].Value + SpecStatus.Approved, 1);
        else lines.Insert(start + 1, "Status: " + SpecStatus.Approved);

        // The note (and a blank line left alone after it) goes away.
        (start, end) = Block(lines, requirement.Line);
        for (var i = end - 1; i > start; i--)
        {
            if (!AgentNote.IsMatch(lines[i])) continue;
            lines.RemoveAt(i);
            if (i < lines.Count && lines[i].Trim().Length == 0 && i - 1 >= 0 && lines[i - 1].Trim().Length == 0) lines.RemoveAt(i);
        }

        return string.Join(newline, lines);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>From the requirement's heading to the next heading of the same or a higher level.</summary>
    private static (int Start, int End) Block(IReadOnlyList<string> lines, int headingLine)
    {
        var level = Heading.Match(lines[headingLine]) is { Success: true } h ? h.Groups["hashes"].Value.Length : 2;
        var end = lines.Count;
        var inFence = false;
        for (var i = headingLine + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (inFence) continue;
            var m = Heading.Match(lines[i]);
            if (m.Success && m.Groups["hashes"].Value.Length <= level)
            {
                end = i;
                break;
            }
        }

        return (headingLine, end);
    }
}
