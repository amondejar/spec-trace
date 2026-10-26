using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SpecTrace.Specs;
using SpecTrace.Tool.Authoring;

namespace SpecTrace.Tool.Mcp;

/// <summary>
/// Herramientas MCP de SpecTrace. Cada llamada vuelve a leer specs y código, así que el agente siempre ve
/// el estado actual del disco. Las rutas que recibe se confinan a la raíz del repositorio (spectrace.json).
/// </summary>
public sealed class SpecTraceTools
{
    private readonly SpecTraceConfig _config;
    private readonly Dictionary<string, (JsonObject Definition, Func<JsonObject, JsonObject> Handler)> _tools;

    public SpecTraceTools(SpecTraceConfig config)
    {
        _config = config;
        _tools = new()
        {
            ["list_requirements"] = (Define("list_requirements", "Listar requisitos",
                "Lista los requisitos de las specs con su estado de trazabilidad: implementado, con tests y criterios cubiertos. " +
                "Usa only_gaps=true para ver solo lo que falta.",
                Schema(
                    ("status", Str("Filtra por estado: draft, proposed, approved, implemented o deprecated.")),
                    ("only_gaps", Bool("Si es true, solo requisitos activos sin implementar, sin tests o con criterios sin test.")),
                    ("only_changed", Bool("Si es true, solo requisitos cuyo texto cambió después de revisarse (spectrace.lock)."))),
                readOnly: true), ListRequirements),

            ["get_requirement"] = (Define("get_requirement", "Contexto de un requisito",
                "Devuelve el enunciado, los criterios de aceptación con su estado, dónde está implementado, en qué proyectos va el " +
                "código y con qué using, los ficheros relacionados y las marcas exactas ([Implements]/[Verifies]) que hay que usar. " +
                "Llámala antes de implementar o probar un requisito.",
                Schema(new[] { "id" }, ("id", Str("ID del requisito, por ejemplo ORD-002."))),
                readOnly: true), GetRequirement),

            ["get_code"] = (Define("get_code", "Leer código del repositorio",
                "Devuelve el contenido de ficheros de código para adaptar una propuesta a los tipos reales en vez de suponerlos. " +
                "Indica exactamente uno de: requirement_id (los ficheros que implementan y prueban el requisito o, si aún no tiene, " +
                "los de otros requisitos de la misma spec), type (los ficheros donde se declara ese tipo) o path (un fichero). " +
                "Solo lee ficheros .cs, .csproj y .md de las carpetas de código y de specs.",
                Schema(
                    ("requirement_id", Str("ID del requisito, por ejemplo ORD-003.")),
                    ("type", Str("Nombre de un tipo C#, por ejemplo Order u OrderService.")),
                    ("path", Str("Ruta de un fichero, relativa a la raíz del repositorio."))),
                readOnly: true), GetCode),

            ["get_traceability_report"] = (Define("get_traceability_report", "Informe de trazabilidad",
                "Informe completo requisito → código → test, en Markdown (por defecto) o JSON.",
                Schema(("format", EnumOf("Formato del informe.", "md", "json"))),
                readOnly: true), GetReport),

            ["scaffold_tests"] = (Define("scaffold_tests", "Generar esqueletos de tests",
                "Crea en <test_project>/Specs un fichero xUnit por requisito con un test (con Skip) por cada criterio sin test. " +
                "No sobrescribe ficheros existentes. Hay que completar cada test y quitar el Skip para que cuente.",
                Schema(new[] { "test_project" },
                    ("test_project", Str("Carpeta del proyecto de tests, relativa a la raíz del repositorio.")),
                    ("requirement_id", Str("Solo este requisito. Si se omite, todos los que tengan criterios sin test."))),
                readOnly: false), ScaffoldTests),

            ["propose_requirement"] = (Define("propose_requirement", "Proponer un requisito",
                "Añade un requisito nuevo con Status: proposed al final de una spec (*.spec.md dentro de la carpeta de specs). " +
                "Úsala cuando la tarea pide comportamiento que ninguna spec describe; una persona debe aprobarlo después.",
                Schema(new[] { "spec_file", "title", "statement", "criteria" },
                    ("spec_file", Str("Spec de destino, relativa a la carpeta de specs (por ejemplo orders.spec.md). Se crea si no existe.")),
                    ("title", Str("Título corto del requisito.")),
                    ("statement", Str("Enunciado, preferiblemente EARS: When/While/If ..., the system shall ...")),
                    ("criteria", new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["minItems"] = 1,
                        ["description"] = "Criterios de aceptación en formato Given/When/Then, uno por elemento.",
                    }),
                    ("id", Str("ID explícito. Si se omite, se usa el siguiente número del prefijo de la spec."))),
                readOnly: false), ProposeRequirement),
        };
    }

    public bool Exists(string name) => _tools.ContainsKey(name);

    public JsonArray Definitions() => new(_tools.Values.Select(t => (JsonNode)t.Definition.DeepClone()).ToArray());

    public JsonObject Call(string name, JsonObject arguments)
    {
        try
        {
            return _tools[name].Handler(arguments);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Errores de ejecución: van en el resultado (isError) para que el modelo pueda corregirse.
            return Text(ex.Message, isError: true);
        }
    }

    // ---------- Herramientas ----------

    private JsonObject ListRequirements(JsonObject args)
    {
        var status = OptionalString(args, "status");
        var onlyGaps = args["only_gaps"]?.GetValue<bool>() ?? false;
        var onlyChanged = args["only_changed"]?.GetValue<bool>() ?? false;
        var model = TraceScanner.Scan(_config);

        var items = model.Requirements
            .Where(t => status == null || string.Equals(t.Requirement.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(t => !onlyGaps || t.HasGap)
            .Where(t => !onlyChanged || t.HasDrift)
            .Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Requirement.Id,
                ["title"] = t.Requirement.Title,
                ["status"] = t.Requirement.Status,
                ["implemented"] = t.IsImplemented,
                ["verified"] = t.IsVerified,
                ["criteria_covered"] = t.CoveredCriteria,
                ["criteria_total"] = t.Requirement.Criteria.Count,
                ["gap"] = t.HasGap,
                ["spec_changed"] = t.HasDrift,
                ["spec"] = $"{t.Requirement.FilePath}:{t.Requirement.Line + 1}",
            })
            .ToArray();

        var structured = new JsonObject { ["requirements"] = new JsonArray(items) };
        var summary = items.Length == 0
            ? "No hay requisitos que cumplan el filtro."
            : string.Join("\n", items.Select(i =>
                $"{i!["id"]} [{i["status"]}] {i["title"]} — " +
                $"{(i["implemented"]!.GetValue<bool>() ? "implementado" : "sin implementar")}, " +
                $"criterios con test {i["criteria_covered"]}/{i["criteria_total"]}" +
                (i["gap"]!.GetValue<bool>() ? " (hueco)" : "") +
                (i["spec_changed"]!.GetValue<bool>() ? " (spec cambiada desde la última revisión)" : "")));

        var result = Text(summary);
        result["structuredContent"] = structured;
        return result;
    }

    private JsonObject GetRequirement(JsonObject args)
    {
        var id = RequiredString(args, "id");
        return Text(Reports.Context(TraceScanner.Scan(_config), id));
    }

    private const int MaxFiles = 8;
    private const int MaxLinesPerFile = 400;
    private const int MaxChars = 100_000;
    private static readonly string[] CodeExtensions = { ".cs", ".csproj", ".md" };

    private JsonObject GetCode(JsonObject args)
    {
        var path = OptionalString(args, "path");
        var type = OptionalString(args, "type");
        var requirementId = OptionalString(args, "requirement_id");
        if (new[] { path, type, requirementId }.Count(a => a != null) != 1)
        {
            throw new ArgumentException("Indica exactamente uno de: requirement_id, type o path.");
        }

        List<string> files;
        string intro;
        if (path != null)
        {
            files = new List<string> { TraceScanner.Relative(_config.Root, ReadableFile(path)) };
            intro = "";
        }
        else if (type != null)
        {
            files = TraceScanner.FindTypeFiles(_config, type).ToList();
            if (files.Count == 0)
            {
                throw new ArgumentException($"No hay ningún tipo llamado '{type}' en {string.Join(", ", _config.Sources)}.");
            }

            intro = $"Ficheros donde se declara `{type}`:\n\n";
        }
        else
        {
            var model = TraceScanner.Scan(_config);
            var trace = model.Requirements.FirstOrDefault(r => string.Equals(r.Requirement.Id, requirementId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No existe el requisito '{requirementId}'.");
            var related = Reports.RelatedFiles(model, trace);
            var own = related.Where(f => f.Ids.Contains(trace.Requirement.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            files = (own.Count > 0 ? own : related).Select(f => f.File).Distinct().ToList();
            if (files.Count == 0)
            {
                throw new ArgumentException($"Ningún fichero trabaja todavía con requisitos de {trace.Requirement.FilePath}. Busca los tipos del dominio con type.");
            }

            intro = own.Count > 0
                ? $"Ficheros que implementan o prueban {trace.Requirement.Id}:\n\n"
                : $"{trace.Requirement.Id} aún no tiene código; estos ficheros trabajan con otros requisitos de la misma spec:\n\n";
        }

        var sb = new System.Text.StringBuilder(intro);
        var shown = 0;
        foreach (var relative in files)
        {
            if (shown == MaxFiles || sb.Length > MaxChars)
            {
                sb.AppendLine($"(… {files.Count - shown} fichero(s) más: {string.Join(", ", files.Skip(shown))}. Pídelos con path.)");
                break;
            }

            var lines = File.ReadAllLines(Path.Combine(_config.Root, relative));
            var language = Path.GetExtension(relative).ToLowerInvariant() switch { ".cs" => "csharp", ".csproj" => "xml", _ => "markdown" };
            sb.AppendLine($"### {relative} ({lines.Length} líneas)");
            sb.AppendLine();
            sb.AppendLine("```" + language);
            foreach (var line in lines.Take(MaxLinesPerFile)) sb.AppendLine(line);
            sb.AppendLine("```");
            if (lines.Length > MaxLinesPerFile) sb.AppendLine($"(recortado: se muestran {MaxLinesPerFile} de {lines.Length} líneas)");
            sb.AppendLine();
            shown++;
        }

        return Text(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Un fichero que get_code puede leer: dentro de las carpetas de código o de specs, con extensión permitida
    /// y fuera de bin/obj/.git. Así no se exponen secretos (.env, appsettings.*.json, claves...).
    /// </summary>
    private string ReadableFile(string relative)
    {
        var full = InsideRoot(relative);
        var allowedRoots = _config.Sources.Append(_config.Specs).Select(s => Path.GetFullPath(Path.Combine(_config.Root, s)));
        if (!allowedRoots.Any(r => IsUnder(full, r)))
        {
            throw new ArgumentException($"'{relative}' no está en las carpetas de código ({string.Join(", ", _config.Sources)}) ni de specs ({_config.Specs}).");
        }

        if (!CodeExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
        {
            throw new ArgumentException($"get_code solo lee ficheros {string.Join(", ", CodeExtensions)}.");
        }

        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p is "bin" or "obj" or ".git"))
        {
            throw new ArgumentException("get_code no lee ficheros de bin, obj ni .git.");
        }

        if (!File.Exists(full)) throw new ArgumentException($"No existe '{relative}'.");
        return full;
    }

    private JsonObject GetReport(JsonObject args)
    {
        var model = TraceScanner.Scan(_config);
        return OptionalString(args, "format") == "json" ? Text(Reports.Json(model)) : Text(Reports.Markdown(model));
    }

    private JsonObject ScaffoldTests(JsonObject args)
    {
        var project = InsideRoot(RequiredString(args, "test_project"));
        if (!Directory.Exists(project)) throw new ArgumentException($"No existe la carpeta '{RequiredString(args, "test_project")}'.");

        var files = Scaffolder.ScaffoldTests(TraceScanner.Scan(_config), project, OptionalString(args, "requirement_id"), ns: null);
        return Text(files.Count == 0
            ? "No había criterios sin test (o los ficheros ya existían). No se ha creado nada."
            : "Creados (completa cada test y quita el Skip):\n" + string.Join("\n", files.Select(f => "- " + TraceScanner.Relative(_config.Root, f))));
    }

    private JsonObject ProposeRequirement(JsonObject args)
    {
        var specsDir = Path.GetFullPath(Path.Combine(_config.Root, _config.Specs));
        var relative = RequiredString(args, "spec_file");
        if (!SpecParser.IsSpecFile(relative)) throw new ArgumentException($"'{relative}' debe terminar en {SpecParser.FileSuffix}.");

        var path = Path.GetFullPath(Path.Combine(specsDir, relative));
        if (!IsUnder(path, specsDir)) throw new ArgumentException($"'{relative}' queda fuera de la carpeta de specs.");

        var title = RequiredString(args, "title");
        var statement = RequiredString(args, "statement");
        var criteria = (args["criteria"] as JsonArray)?.Select(c => c?.GetValue<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList()
            ?? new List<string>();
        if (criteria.Count == 0) throw new ArgumentException("Hace falta al menos un criterio de aceptación.");

        var catalog = TraceScanner.Scan(_config).Catalog;
        var existing = File.Exists(path) ? SpecParser.Parse(path, File.ReadAllText(path)).Requirements.Select(r => r.Id).ToList() : new List<string>();

        var id = OptionalString(args, "id")?.Trim().ToUpperInvariant();
        if (id == null)
        {
            if (existing.Count == 0) throw new ArgumentException("La spec está vacía: indica el ID del requisito (por ejemplo PAY-001).");
            id = SpecWriter.NextId(existing);
            while (catalog.TryGet(id, out _)) id = SpecWriter.NextId(existing.Append(id));
        }

        if (!SpecWriter.IsValidId(id)) throw new ArgumentException($"'{id}' no es un ID válido (PREFIJO-123).");
        if (catalog.TryGet(id, out var clash)) throw new ArgumentException($"El ID {id} ya existe en {clash.FilePath}.");

        var block = SpecWriter.Render(new RequirementDraft(id, title, SpecStatus.Proposed, statement, criteria,
            Note: $"Propuesto por un agente el {DateTime.Now:yyyy-MM-dd}. Revísalo y cambia el estado a approved para exigirlo."));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var separator = current.Length == 0 ? "" : current.EndsWith("\n\n", StringComparison.Ordinal) ? "" : current.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
        File.WriteAllText(path, current + separator + block);

        return Text($"Añadido {id} ({title}) con Status: proposed en {TraceScanner.Relative(_config.Root, path)}. " +
                    "No se exige hasta que alguien lo apruebe (Status: approved).");
    }

    // ---------- Utilidades ----------

    private string InsideRoot(string relative)
    {
        var root = Path.GetFullPath(_config.Root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnder(full, root)) throw new ArgumentException($"'{relative}' queda fuera del repositorio.");
        return full;
    }

    private static bool IsUnder(string path, string dir)
    {
        var d = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return path.StartsWith(d, StringComparison.Ordinal) || path == dir;
    }

    private static string RequiredString(JsonObject args, string name) =>
        OptionalString(args, name) ?? throw new ArgumentException($"Falta el argumento '{name}'.");

    private static string? OptionalString(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Define(string name, string title, string description, JsonObject inputSchema, bool readOnly) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
        ["annotations"] = new JsonObject
        {
            ["title"] = title,
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = readOnly,
            ["openWorldHint"] = false,
        },
    };

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] properties) => Schema(Array.Empty<string>(), properties);

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        var schemaObj = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) schemaObj["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schemaObj;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject EnumOf(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
    };
}
