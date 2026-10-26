using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpecTrace.Tool.Mcp;

/// <summary>
/// Servidor Model Context Protocol mínimo sobre stdio (JSON-RPC 2.0, un mensaje por línea).
/// Implementa el ciclo de vida con <c>initialize</c> y las capacidades de herramientas, sin dependencias externas.
/// Los clientes que hablan la revisión 2026-07-28 preguntan primero por <c>server/discover</c>; al recibir
/// "method not found" vuelven al handshake <c>initialize</c>, que es el que se implementa aquí.
/// </summary>
public sealed class McpServer
{
    /// <summary>Revisiones soportadas, de la más reciente a la más antigua.</summary>
    public static readonly string[] SupportedVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public const string ServerName = "spectrace";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    // Sin escapar tildes ni símbolos (ó...): es JSON válido igual y se lee bien en logs y consola.
    // El canal es stdio hacia un cliente MCP, no HTML, así que el modo relajado es seguro.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Atiende el protocolo sobre la entrada y salida estándar del proceso, en UTF-8 sin BOM sea cual sea la página de
    /// códigos de la consola (en Windows suele ser 850 o 1252 y estropearía las tildes que envía el cliente).
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

    public McpServer(SpecTraceConfig config, TextWriter? log = null)
    {
        _tools = new SpecTraceTools(config);
        _log = log ?? TextWriter.Null;
    }

    public string? NegotiatedVersion { get; private set; }

    /// <summary>Atiende mensajes hasta que se cierra la entrada.</summary>
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

    /// <summary>Procesa un mensaje JSON-RPC y devuelve la respuesta, o null si era una notificación.</summary>
    public JsonObject? Handle(string message)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException ex)
        {
            return Error(null, ParseError, "JSON no válido: " + ex.Message);
        }

        if (node is not JsonObject request || request["jsonrpc"]?.GetValue<string>() != "2.0")
        {
            return Error(null, InvalidRequest, "Se esperaba un objeto JSON-RPC 2.0 (los lotes no están soportados)");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var isNotification = !request.ContainsKey("id");

        if (method == null)
        {
            // Respuesta a una petición del servidor: este servidor no hace peticiones, se ignora.
            return isNotification ? null : Error(id, InvalidRequest, "Falta 'method'");
        }

        _log.WriteLine($"[spectrace mcp] <- {method}");

        if (isNotification)
        {
            // notifications/initialized, notifications/cancelled, ...: no requieren respuesta.
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
                _ => Error(id, MethodNotFound, $"Método no soportado: {method}"),
            };
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[spectrace mcp] error en {method}: {ex}");
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
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = "SpecTrace",
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
            ["instructions"] =
                "SpecTrace da trazabilidad Spec-Driven Development en este repositorio .NET. " +
                "Antes de implementar, usa get_requirement con el ID del requisito; si no existe, propónlo con propose_requirement. " +
                "Antes de escribir código, lee con get_code los ficheros relacionados y reutiliza sus tipos; no los supongas. " +
                "Marca el código con [Implements(Requirements.ID)] y cada test con [Verifies(Requirements.ID, AcceptanceCriteria.ID.AC_n)]. " +
                "El trabajo está terminado cuando list_requirements con only_gaps=true no devuelve ese requisito. " +
                "Si un requisito tiene cambios en la spec sin revisar (only_changed=true), revisa su código y sus tests y avisa a una persona; " +
                "no registres la revisión (dotnet spectrace lock) por tu cuenta.",
        };
    }

    private JsonObject CallTool(JsonNode? id, JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return Error(id, InvalidParams, "Falta 'name'");
        if (!_tools.Exists(name!)) return Error(id, InvalidParams, $"Herramienta desconocida: {name}");

        var arguments = @params?["arguments"] as JsonObject ?? new JsonObject();
        return Result(id, _tools.Call(name!, arguments));
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
