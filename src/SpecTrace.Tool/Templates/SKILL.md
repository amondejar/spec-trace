---
name: spectrace
description: Implements or changes functionality in this repository starting from the specs in specs/*.spec.md, leaving traceability with [Implements] and [Verifies]. Use it whenever the task mentions a requirement (ORD-001, PAY-API-12...), a spec or an acceptance criterion, or when you are about to add new behavior.
---

# Working with SpecTrace

This repository follows Spec-Driven Development. The specs in `specs/*.spec.md` are the source of truth;
code and tests declare which requirement they implement and which criterion they verify, and the compiler
checks it (diagnostics `ST0001`–`ST0011`).

## Before writing code

1. Find the requirement. If the task gives no ID, look in `specs/` for the one that matches.
   If there is none, **do not invent behavior**: first propose the requirement in the spec
   (new ID, EARS statement, `AC-n` criteria, `Status: proposed`) and ask for confirmation.
2. Load its context. If the `spectrace` MCP server is connected, use its tools:

   | Tool | What for |
   | --- | --- |
   | `get_requirement` | Statement, criteria with their status, where the code goes (project and `using`) and the exact markers |
   | `get_code` | Read the related code (`requirement_id`), where a type is declared (`type`) or one file (`path`) |
   | `list_requirements` | Which requirements exist; with `only_gaps: true`, only what is missing |
   | `propose_requirement` | Add a new requirement to a spec with status `proposed` |
   | `scaffold_tests` | Create test skeletons for criteria without tests |
   | `get_story` / `list_stories` | Read user stories from the issue tracker, and see which ones have no spec yet |

   Without MCP, the terminal equivalent is:

   ```bash
   dotnet spectrace context <ID>
   ```

## Before approving a spec

If you are asked to review a requirement, use the `review_spec` MCP prompt or follow it: contradictions between the
statement and the criteria, vague terms (ST0013), missing cases, conflicts with other requirements. Report; do not
edit the spec unless asked.

## Starting from a user story

If the task is a user story or an issue (#12, a GitHub issue URL) rather than a requirement, turn it into a spec first
and do not implement it in the same task:

1. Read it with `get_story` (it also tells you which requirements already cite it).
2. Check the related requirements (`list_requirements`, `get_requirement`) and code (`get_code`).
3. Propose it with `propose_requirement`, passing `source` (the story) and `open_questions` (whatever the story does
   not decide). One behaviour per requirement; criteria testable by a single test each; no invented rules.
4. Stop: a person answers the questions and approves the requirement.

The `story_to_spec` MCP prompt contains these steps in detail. `dotnet spectrace story <ref>` does the first step
from the terminal.

## While implementing

- Read the related code first and reuse its types: do not assume properties, setters or constructors.
- Mark the type or member that implements the requirement with `[Implements(Requirements.<ID_WITH_UNDERSCORES>)]`.
  Use the generated constants (`Requirements`, `AcceptanceCriteria`), never bare strings.
- Write one test per acceptance criterion with
  `[Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)]`. The test name describes the criterion.
- If you need skeletons: `dotnet spectrace scaffold-tests --project <test project folder> --requirement <ID>`.
  Generated tests have `Skip`; remove it when you implement them (a skipped test does not count as coverage).
- Do not change the text of an `approved` requirement to fit your code. If the spec is ambiguous
  or wrong, say so and propose the spec change separately.

## If the spec changed (ST0010 / ST0011)

If the build reports `ST0010` or `ST0011`, or `list_requirements` with `only_changed: true` returns the requirement,
its text changed after the last review. Check the code or tests named in the warning against the new text
and update them. **Do not run `dotnet spectrace lock`**: recording the review is a person's job.
Tell them what you reviewed and what you changed.

## After merging main

If the build reports `ST0007` (duplicate requirement ID) after bringing main into your branch, another branch took
the same "next ID". Run `dotnet spectrace ids --fix`: it renumbers only the requirement your branch added, with its
code and tests. Show the person the `git diff`.

## To finish

1. `dotnet build`: no `ST0001`, `ST0002`, `ST0003`, `ST0004` or `ST0005` may remain for the requirement.
2. `dotnet test`: all the requirement's tests pass.
3. `dotnet spectrace report` (or `list_requirements` with `only_gaps: true`): the requirement no longer shows as a gap.
4. In the change summary, list the requirements and criteria covered, by ID.

## Diagnostics quick reference

| ID | Meaning | What to do |
| --- | --- | --- |
| ST0001 | Requirement ID does not exist | Fix the ID or add the requirement to the spec |
| ST0002 | Criterion does not exist | Use one of the requirement's `AC-n` |
| ST0003 | Approved requirement not implemented | Implement it and mark it with `[Implements]` |
| ST0004 | Implemented without any test | Add tests with `[Verifies]` |
| ST0005 | Criterion without a test | Add the test for that criterion |
| ST0006 | Deprecated requirement referenced | Remove the reference |
| ST0010 | The statement changed after the last review | Review the implementation and tell a person |
| ST0011 | A criterion changed after the last review | Review its tests and tell a person |
| ST0012–ST0014 | The requirement is hard to test as written | Ask a person what it means; do not choose for them |
