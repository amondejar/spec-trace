using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpecTrace.Tool.Mcp;

/// <summary>
/// Minimal Model Context Protocol server over stdio (JSON-RPC 2.0, one message per line).
/// Implements the <c>initialize</c> lifecycle and the tools and prompts capabilities, with no external dependencies.
/// Clients speaking revision 2026-07-28 first ask for <c>server/discover</c>; on
/// "method not found" they fall back to the <c>initialize</c> handshake, which is the one implemented here.
/// </summary>
public sealed class McpServer
{
    /// <summary>Supported revisions, newest first.</summary>
    public static readonly string[] SupportedVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public const string ServerName = "spectrace";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    // Do not escape accents or symbols (ó...): it is still valid JSON and reads well in logs and the console.
    // The channel is stdio to an MCP client, not HTML, so the relaxed mode is safe.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serves the protocol over the process's standard input and output, in UTF-8 without BOM regardless of the console
    /// code page (on Windows it is usually 850 or 1252, which would garble non-ASCII text sent by the client).
    /// </summary>
    public Task RunOnStdioAsync(CancellationToken ct = default)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var input = new StreamReader(Console.OpenStandardInput(), utf8);
        var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
        return RunAsync(input, output, ct);
    }

    private readonly SpecTraceTools _tools;
    private readonly TextWriter _log;

    public McpServer(SpecTraceConfig config, TextWriter? log = null, Func<Stories.IStoryTracker>? tracker = null)
    {
        _tools = new SpecTraceTools(config, tracker);
        _log = log ?? TextWriter.Null;
    }

    public string? NegotiatedVersion { get; private set; }

    /// <summary>Serves messages until the input is closed.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var response = Handle(line);
            if (response == null) continue;

            await output.WriteLineAsync(response.ToJsonString(Compact)).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Processes a JSON-RPC message and returns the response, or null if it was a notification.</summary>
    public JsonObject? Handle(string message)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException ex)
        {
            return Error(null, ParseError, "Invalid JSON: " + ex.Message);
        }

        if (node is not JsonObject request || request["jsonrpc"]?.GetValue<string>() != "2.0")
        {
            return Error(null, InvalidRequest, "Expected a JSON-RPC 2.0 object (batches are not supported)");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var isNotification = !request.ContainsKey("id");

        if (method == null)
        {
            // Response to a server request: this server sends no requests, so it is ignored.
            return isNotification ? null : Error(id, InvalidRequest, "Missing 'method'");
        }

        _log.WriteLine($"[spectrace mcp] <- {method}");

        if (isNotification)
        {
            // notifications/initialized, notifications/cancelled, ...: no response needed.
            return null;
        }

        try
        {
            var @params = request["params"] as JsonObject;
            return method switch
            {
                "initialize" => Result(id, Initialize(@params)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = _tools.Definitions() }),
                "tools/call" => CallTool(id, @params),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = SpecTracePrompts.List() }),
                "prompts/get" => GetPrompt(id, @params),
                _ => Error(id, MethodNotFound, $"Method not supported: {method}"),
            };
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[spectrace mcp] error in {method}: {ex}");
            return Error(id, InternalError, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? @params)
    {
        var requested = @params?["protocolVersion"]?.GetValue<string>();
        NegotiatedVersion = requested != null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];

        return new JsonObject
        {
            ["protocolVersion"] = NegotiatedVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = "SpecTrace",
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
            ["instructions"] =
                "SpecTrace provides Spec-Driven Development traceability in this .NET repository. " +
                "Before implementing, call get_requirement with the requirement ID; if it does not exist, propose it with propose_requirement. " +
                "Before writing code, read the related files with get_code and reuse their types; do not guess them. " +
                "Follow the project's architecture (get_architecture): the build reports ST0015 when code breaks one of its rules. " +
                "Mark the code with [Implements(Requirements.ID)] and each test with [Verifies(Requirements.ID, AcceptanceCriteria.ID.AC_n)]. " +
                "The work is done when list_requirements with only_gaps=true no longer returns that requirement. " +
                "If a requirement has unreviewed spec changes (only_changed=true), review its code and tests and tell a person; " +
                "do not record the review (dotnet spectrace lock) yourself. " +
                "New work can start from a user story in the issue tracker: read it with get_story and turn it into a proposed requirement " +
                "with propose_requirement (source = the story, open_questions = what it leaves undecided); list_stories shows the stories without a spec.",
        };
    }

    private JsonObject CallTool(JsonNode? id, JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return Error(id, InvalidParams, "Missing 'name'");
        if (!_tools.Exists(name!)) return Error(id, InvalidParams, $"Unknown tool: {name}");

        var arguments = @params?["arguments"] as JsonObject ?? new JsonObject();
        return Result(id, _tools.Call(name!, arguments));
    }

    private static JsonObject GetPrompt(JsonNode? id, JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return Error(id, InvalidParams, "Missing 'name'");
        if (!SpecTracePrompts.Exists(name!)) return Error(id, InvalidParams, $"Unknown prompt: {name}");

        try
        {
            return Result(id, SpecTracePrompts.Get(name!, @params?["arguments"] as JsonObject));
        }
        catch (ArgumentException ex)
        {
            return Error(id, InvalidParams, ex.Message);
        }
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
}
