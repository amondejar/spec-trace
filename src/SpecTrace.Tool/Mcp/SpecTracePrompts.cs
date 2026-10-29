using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SpecTrace.Tool.Mcp;

/// <summary>
/// MCP prompts: reusable instructions the client offers to the person (Claude Code shows them as slash commands,
/// for example <c>/mcp__spectrace__story_to_spec 12</c>). They work the same in any MCP client.
/// </summary>
public static class SpecTracePrompts
{
    private sealed record Prompt(string Name, string Title, string Description, string Argument, string ArgumentDescription, Func<string, string> Text);

    private static readonly Prompt[] All =
    {
        new("story_to_spec", "Turn a user story into a spec",
            "Reads a user story from the issue tracker and proposes a requirement with an EARS statement, testable acceptance criteria and the open questions the story leaves.",
            "story", "The story: #12, owner/repo#12 or the issue URL.",
            StoryToSpec),
        new("review_spec", "Review a spec before approving it",
            "Reviews a requirement (or every requirement of a spec file) for what makes it hard to build or test: ambiguity, statement and criteria that disagree, missing cases and conflicts with other requirements.",
            "target", "A requirement ID (BK-008) or a spec file (bookings.spec.md).",
            ReviewSpec),
        new("strengthen_tests", "Strengthen a requirement's tests",
            "Writes the tests that would catch the changes to a requirement's code that no test detects today (surviving mutants from Stryker).",
            "id", "Requirement ID, for example BK-002.",
            StrengthenTests),
        new("implement_requirement", "Implement a requirement",
            "Implements an approved requirement with one test per acceptance criterion, following the traceability rules of SpecTrace.",
            "id", "Requirement ID, for example ORD-002.",
            Implement),
    };

    public static JsonArray List() => new(All.Select(p => (JsonNode)new JsonObject
    {
        ["name"] = p.Name,
        ["title"] = p.Title,
        ["description"] = p.Description,
        ["arguments"] = new JsonArray(new JsonObject
        {
            ["name"] = p.Argument,
            ["description"] = p.ArgumentDescription,
            ["required"] = true,
        }),
    }).ToArray());

    public static bool Exists(string name) => All.Any(p => p.Name == name);

    /// <summary>The prompt's messages, or an <see cref="ArgumentException"/> if the argument is missing.</summary>
    public static JsonObject Get(string name, JsonObject? arguments)
    {
        var prompt = All.Single(p => p.Name == name);
        var value = arguments?[prompt.Argument] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : throw new ArgumentException($"Missing argument '{prompt.Argument}'.");

        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Text(value) },
            }),
        };
    }

    private static string StoryToSpec(string story) => $"""
        Turn the user story {story} into a requirement in this repository's specs. Do not implement anything.

        A user story says what someone needs; a requirement is the contract the code is checked against. Write that contract
        without deciding anything the story does not decide: a person approves it afterwards.

        1. Read the story with get_story ({story}). Read its comments too: decisions are often made there.
           If it already lists requirements that come from it, say so and propose changes to them instead of a duplicate.
        2. Read what exists: list_requirements, then get_requirement on the related requirements (the new one must be
           consistent with their rules), and get_code with type for the domain types it touches. Note what the domain does
           not support yet, without designing the solution.
        3. Write the requirement:
           - In the spec of its domain (a new spec file only for a new domain). Let propose_requirement pick the ID.
           - One behaviour per requirement: if the story asks for several, propose several requirements with the same source.
           - Statement: one EARS sentence (When ..., the system shall ... / If ..., then the system shall ...). Behaviour,
             not endpoints or classes.
           - Acceptance criteria: Given / when / then, one observable behaviour each, with concrete values, each testable by
             a single test. Include the story's own criteria and the edge cases the existing rules make obvious. Repeat a
             general rule (opening hours, permissions...) only if the new behaviour could bypass it.
           - Do not invent rules. If the story does not say what happens in a case that matters, it is an open question,
             not a criterion.
           Create it with propose_requirement, passing source = "{story}" and the open questions in open_questions.
           If the story is too vague for even one testable criterion, do not create a requirement: list the questions
           that would make it specifiable.
        4. Check with list_requirements that it is there with status proposed. Never change the status to approved and do
           not commit or open pull requests unless the person asks.

        Finish with: the requirement IDs and files, which criteria came from the story and which from existing rules,
        and the open questions.
        """;

    private static string ReviewSpec(string target) => $"""
        Review {target} before it is approved. Do not change any file: report what you find, so a person decides.

        1. Read it with get_requirement (for a spec file, list_requirements and then get_requirement on each requirement in
           it). Read the related requirements too, and get_code for the domain types it mentions.
        2. Look for, in this order:
           - Contradictions: the statement and the criteria disagree (a scope, a number, a unit, a limit), or two criteria
             cannot both be true.
           - Ambiguity: words that hide a number or a decision ("fast", "several", "appropriate"); an actor, a time or a
             unit that is not said. The "Spec quality" section of get_requirement lists the obvious ones (ST0012–ST0014).
           - Missing cases a tester would ask about: the error case of each criterion, boundaries (exactly the limit, one
             over), permissions (another user), empty input, what happens at the same time from two places.
           - Conflicts with other requirements: a rule that changes or bypasses an existing one (opening hours, capacity,
             who can cancel...).
           - Criteria that cannot be tested by a single test, or that test the implementation instead of the behaviour.
        3. For each finding give: where (ID and AC), what is wrong in one sentence, and a proposed rewrite of the
           statement or the criterion, or the question someone has to answer.

        Finish with a verdict: ready to approve, ready after small fixes, or needs decisions (list them).
        """;

    private static string StrengthenTests(string id) => $"""
        Strengthen the tests of requirement {id}: they pass, but some changes to its code would go unnoticed.

        1. Call get_requirement ({id}). Its "Changes the tests do not detect" section lists the surviving mutants from the
           latest Stryker report: where the code was changed and how (for example `<` became `<=`, or a call was removed).
           If there is no such section, say that a mutation report is needed: dotnet stryker --reporter json.
        2. Read the code at each location with get_code, and the existing tests of the requirement.
        3. For each surviving mutant, decide which acceptance criterion the behaviour belongs to and write (or tighten) a
           test of that criterion that would fail with the change: usually a boundary value, an assertion on a value
           that is not checked yet, or the error case. Mark it [Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)].
        4. If a mutant changes behaviour that no criterion describes, do not invent the rule: list it as a question for
           the person, since the spec may be missing a criterion.
        5. Do not change the production code. Run dotnet test; the person reruns Stryker to confirm.

        Finish with, for each mutant: the test that now catches it, or why it is left as a question.
        """;

    private static string Implement(string id) => $"""
        Implement requirement {id} in this repository.

        1. Call get_requirement ({id}) and read the related code with get_code before writing anything; reuse the existing
           types, constructors and members instead of guessing them.
        2. Mark the code that implements it with [Implements(Requirements.<ID with underscores>)] and write one test per
           acceptance criterion with [Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)]. A skipped test does not count.
        3. Do not change the text of the requirement to fit the code. If the spec is ambiguous or wrong, stop and say so.
        4. Finish only when dotnet build reports no SpecTrace warnings for {id} and dotnet test passes; list_requirements
           with only_gaps=true must not return it.
        5. Do not run dotnet spectrace lock: recording the review is a person's job.

        Finish with what you changed and the test that covers each criterion.
        """;
}
