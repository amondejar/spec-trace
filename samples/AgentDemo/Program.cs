// SpecTrace + Microsoft Agent Framework + MCP demo.
//
// The agent receives a requirement ID, looks up the spec and the traceability status through the SpecTrace
// MCP server (`spectrace mcp`) and proposes the missing code and tests, with the correct
// [Implements]/[Verifies] markers. By default it only uses read-only tools.
//
//   dotnet build src/SpecTrace.Tool
//   export OPENAI_API_KEY=...              (or OPENAI_BASE_URL for Ollama, LM Studio, Azure OpenAI v1...)
//   dotnet run --project samples/AgentDemo -- ORD-002
//   dotnet run --project samples/AgentDemo -- ORD-003 --allow-writes   (allows scaffold_tests and propose_requirement)

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;

var requirementId = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "ORD-002";
var allowWrites = args.Contains("--allow-writes");
var root = Path.GetFullPath(Option("--root") ?? DefaultRoot());

// ---------- Model: any OpenAI-compatible endpoint ----------
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
if (apiKey is null && baseUrl is null)
{
    Console.Error.WriteLine("""
        Configure a model before running the demo:
          OPENAI_API_KEY   OpenAI key (or your provider's)
          OPENAI_BASE_URL  optional: OpenAI-compatible endpoint, e.g. http://localhost:11434/v1 (Ollama)
          OPENAI_MODEL     optional: model name (default gpt-4.1-mini)
        """);
    return 1;
}

var openAiOptions = new OpenAIClientOptions();
if (baseUrl is not null) openAiOptions.Endpoint = new Uri(baseUrl);
IChatClient chatClient = new OpenAIClient(new ApiKeyCredential(apiKey ?? "no-key"), openAiOptions)
    .GetChatClient(model)
    .AsIChatClient();

// ---------- SpecTrace MCP server over stdio ----------
var (command, arguments) = SpecTraceServerCommand(root);
Console.WriteLine($"MCP server: {command} {string.Join(' ', arguments)}");

await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "spectrace",
    Command = command,
    Arguments = arguments,
}));

var readOnlyTools = new[] { "list_requirements", "get_requirement", "get_code", "get_traceability_report" };
var tools = (await mcp.ListToolsAsync())
    .Where(t => allowWrites || readOnlyTools.Contains(t.Name))
    .ToList();
Console.WriteLine($"Tools: {string.Join(", ", tools.Select(t => t.Name))}\n");

// ---------- Agent ----------
AIAgent agent = chatClient.AsAIAgent(
    instructions: $"""
        You are a .NET engineer doing Spec-Driven Development in a repository that uses SpecTrace.
        Rules:
        - The specs are the source of truth. Always look up the requirement with get_requirement before proposing anything.
        - Before writing code, read the related files with get_code (requirement_id) and the types you will use (type).
          Reuse the existing types, constructors and members; do not assume properties or setters. If the current design
          prevents implementing something, say so and propose the smallest change.
        - Use exactly the usings that get_requirement lists under "Where the code goes".
        - Do not invent behavior. If a new requirement is needed, {(allowWrites ? "propose it with propose_requirement (it stays in status proposed)" : "describe it as a proposal for a person to add to the spec")}.
        - Code that implements a requirement carries [Implements(Requirements.<ID with underscores>)].
        - Each xUnit test verifies one criterion: [Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)].
        - A skipped test does not count as coverage.
        - Answer with the code in ```csharp blocks and the target file above each block.
        """,
    tools: [.. tools.Cast<AITool>()]);

var task = $"""
    Work on requirement {requirementId}.
    1. Look up its context, its traceability status and the related code.
    2. Explain in two or three sentences what is missing (implementation, tests or criteria without tests).
    3. Propose the missing code, consistent with the existing types: the implementation if it does not exist and one xUnit test per criterion without a test.
    {(allowWrites ? "4. If tests are missing, generate the skeletons with scaffold_tests in tests/Orders.Tests and tell me what to fill in." : "")}
    """;

await foreach (var update in agent.RunStreamingAsync(task))
{
    Console.Write(update);
}

Console.WriteLine();
return 0;

// ---------- Helpers ----------

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string DefaultRoot()
{
    var cwd = Directory.GetCurrentDirectory();
    var sample = Path.Combine(cwd, "samples", "Orders");
    return Directory.Exists(sample) ? sample : cwd;
}

// Uses the repository's built CLI if it exists (dotnet build src/SpecTrace.Tool);
// otherwise the repository's local tool (dotnet tool install SpecTrace.Tool, invoked as `dotnet spectrace`).
// `dotnet run` is not used because it writes build output to stdout, which is the protocol channel.
static (string Command, List<string> Arguments) SpecTraceServerCommand(string root)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var bin = Path.Combine(dir.FullName, "src", "SpecTrace.Tool", "bin");
        if (Directory.Exists(bin))
        {
            var dll = Directory.EnumerateFiles(bin, "SpecTrace.Tool.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (dll is not null) return ("dotnet", [dll, "mcp", "--root", root]);
        }

        dir = dir.Parent;
    }

    return ("dotnet", ["spectrace", "mcp", "--root", root]);
}
