<p align="center">
  <img src="https://raw.githubusercontent.com/amondejar/spec-trace/main/assets/icon.png" width="96" alt="SpecTrace">
</p>

# SpecTrace

Spec-Driven Development traceability for .NET. Your Markdown specs become a typed catalog of requirements, and the
C# compiler tells you what is not implemented, not tested, or out of date with the spec.

```text
specs/orders.spec.md(28,1): warning ST0003: Requirement 'ORD-003' (Apply discount coupon) is approved
  but no type or member has [Implements("ORD-003")]
```

SpecTrace links **spec → code → test** at build time, in the IDE, in pull requests and for AI coding agents:

- Roslyn analyzer and source generator: no extra build step, warnings show up next to the others.
- Detects spec changes after review (`spectrace.lock`): when a requirement's text changes, the build names the code and tests to review.
- `dotnet spectrace` CLI: traceability report, test scaffolding, import from Spec Kit and OpenSpec.
- User stories from GitHub Issues: each requirement can point to the story it comes from, and agents turn stories into
  proposed requirements without inventing what the story leaves open.
- MCP server and Agent Skill so coding agents start from the spec and stop only when the build is clean.

## How it works

1. Write specs as `*.spec.md` files with identified requirements (or import them from Spec Kit / OpenSpec):

   ```markdown
   ## ORD-001: Create order
   Status: approved

   When the customer submits a cart with at least one line,
   the system shall create an order in state Pending.

   - AC-1: Given a cart with 2 lines, when it is submitted, then an order with 2 lines is created.
   - AC-2: Given an empty cart, when it is submitted, then the request is rejected.
   ```

2. The generator creates `Requirements.ORD_001` and `AcceptanceCriteria.ORD_001.AC_1`.
3. Mark code and tests:

   ```csharp
   [Implements(Requirements.ORD_001)]
   public Order Create(IReadOnlyList<OrderLine> cart) { ... }

   [Fact]
   [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)]
   public void Create_with_empty_cart_is_rejected() { ... }
   ```

4. `dotnet build` checks traceability; `dotnet spectrace report` prints the full matrix.

`dotnet spectrace --version` prints the version of the tool. An option a command does not know is an error, with
the closest valid one suggested.

## Packages

| Package | Contents |
| --- | --- |
| `SpecTrace` | Roslyn analyzer + source generator, IDE code fixes and MSBuild props/targets. Brings in `SpecTrace.Abstractions`. |
| `SpecTrace.Abstractions` | The `[Implements]` and `[Verifies]` attributes. |
| `SpecTrace.Tool` | The `dotnet spectrace` CLI: `report`, `context`, `lock`, `scaffold-tests`, `import`, `mcp`, `init`. |

## Getting started

```bash
dotnet new tool-manifest               # local tool for the repo: invoked as "dotnet spectrace"
dotnet tool install SpecTrace.Tool --prerelease
dotnet spectrace init                  # spectrace.json, specs/example.spec.md, the agent skill and .mcp.json
dotnet add src/MyApp package SpecTrace --prerelease
dotnet add tests/MyApp.Tests package SpecTrace --prerelease
dotnet build
```

If you prefer a global install (`dotnet tool install -g SpecTrace.Tool --prerelease`), the command is `spectrace`
instead of `dotnet spectrace`, including in `.mcp.json`.

Optional MSBuild settings: `SpecTraceSpecsDir`, `SpecTraceNamespace`, `SpecTraceRole` (`Implementation` | `Tests`).
Set severities in a `.globalconfig` (`is_global = true`) rather than `.editorconfig`, because coverage diagnostics are
reported on the `.md` files.

## Spec format

A requirement is a level 2–4 heading `ID: Title`, an optional status line, an optional source line, a statement
(EARS recommended) and acceptance criteria `- AC-n: ...`.

```markdown
## BK-008: Move a booking
Status: proposed
Source: https://github.com/acme/room-booking/issues/3

> Open questions:
> - Can a booking be moved to another day?

When the organizer moves an upcoming booking to a free time range on the same day,
the system shall keep the booking with the new time range.

- AC-1: Given a booking from 10:00 to 11:00 and the room free at 12:00, when it is moved to 12:00–13:00, then it is moved.
```

`Source:` says where the requirement comes from: a user story (`#3`, `owner/repo#3`, its URL) or any address. It is
metadata, like `Status:`: changing it is not a spec change. The report links to it and the generated constant shows
it in the IDE tooltip. Blockquotes (`> ...`) are notes for people and are not part of the requirement either.

| Status | Meaning | Implementation and tests required |
| --- | --- | --- |
| `draft` / `proposed` | Under discussion | No |
| `approved` (default) | Agreed, must be built | Yes |
| `implemented` | Delivered | Yes |
| `deprecated` | Retired | No; referencing it is a warning |

## Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| [ST0001](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0001.md) | Error | Unknown requirement ID (code fix: closest existing ID) |
| [ST0002](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0002.md) | Error | Unknown acceptance criterion (code fix: a valid criterion) |
| [ST0003](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0003.md) | Warning | Approved requirement not implemented |
| [ST0004](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0004.md) | Warning | Implemented requirement without any test |
| [ST0005](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0005.md) | Info | Acceptance criterion without a test |
| [ST0006](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0006.md) | Warning | Deprecated requirement referenced (code fix: remove the reference) |
| [ST0007](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0007.md) | Error | Duplicate ID in the specs |
| [ST0008](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0008.md) | Warning | Spec format issue (no criteria, no statement, unknown status) |
| [ST0009](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0009.md) | Warning | Requirement ID in the attribute is not a constant |
| [ST0010](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0010.md) | Warning | The statement changed after review: review the implementation |
| [ST0011](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0011.md) | Warning | A criterion changed after review: review its tests |
| [ST0012](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0012.md) | Info | The statement does not say what the system *shall* do (EARS) |
| [ST0013](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0013.md) | Warning | Vague term that cannot be tested as written ("fast", "user-friendly", "etc.", "rápido"...) |
| [ST0014](https://github.com/amondejar/spec-trace/blob/main/docs/rules/ST0014.md) | Info | Acceptance criterion without an observable outcome (*then* ...) |

Tests marked `Skip` or `[Ignore]` do not count as coverage.

ST0012–ST0014 check how requirements are written, before anyone builds them: a statement without *shall*, words that
hide a number or a decision, criteria without an expected result. They run on `proposed` requirements too, because
that is when fixing the text is cheap. Contradictions between the statement and the criteria need a reader: the
`review_spec` MCP prompt asks an agent to review a requirement or a whole spec before it is approved.

## Spec changes (spectrace.lock)

A spec that changes after it was implemented is the case that slips through most often: code and tests stay green but
no longer do what the text says. SpecTrace stores in `spectrace.lock` a fingerprint of each requirement's statement and
criteria at the time they were reviewed, and warns when they change:

```bash
dotnet spectrace lock                          # record the current version of every active requirement
dotnet spectrace lock --requirement ORD-001    # after reviewing one requirement
dotnet spectrace lock --check                  # in CI: fail if there are unreviewed changes
```

| What changes | Diagnostic | Where | What to review |
| --- | --- | --- | --- |
| The statement | ST0010 | Implementation project, on the requirement line | Members with `[Implements]` |
| A criterion | ST0011 | Test project, on the criterion line | Tests with `[Verifies]` for that criterion |
| Title, status or whitespace | — | — | Nothing: behavior is unchanged |
| A new criterion | ST0005 if it has no test | — | Write its test |

Commit `spectrace.lock` together with the review. It has one line per requirement, so diffs are readable. Recording a
review is a human decision: the skill and the MCP server ask agents not to run `lock` on their own.

## In the IDE

Besides the code fixes listed above, on any `[Implements]` or `[Verifies]` (Ctrl+. in Visual Studio and VS Code,
Alt+Enter in Rider):

- **Use SpecTrace catalog constants**: `"ORD-001"` → `Requirements.ORD_001`, `"AC-1"` → `AcceptanceCriteria.ORD_001.AC_1`.
- **Generate tests for ORD-001 in MyApp.Tests**: creates `Specs/ORD_001_...Tests.cs` in the test project with one
  test (marked `Skip`) per criterion that has no test yet.

## In pull requests (GitHub Actions)

```yaml
permissions:
  contents: read
  pull-requests: write

jobs:
  spectrace:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet test --logger trx --results-directory TestResults
      - uses: amondejar/spec-trace@v0.7.1
        if: always()                 # also when tests fail: the report says which criteria broke
        with:
          root: .                    # folder containing spectrace.json
          test-results: TestResults  # .trx files: whether each criterion's tests pass
          # mutation-report: tests/MyApp.Tests/StrykerOutput   # optional: Stryker report, per requirement
          fail-on-gaps: false        # true to block the PR when there are gaps
          fail-on-drift: true        # block when the spec changed and nobody reviewed code and tests
```

On every pull request the Action:

- starts the report with the **requirements the pull request touches**: which requirements' spec changed (and how),
  which implementations and tests it edits, which requirements it removes and which changed code no requirement covers
  (input `impact`, on by default);
- shows whether each acceptance criterion's tests **pass** (✅ / ❌ / ⚪ not run) when given `test-results`, and
  annotates failing criteria on the spec line;
- annotates gaps and spec quality issues on the PR files, adds the report to the job summary and keeps one comment
  with it, updated on every push.

With `mutation-report`, it also shows per requirement how many changes to its code the tests detect, and annotates
the ones they do not. Outputs: `gaps`, `drift` and `failing` (criteria with a failing test). For other CI systems:
`dotnet spectrace report --test-results TestResults --base origin/main --fail-on-gaps --fail-on-failing`, in Markdown
or `--format json`.

### Requirements touched by a change

```bash
dotnet spectrace impact              # compared with origin/main (or --base <ref>)
```

```text
Compared with `origin/main`: 2 requirement(s) touched.

| Requirement             | Spec          | Code                  | Tests                                  | Source |
| BK-008 Move a booking   | new           | `BookingService.Move` | `BookingsApiTests.Moves_a_booking` (AC-1) | #3   |
| BK-002 Prevent double…  | –             | `BookingService.FindConflict` | –                              | –      |
```

A requirement is touched when its spec text changed against the base (statement, a criterion, its status) or when the
change edits lines of a member with `[Implements]` or a test with `[Verifies]` for it. Reviewers see the change as
behaviour, and the product owner knows what to check again. Changed code that no requirement covers is listed too.

### Mutation testing per requirement

A test with `[Verifies]` that passes may still check nothing. Mutation testing changes the code on purpose (`<` into
`<=`, a condition into `true`, a call removed) and looks for a test that fails. SpecTrace reads the report of
[Stryker.NET](https://stryker-mutator.io) and tells, per requirement, how many of the
changes to its code (the members with `[Implements]`) the tests detect, which ones survive, and which criteria's tests
detect none of them:

```bash
dotnet tool install dotnet-stryker                   # once, as a local tool
cd tests/MyApp.Tests && dotnet stryker --reporter json
cd ../.. && dotnet spectrace report --mutation tests/MyApp.Tests/StrykerOutput
```

```text
| BK-002 Prevent double booking | ... | 4/4 | 60% (6/10) ⚠️ |

**BK-002 Prevent double booking**: 60% (6/10)
- src/RoomBooking.Api/Domain/BookingService.cs:199 EqualityOperator: `<=` (survived)
- The tests of AC-4 detect none of the changes in this code.
```

Two Stryker.NET settings make the result sharper (`stryker-config.json` in the test project):

```json
{
  "stryker-config": {
    "reporters": ["json", "html"],
    "disable-bail": true,
    "ignore-mutations": ["string"]
  }
}
```

- `disable-bail` runs every test against each mutant instead of stopping at the first one that fails. It is slower,
  but then the report knows *every* test that detects a change, and "the tests of AC-2 detect none of the changes" is
  exact. Without it, SpecTrace only says that a criterion's tests were never the first to fail.
- `ignore-mutations: ["string"]` skips changes to string literals, which in most services are error messages that the
  tests do not (and need not) compare word by word.

Each surviving mutant is a test that should exist: the PR gets a warning on that line of code. `--min-mutation-score
<n>` fails when a requirement is below n %. The MCP tool `get_requirement` finds the newest report in a
`StrykerOutput` folder by itself, and the `strengthen_tests` prompt asks an agent to write the tests that catch the
surviving mutants, without touching the production code and without inventing rules for behaviour no criterion
describes. Stryker is slow on big code bases: run it nightly or before a release rather than on every push.

### Test results

`dotnet spectrace report --test-results <path>` reads the `.trx` files written by `dotnet test --logger trx` (xUnit,
NUnit and MSTest) and shows, for every criterion, whether its tests passed, failed or did not run. A theory fails if
any of its rows fails; a `[Verifies]` on a test class takes every test of the class. `--fail-on-failing` returns exit
code 1 when a criterion has a failing test. SpecTrace never runs the tests: it reads their results.

## For AI coding agents

### Skill

`dotnet spectrace init` installs `.claude/skills/spectrace/SKILL.md` (Agent Skills format; use `--skills-dir` to place
it where your agent reads skills). The skill tells the agent to start from the spec, load the requirement before
implementing, read the existing code instead of guessing types, and not to consider the work done while ST
diagnostics remain.

### MCP server

`dotnet spectrace mcp` starts a [Model Context Protocol](https://modelcontextprotocol.io) server over stdio, with no
external dependencies:

| Tool | Kind | What it does |
| --- | --- | --- |
| `list_requirements` | read | Requirements with their traceability status; `only_gaps` for what is missing, `only_changed` for unreviewed spec changes |
| `get_requirement` | read | Statement, criteria with status, current implementation, target project and `using`, related files and the exact attributes to use |
| `get_code` | read | Code related to a requirement, where a type is declared, or a given file (only `.cs`, `.csproj` and `.md` under the code and spec folders) |
| `get_traceability_report` | read | Full report in Markdown or JSON |
| `scaffold_tests` | write | Test skeletons for criteria without tests (never overwrites) |
| `propose_requirement` | write | Appends a `proposed` requirement to a spec, with its `source` and `open_questions`; a person approves it later |
| `get_story` | read | A user story from GitHub Issues: user story, acceptance criteria, notes, comments and the requirements that cite it |
| `list_stories` | read | User stories and the requirements that come from each one; `only_unspecified` for the stories without a spec |

It also offers MCP prompts, which Claude Code shows as slash commands:

| Prompt | Argument | What it asks the agent |
| --- | --- | --- |
| `story_to_spec` | `story` | Turn a user story into proposed requirements: EARS statement, testable criteria, open questions, no invented rules |
| `review_spec` | `target` | Review a requirement or a spec file before approving it: contradictions, ambiguity, missing cases, conflicts |
| `strengthen_tests` | `id` | Write the tests that catch the changes to a requirement's code no test detects (Stryker's surviving mutants) |
| `implement_requirement` | `id` | Implement a requirement with one test per criterion and stop when the build is clean |

Paths are confined to the repository and the spec folder; `get_code` never reads `bin`, `obj`, `.git`, configuration
or secrets. The server implements the `initialize` handshake (protocol revisions 2024-11-05 to 2025-11-25); clients
that prefer revision 2026-07-28 fall back to it automatically.

`dotnet spectrace init` creates `.mcp.json` (Claude Code and other clients that read it). For VS Code,
`.vscode/mcp.json`:

```json
{
  "servers": {
    "spectrace": { "type": "stdio", "command": "dotnet", "args": ["spectrace", "mcp"] }
  }
}
```

### Microsoft Agent Framework demo

[`samples/AgentDemo`](https://github.com/amondejar/spec-trace/tree/main/samples/AgentDemo) is an Agent Framework agent
that connects to this MCP server, studies a requirement and proposes the missing code and tests. It works with any
OpenAI-compatible endpoint (OpenAI, Ollama, LM Studio, Claude's compatibility endpoint…).

## Import from Spec Kit and OpenSpec

```bash
dotnet spectrace import speckit specs/              # each specs/NNN-feature/spec.md → spectrace.spec.md next to it
dotnet spectrace import openspec openspec/          # each openspec/specs/<cap>/spec.md → specs/<cap>.spec.md
```

| Source | Becomes |
| --- | --- |
| Spec Kit: User Story N with its Acceptance Scenarios | `PA1-US-N` with one `AC-n` criterion per scenario (`approved`) |
| Spec Kit: `**FR-001**` | `PA1-FR-001` as `proposed` (Spec Kit gives them no criteria of their own) |
| Spec Kit: `[NEEDS CLARIFICATION]` | The requirement stays `proposed`, with a note |
| OpenSpec: `### Requirement:` and its `#### Scenario:` | `AUTH-001` with one criterion per scenario |
| OpenSpec: `openspec/changes/` | Not imported: unarchived proposals |

The prefix comes from the feature folder (`001-photo-albums` → `PA1`) or the capability (`auth` → `AUTH`) and can be
set with `--prefix`. Re-importing is safe: IDs (matched by title in OpenSpec), statuses you changed and criteria you
added by hand to requirements without criteria are kept. `--dry-run` shows what would happen.
Examples in [`samples/Import`](https://github.com/amondejar/spec-trace/tree/main/samples/Import).

## Working as a team

Everything SpecTrace knows lives in the repository (specs, `spectrace.lock`, attributes), so several people and their
agents work with it the way they work with any code: branches, pull requests and reviews.

- **Approving a spec is reviewing a pull request.** Changing `Status: proposed` to `approved` goes through a PR, and
  git records who approved it. Require the right reviewers with `CODEOWNERS`:

  ```text
  /specs/          @product-owner @tech-lead
  /spectrace.lock  @tech-lead
  ```

- **The build is the gate.** With the GitHub Action (`fail-on-gaps`, `fail-on-drift`) and branch protection, nothing
  is merged with approved requirements that are not implemented, criteria without tests or unreviewed spec changes.

Two things happen when branches run in parallel, and SpecTrace handles both:

**Reviews from two branches.** Two branches that review different requirements add neighbouring lines to
`spectrace.lock`. `dotnet spectrace init` adds `spectrace.lock merge=union` to `.gitattributes`, so local merges and
rebases keep both lines instead of reporting a conflict. GitHub's web merge does not apply merge drivers: if a pull
request shows a conflict in `spectrace.lock`, update the branch locally (`git merge origin/main`), or keep both sides
in the web editor. Either way it is safe: when a requirement has several lines, the one that matches the spec text
is used, and leftover conflict markers are reported (ST0008) instead of breaking anything. `dotnet spectrace lock
--tidy` leaves one line per requirement without reviewing anything.

**Two branches propose the same ID.** Both take "the next ID" of a spec, and after merging the build reports ST0007.

```bash
dotnet spectrace ids          # IDs used twice, compared with origin/main (exit code 1)
dotnet spectrace ids --fix    # renumbers what this branch added
```

`--fix` compares the branch with its base through git and renumbers only the requirement this branch added: its
heading, its `spectrace.lock` line, and the references (`Requirements.BK_009`, `AcceptanceCriteria.BK_009.AC_1`,
`"BK-009"`) on the lines of code and tests this branch added. The other requirement and its code keep their ID. It
also works before merging, when main already uses the ID. Review the result with `git diff`. To make collisions
rarer, `propose_requirement` skips the IDs already used on the base branch.

## User stories (GitHub Issues)

Work often starts as a user story in the issue tracker. SpecTrace reads it, so that the spec, not the ticket, is what
the code is checked against, and every requirement keeps a link to the story it came from.

```bash
dotnet spectrace story #12            # the story, its acceptance criteria, comments, and the requirements that cite it
dotnet spectrace stories --unspecified # stories with the "user-story" label that no requirement cites yet
```

With an agent, the `story_to_spec` prompt (in Claude Code, `/mcp__spectrace__story_to_spec 12`) reads the story with
`get_story`, checks the existing requirements and code, and proposes requirements with `Source:` pointing to the story
and the open questions in a note. It does not approve or implement anything: a person reviews the proposal, answers
the questions and changes the status to `approved`, and from then on the build enforces it.

Configuration in `spectrace.json` (all optional):

```json
{
  "trackers": {
    "github": {
      "repository": "acme/room-booking",
      "label": "user-story"
    }
  }
}
```

- `repository`: default `GITHUB_REPOSITORY`, then the `origin` remote of the git repository.
- `label`: issues that are user stories, for `stories` / `list_stories`; `""` for all issues.
- `url` / `apiUrl`: for GitHub Enterprise Server (`https://github.example.com`, API at `<url>/api/v3`).

Public repositories work without credentials. For private ones, SpecTrace uses `GITHUB_TOKEN` or `GH_TOKEN`, or the
token of a logged-in GitHub CLI (`gh auth login`); the token only needs read access to issues and is only sent to the
configured GitHub API. Nothing in the build calls the network: stories are read only by `story`, `stories` and the
MCP tools. A GitHub issue form for stories (As a / I want / So that / Acceptance criteria) gives the best results;
see [room-booking](https://github.com/amondejar/room-booking/blob/main/.github/ISSUE_TEMPLATE/user-story.yml)
for an example. Jira is next.

## Repository layout

```text
src/SpecTrace.Abstractions   attributes (netstandard2.0)
src/SpecTrace.Analyzers      spec parser, generator, analyzer, build/*.props|targets (netstandard2.0)
src/SpecTrace.CodeFixes      IDE code fixes and refactorings (netstandard2.0)
src/SpecTrace.Package        packs analyzer + code fixes as the "SpecTrace" package
src/SpecTrace.Tool           dotnet spectrace CLI, MCP server and story trackers (net8.0, RollForward Major)
tests/SpecTrace.Tests        tests for parser, generator, analyzer, code fixes, MCP, importers and lock
docs/rules                   one page per diagnostic
action.yml                   GitHub Action for pull requests
samples/Orders               full example with intentional gaps (ORD-003 not implemented, ORD-002 AC-2 untested)
                             and one unreviewed change (ORD-001 AC-2 changed after spectrace.lock)
samples/Import               Spec Kit and OpenSpec example specs to try the importers
samples/AgentDemo            Microsoft Agent Framework agent using the MCP server
```

## Development

```bash
dotnet test tests/SpecTrace.Tests
dotnet build samples/Orders/tests/Orders.Tests     # shows ST0003, ST0005 and ST0011
dotnet run --project src/SpecTrace.Tool -- report --root samples/Orders
dotnet pack src/SpecTrace.Abstractions -c Release -o artifacts
dotnet pack src/SpecTrace.Package -c Release -o artifacts     # the "SpecTrace" package
dotnet pack src/SpecTrace.Tool -c Release -o artifacts
```

Releases are published to nuget.org from GitHub Actions with Trusted Publishing when a `v*` tag is pushed
(`.github/workflows/release.yml`).

## License

[MIT](https://github.com/amondejar/spec-trace/blob/main/LICENSE)
