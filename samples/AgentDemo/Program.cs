// Demo de SpecTrace + Microsoft Agent Framework + MCP.
//
// El agente recibe un ID de requisito, consulta la spec y el estado de trazabilidad a través del servidor
// MCP de SpecTrace (`spectrace mcp`) y propone el código y los tests que faltan, con las marcas
// [Implements]/[Verifies] correctas. Por defecto solo usa herramientas de lectura.
//
//   dotnet build src/SpecTrace.Tool
//   export OPENAI_API_KEY=...              (o OPENAI_BASE_URL para Ollama, LM Studio, Azure OpenAI v1...)
//   dotnet run --project samples/AgentDemo -- ORD-002
//   dotnet run --project samples/AgentDemo -- ORD-003 --allow-writes   (permite scaffold_tests y propose_requirement)

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;

var requirementId = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "ORD-002";
var allowWrites = args.Contains("--allow-writes");
var root = Path.GetFullPath(Option("--root") ?? DefaultRoot());

// ---------- Modelo: cualquier endpoint compatible con la API de OpenAI ----------
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
if (apiKey is null && baseUrl is null)
{
    Console.Error.WriteLine("""
        Configura un modelo antes de ejecutar la demo:
          OPENAI_API_KEY   clave de OpenAI (o de tu proveedor)
          OPENAI_BASE_URL  opcional: endpoint compatible con OpenAI, p. ej. http://localhost:11434/v1 (Ollama)
          OPENAI_MODEL     opcional: nombre del modelo (por defecto gpt-4.1-mini)
        """);
    return 1;
}

var openAiOptions = new OpenAIClientOptions();
if (baseUrl is not null) openAiOptions.Endpoint = new Uri(baseUrl);
IChatClient chatClient = new OpenAIClient(new ApiKeyCredential(apiKey ?? "no-key"), openAiOptions)
    .GetChatClient(model)
    .AsIChatClient();

// ---------- Servidor MCP de SpecTrace por stdio ----------
var (command, arguments) = SpecTraceServerCommand(root);
Console.WriteLine($"Servidor MCP: {command} {string.Join(' ', arguments)}");

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
Console.WriteLine($"Herramientas: {string.Join(", ", tools.Select(t => t.Name))}\n");

// ---------- Agente ----------
AIAgent agent = chatClient.AsAIAgent(
    instructions: $"""
        Eres un ingeniero .NET que trabaja con Spec-Driven Development en un repositorio que usa SpecTrace.
        Reglas:
        - Las specs son la fuente de verdad. Consulta siempre el requisito con get_requirement antes de proponer nada.
        - Antes de escribir código, lee con get_code los ficheros relacionados (requirement_id) y los tipos que vayas a usar (type).
          Reutiliza los tipos, constructores y miembros que existen; no supongas propiedades ni setters. Si el diseño actual
          impide implementar algo, dilo y propón el cambio mínimo.
        - Usa exactamente los using que indica get_requirement en "Dónde va el código".
        - No inventes comportamiento. Si hace falta un requisito nuevo, {(allowWrites ? "propónlo con propose_requirement (queda en estado proposed)" : "descríbelo como propuesta para que una persona lo añada a la spec")}.
        - El código que implementa un requisito lleva [Implements(Requirements.<ID con guiones bajos>)].
        - Cada test xUnit verifica un criterio: [Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)].
        - Un test con Skip no cuenta como cobertura.
        - Responde en español, con el código en bloques ```csharp y el fichero de destino encima de cada bloque.
        """,
    tools: [.. tools.Cast<AITool>()]);

var task = $"""
    Trabaja en el requisito {requirementId}.
    1. Consulta su contexto, el estado de trazabilidad y el código relacionado.
    2. Explica en dos o tres frases qué falta (implementación, tests o criterios sin test).
    3. Propón el código que falta, coherente con los tipos existentes: la implementación si no existe y un test xUnit por cada criterio sin test.
    {(allowWrites ? "4. Si faltan tests, genera los esqueletos con scaffold_tests en tests/Orders.Tests y dime qué rellenar." : "")}
    """;

await foreach (var update in agent.RunStreamingAsync(task))
{
    Console.Write(update);
}

Console.WriteLine();
return 0;

// ---------- Utilidades ----------

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

// Usa la CLI compilada del repositorio si existe (dotnet build src/SpecTrace.Tool);
// si no, la herramienta local del repositorio (dotnet tool install SpecTrace.Tool, que se invoca como `dotnet spectrace`).
// No se usa `dotnet run` porque escribe la salida del build en stdout, que es el canal del protocolo.
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
