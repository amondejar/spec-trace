# The story flow

One user story, from the issue to the merge, in six steps. You only need to remember one command:

```bash
dotnet spectrace next
```

It reads the branch, the specs, the reviews in `spectrace.lock` and (with the GitHub CLI) the pull request, and says
which step the story is in and the exact command for the next one.

| Step | Who | What you do | Command |
| --- | --- | --- | --- |
| 1. Start | PO, then developer | Write the story in an issue (label `user-story`); create its branch | `dotnet spectrace start #12` |
| 2. Spec | Agent | Turn the story into proposed requirements, with open questions | `/mcp__spectrace__story_to_spec #12` (Claude Code) |
| 3. Approve | PO and developer | Answer the questions in the issue, turn the answers into criteria, approve the whole story | `dotnet spectrace approve` |
| 4. Agent | Agent | Commit, push, pull request and label; the agent implements and comments | `dotnet spectrace submit` |
| 5. Review | Developer | Review the agent's code and tests; record the review and push it | `dotnet spectrace accept` |
| 6. Merge | Developer | Merge the pull request on GitHub when CI is green | (GitHub) |

## What the commands do for you

- **start** creates `spec/gh-<n>-<title>` from the default branch (or switches to it if it exists). It refuses with
  uncommitted changes, so a story never starts on top of unrelated work.
- **approve** changes every `proposed` requirement of the story to `approved` and removes the "Proposed by an agent"
  note. It refuses while a requirement still lists open questions: a story is approved whole, never by halves. A
  requirement the story should not have: set `Status: deprecated` first.
- **submit** commits only the spec folder, pushes, opens the pull request (`Closes #12`) and adds the
  `spectrace:implement` label that starts the agent (see `init --agent`). It refuses when the branch's pull request is
  already merged: new work needs a new pull request, so code never stays behind on a merged branch. Without the GitHub
  CLI (`gh auth login`), it pushes and prints the two things to do on github.com.
- **accept** pulls the agent's commits, checks that every requirement of the story has code and a test per criterion,
  records the review of all of them in `spectrace.lock`, commits and pushes. Nothing to type by ID.

CI stays red between steps 3 and 5 (ST0003: approved requirements without code). That is expected: the pull request
is merged once, with the spec, the code, the tests and the review together.
