# Demo: SpecTrace + Microsoft Agent Framework

A [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) agent that connects to the SpecTrace MCP
server, studies a requirement from `samples/Orders` and proposes the missing code and tests with the right
`[Implements]` / `[Verifies]` attributes.

## Run

```bash
# 1. Build the CLI: the demo starts its MCP server from bin/
dotnet build src/SpecTrace.Tool

# 2. Configure the model (any OpenAI-compatible endpoint)
export OPENAI_API_KEY=sk-...                    # OpenAI
# or Claude, through the Claude API's OpenAI-compatible endpoint (for testing, not production):
export OPENAI_API_KEY=sk-ant-...
export OPENAI_BASE_URL=https://api.anthropic.com/v1/
export OPENAI_MODEL=claude-sonnet-5-5
# or locally with Ollama:
export OPENAI_BASE_URL=http://localhost:11434/v1
export OPENAI_MODEL=qwen2.5-coder:14b           # a model with tool support

# 3. Run the agent on a requirement
dotnet run --project samples/AgentDemo -- ORD-002
dotnet run --project samples/AgentDemo -- ORD-003 --allow-writes
```

| Option | Effect |
| --- | --- |
| `<ID>` | Requirement to work on (default `ORD-002`). |
| `--allow-writes` | Also gives the agent `scaffold_tests` and `propose_requirement`. Without it the agent only reads (`list_requirements`, `get_requirement`, `get_code`, `get_traceability_report`). |
| `--root <dir>` | Folder containing `spectrace.json` (default `samples/Orders`). |

## What to expect

- **ORD-002**: the agent sees that AC-2 (cancelling a shipped order) has no test and proposes one with
  `[Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_2)]`.
- **ORD-003**: there is no implementation; it reads `OrderService.cs` with `get_code`, sees that `Order.Total` is
  computed and proposes an `ApplyCoupon` with `[Implements(Requirements.ORD_003)]` that fits those types, plus one test
  per criterion. With `--allow-writes` it also creates the skeletons in `tests/Orders.Tests/Specs`.

Check the result as with any change: `dotnet build` (ST diagnostics) and `dotnet spectrace report`.

## Notes

- On Windows, `cmd` uses `set NAME=value` and PowerShell `$env:NAME="value"`. Never paste the key into a chat or a
  versioned file.
- The demo is not part of `SpecTrace.sln` and uses floating versions of `Microsoft.Agents.AI` (1.x),
  `Microsoft.Extensions.AI.OpenAI` and `ModelContextProtocol.Core` (2.x). Pin them when you adopt it.
- The SpecTrace MCP server implements the `initialize` handshake (revisions 2024-11-05 to 2025-11-25).
  SDK 2.x clients prefer revision 2026-07-28 and fall back to `initialize` automatically.
