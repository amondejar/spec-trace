using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SpecTrace.Tool;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        SpecTrace: trazabilidad Spec-Driven Development para .NET

        Uso: dotnet spectrace <comando> [opciones]

        Comandos:
          report          Matriz requisito → código → test.
                            --format md|json|github
                                               md por defecto; github = anotaciones para GitHub Actions
                            --link-base <url>  prefijo para enlaces absolutos en md (comentarios de PR)
                            --out <fichero>    escribir a fichero en vez de a la consola
                            --fail-on-gaps     código de salida 1 si hay requisitos activos sin implementar o sin test
                            --fail-on-drift    código de salida 1 si hay cambios en la spec sin revisar (spectrace.lock)
          lock            Registra la versión actual de la spec como revisada (spectrace.lock).
                            --requirement <ID> solo ese requisito (repetible); sin él, todos los activos
                            --check            no escribe: código de salida 1 si hay cambios sin revisar
          context <ID>    Contexto de un requisito para un agente: enunciado, criterios, estado y cómo marcarlo.
          scaffold-tests  Esqueletos xUnit para los criterios sin test.
                            --project <dir>    carpeta del proyecto de tests (obligatorio)
                            --requirement <ID> solo ese requisito
                            --namespace <ns>   namespace raíz del proyecto de tests
          mcp             Servidor MCP por stdio para agentes (Claude Code, VS Code, Agent Framework...).
          import speckit <ruta>
                          Importa specs de GitHub Spec Kit (spec.md o carpeta specs/).
                            --prefix <P>       prefijo de los IDs (por defecto, iniciales de la feature: PA1)
                            --out <fichero>    destino (solo con una feature); por defecto spectrace.spec.md junto a la fuente
          import openspec <ruta>
                          Importa specs de OpenSpec (openspec/, openspec/specs o un spec.md).
                            --prefix <P>       prefijo de los IDs (por defecto, el nombre de la capacidad: AUTH)
                            --out <fichero>    destino (solo con una capacidad); por defecto <specs>/<capacidad>.spec.md
                          Comunes: --status approved|proposed (estado de lo importado con criterios), --dry-run
          init            Crea spectrace.json, specs/example.spec.md, la skill para agentes y .mcp.json.
                            --skills-dir <dir> carpeta de skills (por defecto .claude/skills)

        Opciones comunes:
          --root <dir>      carpeta desde la que buscar spectrace.json (por defecto la actual)
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var opts = Options.Parse(args.Skip(1).ToArray());
            var config = SpecTraceConfig.Load(opts.Get("root") ?? Directory.GetCurrentDirectory());

            switch (args[0])
            {
                case "report":
                {
                    var model = TraceScanner.Scan(config);
                    var repoRoot = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } ws
                        ? ws
                        : Directory.GetCurrentDirectory();
                    var text = opts.Get("format") switch
                    {
                        "json" => Reports.Json(model),
                        "github" => Reports.GitHub(model, repoRoot),
                        "md" or null => Reports.Markdown(model, opts.Get("link-base")),
                        var other => throw new ArgumentException($"Formato desconocido '{other}'. Usa md, json o github."),
                    };
                    Write(text, opts.Get("out"));
                    var gaps = model.Requirements.Count(r => r.HasGap) + model.UnknownLinks.Count;
                    var drift = model.Requirements.Count(r => r.HasDrift);
                    return (opts.Has("fail-on-gaps") && gaps > 0) || (opts.Has("fail-on-drift") && drift > 0) ? 1 : 0;
                }

                case "context":
                {
                    var id = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Falta el ID del requisito: dotnet spectrace context ORD-001");
                    Console.WriteLine(Reports.Context(TraceScanner.Scan(config), id));
                    return 0;
                }

                case "scaffold-tests":
                {
                    var project = opts.Get("project") ?? throw new ArgumentException("Falta --project <carpeta del proyecto de tests>");
                    var files = Scaffolder.ScaffoldTests(TraceScanner.Scan(config), Path.GetFullPath(project),
                        opts.Get("requirement"), opts.Get("namespace"));
                    Console.WriteLine(files.Count == 0
                        ? "No hay criterios sin test (o los ficheros ya existen)."
                        : "Creados:\n" + string.Join("\n", files.Select(f => "  " + TraceScanner.Relative(config.Root, f))));
                    return 0;
                }

                case "mcp":
                {
                    // stdout es el canal del protocolo: todo lo demás va a stderr.
                    var server = new SpecTrace.Tool.Mcp.McpServer(config, Console.Error);
                    Console.Error.WriteLine($"[spectrace mcp] raíz: {config.Root}");
                    server.RunOnStdioAsync().GetAwaiter().GetResult();
                    return 0;
                }

                case "import":
                    return Import(opts, config);

                case "lock":
                    return Lock(opts, config);

                case "init":
                    return Init(opts);

                default:
                    Console.Error.WriteLine($"Comando desconocido: {args[0]}\n");
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Init(Options opts)
    {
        var root = Path.GetFullPath(opts.Get("root") ?? Directory.GetCurrentDirectory());
        var skillsDir = Path.Combine(root, opts.Get("skills-dir") ?? Path.Combine(".claude", "skills"));
        var created = new List<string>();

        void Create(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            created.Add(TraceScanner.Relative(root, path));
        }

        Create(SpecTraceConfig.FileName, "{\n  \"specs\": \"specs\",\n  \"sources\": [\"src\", \"tests\"]\n}\n");
        Create(Path.Combine("specs", "example.spec.md"), Template("example.spec.md"));
        Create(Path.GetRelativePath(root, Path.Combine(skillsDir, "spectrace", "SKILL.md")), Template("SKILL.md"));
        // Registro del servidor MCP para agentes que leen .mcp.json en la raíz del proyecto (por ejemplo Claude Code).
        Create(".mcp.json", "{\n  \"mcpServers\": {\n    \"spectrace\": {\n      \"command\": \"dotnet\",\n      \"args\": [\"spectrace\", \"mcp\"]\n    }\n  }\n}\n");

        Console.WriteLine(created.Count == 0 ? "Todo estaba ya creado." : "Creados:\n" + string.Join("\n", created.Select(c => "  " + c)));
        Console.WriteLine("\nSiguiente paso: añade el paquete a tus proyectos con  dotnet add package SpecTrace");
        return 0;
    }

    /// <summary>
    /// Registra en spectrace.lock la huella actual de los requisitos revisados. Es un acto humano: confirma que el código
    /// y los tests están al día con el texto de la spec, igual que aprobar una PR.
    /// </summary>
    private static int Lock(Options opts, SpecTraceConfig config)
    {
        var model = TraceScanner.Scan(config);
        var path = TraceScanner.LockPath(config);

        if (opts.Has("check"))
        {
            var drifted = model.Requirements.Where(t => t.HasDrift).ToList();
            if (!model.HasLock)
            {
                Console.WriteLine("No hay spectrace.lock: ejecuta `dotnet spectrace lock` para empezar a detectar cambios.");
                return 0;
            }

            foreach (var t in drifted)
            {
                var what = new List<string>();
                if (t.CodeNeedsReview) what.Add("enunciado");
                what.AddRange(t.CriteriaNeedingReview.Select(c => c.Id));
                Console.WriteLine($"{t.Requirement.Id}: cambió {string.Join(", ", what)}");
            }

            Console.WriteLine(drifted.Count == 0 ? "Sin cambios pendientes de revisar." : $"{drifted.Count} requisito(s) con cambios sin revisar.");
            return drifted.Count == 0 ? 0 : 1;
        }

        var ids = opts.GetAll("requirement");
        var current = model.Lock ?? new SpecTrace.Specs.SpecLock(Array.Empty<SpecTrace.Specs.SpecLockEntry>());
        SpecTrace.Specs.SpecLock updated;
        int changed = 0, added = 0, removed = 0;

        if (ids.Count > 0)
        {
            updated = current;
            foreach (var id in ids)
            {
                var t = model.Requirements.FirstOrDefault(r => string.Equals(r.Requirement.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No existe el requisito '{id}'.");
                if (t.Requirement.IsDeprecated) throw new ArgumentException($"{t.Requirement.Id} está deprecated: no se registra.");
                if (current.Find(t.Requirement.Id) == null) added++;
                else if (t.DriftInfo.Any) changed++;
                updated = updated.With(SpecTrace.Specs.SpecFingerprint.Entry(t.Requirement));
            }
        }
        else
        {
            // Todos los requisitos activos; los retirados o en borrador salen del fichero.
            var active = model.Requirements.Where(t => t.Requirement.IsEnforced).ToList();
            added = active.Count(t => current.Find(t.Requirement.Id) == null);
            changed = active.Count(t => t.DriftInfo.IsLocked && t.DriftInfo.Any);
            removed = current.Entries.Count(e => !active.Any(t => string.Equals(t.Requirement.Id, e.Id, StringComparison.OrdinalIgnoreCase)));
            updated = new SpecTrace.Specs.SpecLock(active.Select(t => SpecTrace.Specs.SpecFingerprint.Entry(t.Requirement)));
        }

        File.WriteAllText(path, updated.Render());
        Console.WriteLine($"{SpecTrace.Specs.SpecLock.FileName}: {updated.Count} requisitos ({added} nuevos, {changed} actualizados, {removed} retirados).");
        if (ids.Count == 0 && changed > 0)
        {
            Console.WriteLine("Aviso: se han dado por revisados cambios pendientes. Si no era la intención, deshazlo con git.");
        }

        return 0;
    }

    private static int Import(Options opts, SpecTraceConfig config)
    {
        var kind = opts.Positional.ElementAtOrDefault(0)
            ?? throw new ArgumentException("Uso: dotnet spectrace import speckit|openspec <ruta>");
        var source = opts.Positional.ElementAtOrDefault(1)
            ?? throw new ArgumentException($"Falta la ruta: dotnet spectrace import {kind} <ruta>");
        var status = opts.Get("status") ?? SpecTrace.Specs.SpecStatus.Approved;
        if (status is not ("approved" or "proposed")) throw new ArgumentException("--status debe ser approved o proposed.");
        var dryRun = opts.Has("dry-run");
        var outOption = opts.Get("out");

        var specs = new List<SpecTrace.Tool.Import.ImportedSpec>();
        switch (kind)
        {
            case "speckit":
            {
                var sources = SpecTrace.Tool.Import.SpecKitImporter.FindSources(source).ToList();
                if (sources.Count == 0) throw new ArgumentException($"No hay ningún spec.md de Spec Kit en '{source}'.");
                if (outOption != null && sources.Count > 1) throw new ArgumentException("--out solo se puede usar con una feature.");
                foreach (var file in sources)
                {
                    var output = Path.GetFullPath(outOption ?? Path.Combine(Path.GetDirectoryName(file)!, "spectrace.spec.md"));
                    specs.Add(SpecTrace.Tool.Import.SpecKitImporter.Parse(file, File.ReadAllText(file), output,
                        opts.Get("prefix"), status, TraceScanner.Relative(config.Root, file)));
                }

                break;
            }

            case "openspec":
            {
                var sources = SpecTrace.Tool.Import.OpenSpecImporter.FindSources(source).ToList();
                if (sources.Count == 0) throw new ArgumentException($"No hay ningún spec.md de OpenSpec en '{source}'.");
                if (outOption != null && sources.Count > 1) throw new ArgumentException("--out solo se puede usar con una capacidad.");
                if (opts.Get("prefix") != null && sources.Count > 1) throw new ArgumentException("--prefix solo se puede usar con una capacidad.");
                foreach (var (capability, file) in sources)
                {
                    var output = Path.GetFullPath(outOption ?? Path.Combine(config.Root, config.Specs, capability + SpecTrace.Specs.SpecParser.FileSuffix));
                    specs.Add(SpecTrace.Tool.Import.OpenSpecImporter.Parse(capability, file, File.ReadAllText(file), output,
                        opts.Get("prefix"), status, TraceScanner.Relative(config.Root, file)));
                }

                break;
            }

            default:
                throw new ArgumentException($"Formato de importación desconocido '{kind}'. Usa speckit u openspec.");
        }

        foreach (var spec in specs)
        {
            var result = SpecTrace.Tool.Import.ImportWriter.Write(spec, dryRun);
            Console.WriteLine($"{(dryRun ? "[simulación] " : "")}{TraceScanner.Relative(config.Root, result.OutputPath)}: " +
                              $"{result.Requirements} requisitos ({result.Added} nuevos, {result.Kept} conservados) con prefijo {spec.SpecId}");
        }

        return 0;
    }

    private static string Template(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Write(string text, string? path)
    {
        if (path == null)
        {
            Console.WriteLine(text);
            return;
        }

        File.WriteAllText(path, text);
        Console.WriteLine($"Informe escrito en {path}");
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _all = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetAll(string name) => _all.TryGetValue(name, out var v) ? v : new List<string>();

        public List<string> Positional { get; } = new();

        public string? Get(string name) => _named.TryGetValue(name, out var v) ? v : null;

        public bool Has(string name) => _named.ContainsKey(name);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var name = args[i][2..];
                    var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    var value = hasValue ? args[++i] : null;
                    o._named[name] = value;
                    if (value != null)
                    {
                        if (!o._all.TryGetValue(name, out var list)) o._all[name] = list = new List<string>();
                        list.Add(value);
                    }
                }
                else
                {
                    o.Positional.Add(args[i]);
                }
            }

            return o;
        }
    }
}
