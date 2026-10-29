# Changelog

## 0.6.0-preview.1

### Added

- **Test results per criterion.** `report --test-results <path>` reads `.trx` files (`dotnet test --logger trx`;
  xUnit, NUnit, MSTest) and shows whether each criterion's tests passed, failed or did not run: icons per test and per
  requirement, "Acceptance criteria passing" in the summary, a "Failing criteria" section with the failure message,
  `result`/`outcome` in the JSON report and error annotations on the criterion line. Theory rows combine (one failure
  fails it); `[Verifies]` on a test class takes all its tests. `--fail-on-failing`.
- **Requirements touched by a change.** `dotnet spectrace impact [--base <ref>]` and `report --base <ref>`: spec
  changes against the base (new, statement, criteria added/changed/removed, status), members with `[Implements]` and
  tests with `[Verifies]` on changed lines (deletions included), removed requirements and changed code that no
  requirement covers. Works on pull request merge commits checked out shallow.
- **Spec quality.** ST0012 (statement without *shall*, EARS), ST0013 (vague terms such as "fast", "user-friendly",
  "etc.", "and/or", "rápido", "y/o"... in statements and criteria; warning) and ST0014 (criterion without an outcome).
  Also in the report ("Spec quality"), the JSON (`specQuality`), the PR annotations and `get_requirement`.
- MCP prompt `review_spec`: contradictions, ambiguity, missing cases and conflicts, before approving.
- GitHub Action: inputs `test-results` and `impact` (on by default for pull requests), output `failing`.

### Changed

- The Markdown report puts the matrix under "All requirements" (after the impact section when there is one).
- `init` in a subfolder uses the existing `spectrace.json` above it, or the root of the git repository, instead of
  creating a second setup (`--here` to force the current folder), and only adds the example spec when there are no
  specs yet.

## 0.5.1-preview.1

### Added

- **Team work: merge-safe `spectrace.lock`.**
  - When a requirement has several lines (one per branch after a merge), the one that matches the spec text is used:
    a version counts as reviewed if any recorded review matches it. Before, the last line won.
  - Git conflict markers left in `spectrace.lock` are reported (ST0008 on the lock, `lock --check` fails) and both
    sides are still read.
  - `dotnet spectrace lock --tidy`: one line per requirement, keeping the review that matches the spec; no review is
    added or changed. Any `lock` also tidies the file.
  - `dotnet spectrace init` adds `spectrace.lock merge=union` to `.gitattributes`, so local merges and rebases keep
    the reviews of both branches without a conflict.
- **`dotnet spectrace ids`**: requirement IDs used twice, typically by two branches that proposed "the next ID".
  `--fix` compares the branch with its base (`--base`, default `origin/main`) through git and renumbers only what the
  branch added: the requirement heading, its `spectrace.lock` line, and the references on the lines of code and tests
  the branch added. Works after merging main and before.
- `propose_requirement` skips IDs already used on the base branch, and rejects an explicit ID that is.
- README: "Working as a team" (CODEOWNERS, branch protection, parallel branches).

## 0.5.0-preview.1

### Added

- **User stories from GitHub Issues.**
  - `dotnet spectrace story <ref>`: a story (`#12`, `owner/repo#12` or its URL) with its user story, acceptance
    criteria, notes, comments and the requirements that cite it. `--format json`.
  - `dotnet spectrace stories`: the stories (issues with the `user-story` label by default) and the requirements that
    come from each one; `--unspecified`, `--state`, `--label`, `--fail-on-unspecified`, `--format json`.
  - MCP tools `get_story` and `list_stories`.
  - Reads GitHub issue forms (`### As a`, `### I want`, `### So that`, `### Acceptance criteria`...), free-form issues
    and comments. Pull requests are not stories.
  - Configuration in `spectrace.json` (`trackers.github`: `repository`, `label`, `url`, `apiUrl` for GitHub Enterprise
    Server). The repository defaults to `GITHUB_REPOSITORY`, then the `origin` remote. Credentials from `GITHUB_TOKEN`,
    `GH_TOKEN` or a logged-in GitHub CLI; public repositories work without them. The build never calls the network.
  - `dotnet spectrace init` adds `trackers.github` when the repository has a GitHub remote.
- **`Source:` line in the spec format**: where a requirement comes from (a story reference or any URL), also accepted
  as `> Source: ...` in a note and as a Markdown link. It is metadata: it is not part of the reviewed text, so adding or
  changing it does not trigger ST0010/ST0011. The report links it next to the title (`· [#12](...)`), the JSON report
  and `get_requirement` include it, and the generated constant has a `<seealso>` link to it.
- `propose_requirement` accepts `source` (written as a `Source:` line, story references normalized to their URL) and
  `open_questions` (written in the note for the reviewer), and says when the same story is already cited.
- **MCP prompts**: `story_to_spec` (turn a story into proposed requirements without inventing rules) and
  `implement_requirement`. Claude Code shows them as `/mcp__spectrace__story_to_spec` and
  `/mcp__spectrace__implement_requirement`.

### Fixed

- The `GeneratedCode` attribute of the catalog now carries the package version (it said 0.4.0 in 0.4.1).
- Two MCP error messages were still in Spanish.

## 0.4.1-preview.1

### Changed

- Everything is now in English: diagnostics, CLI output, reports, MCP tool descriptions, the agent skill, templates,
  generated code comments, documentation and samples.
- Package icon, copyright, repository type and release notes link in the NuGet metadata.
- The README uses absolute links so they work on nuget.org.

### Added

- `.github/workflows/release.yml`: publishes the three packages to nuget.org with Trusted Publishing when a `v*` tag
  is pushed, and creates a GitHub release with the `.nupkg` files.

## 0.4.0-preview.1

### Added

- **Spec change detection.** `spectrace.lock` stores a fingerprint of each reviewed requirement's statement and
  criteria. If the text changes afterwards:
  - **ST0010** (implementation project): the statement changed; names the members with `[Implements]` to review.
  - **ST0011** (test project): a criterion changed; names the tests with `[Verifies]` to review.
  Title, status and whitespace do not count. A new criterion is not a change (if it has no test, ST0005).
- `dotnet spectrace lock` (all active requirements), `lock --requirement <ID>` (repeatable) and `lock --check` (for CI).
- Report: summary line and a "Spec changes since the last review" section; JSON `drift` per requirement;
  ST0010/ST0011 annotations in `--format github`; `report --fail-on-drift`.
- GitHub Action: `drift` output and `fail-on-drift` input.
- MCP: `list_requirements` with `only_changed` and a `spec_changed` field; `get_requirement` explains what changed and
  asks agents not to record the review without human approval.
- The sample ships a `spectrace.lock` with one unreviewed change (ORD-001 AC-2) to show ST0011.
- Documentation for ST0010 and ST0011.

## 0.3.1-preview.1

What an agent without its own repository access (such as the Agent Framework demo) needed to propose code that
compiles: the first real run guessed types and namespaces that did not exist.

### Added

- MCP tool **`get_code`** (read-only): code related to a requirement (or, if it has none yet, the code of other
  requirements in the same spec), the files that declare a type, or a given file. Only `.cs`, `.csproj` and `.md`
  under the code and spec folders; never `bin`, `obj`, `.git` or configuration.
- `get_requirement` and `dotnet spectrace context` say **where the code goes**: each project with the `using` of its
  generated catalog (`Orders.Specs`, `Orders.Tests.Specs`), plus the **related files** worth reading.
- The Agent Framework demo uses `get_code` and its instructions forbid guessing types. Documented use with Claude.

### Fixed

- The MCP server reads and writes UTF-8 regardless of the console code page: on Windows, accented characters sent by
  the client (for example in `propose_requirement`) could arrive garbled.
- JSON output (MCP and `report --format json`) no longer escapes non-ASCII characters.
- Re-import test independent of line endings (failed on Windows) and analyzer release tracking (RS2008).
- Documentation: install `SpecTrace.Tool` as a local tool to invoke it as `dotnet spectrace`.

## 0.3.0-preview.1

### Added

- **MCP server** (`dotnet spectrace mcp`): stdio, JSON-RPC 2.0, no dependencies. Tools `list_requirements`,
  `get_requirement`, `get_traceability_report` (read) and `scaffold_tests`, `propose_requirement` (write, with paths
  confined to the repository). Protocol revisions 2024-11-05 to 2025-11-25; answers "method not found" to
  `server/discover` so clients of revision 2026-07-28 fall back to `initialize`.
- **Import** from GitHub Spec Kit (`import speckit`) and OpenSpec (`import openspec`), re-importable: keeps IDs,
  statuses and criteria added by hand.
- **Microsoft Agent Framework demo** in `samples/AgentDemo` (outside the solution).
- `init` also creates `.mcp.json`. The skill explains the MCP tools.
- Import samples in `samples/Import`.

### Changed

- The parser ignores blockquotes (`> ...`) when reading the statement: use them for notes.
- `SpecTrace.Tool` references `SpecTrace.Analyzers` instead of compiling its files, so the tests can use it.

## 0.2.0-preview.1

### Added

- **IDE code fixes** (new `SpecTrace.CodeFixes` assembly, shipped in the `SpecTrace` package):
  - ST0001: *Change to 'ORD-001'*, with up to three similar existing IDs.
  - ST0002: *Change to 'AC-n'*, one option per valid criterion of the requirement.
  - ST0006: *Remove the reference to the deprecated requirement*, keeping XML doc comments.
- **Refactorings** on `[Implements]` / `[Verifies]`:
  - *Use SpecTrace catalog constants*: `"ORD-001"` → `Requirements.ORD_001` and `"AC-1"` → `AcceptanceCriteria.ORD_001.AC_1`, adding the `using`.
  - *Generate tests for ORD-001 in X.Tests*: creates `Specs/ORD_001_...Tests.cs` in each test project that references
    the project, only for criteria that have no test yet.
- **GitHub Action** (`action.yml` at the root): annotations on the PR files, job summary, a comment with the matrix
  updated on every push, `gaps` output and `fail-on-gaps` option.
- CLI: `report --format github` (GitHub Actions annotations) and `report --link-base <url>` (absolute links in Markdown).
- One documentation page per rule in `docs/rules/ST000x.md` (the diagnostics' help links point there).
- Repository CI workflow: build, tests, pack and the Action run against the sample.

### Changed

- The `SpecTrace` package is now built from `src/SpecTrace.Package` (analyzer + code fixes). `SpecTrace.Analyzers` is
  no longer packed on its own: use `dotnet pack src/SpecTrace.Package`.
- ST0001 and ST0002 carry suggestions in `Diagnostic.Properties` (`SpecTrace.Suggestions`) for external tools.
- The test template is shared between the CLI and the IDE (`TestSkeleton`).

## 0.1.0-preview.1

- First version: Markdown spec parser with identified requirements, generated typed catalog, analyzer with rules
  ST0001–ST0009, `dotnet spectrace` CLI (`report`, `context`, `scaffold-tests`, `init`), agent skill and the
  `samples/Orders` example.
