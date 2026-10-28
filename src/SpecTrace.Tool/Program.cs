using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SpecTrace.Tool;
using SpecTrace.Tool.Stories;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        SpecTrace: Spec-Driven Development traceability for .NET

        Usage: dotnet spectrace <command> [options]

        Commands:
          report          Requirement → code → test matrix.
                            --format md|json|github
                                               md by default; github = GitHub Actions annotations
                            --link-base <url>  prefix for absolute links in md (PR comments)
                            --out <file>       write to a file instead of the console
                            --fail-on-gaps     exit code 1 if active requirements are not implemented or not tested
                            --fail-on-drift    exit code 1 if there are unreviewed spec changes (spectrace.lock)
          lock            Records the current version of the spec as reviewed (spectrace.lock).
                            --requirement <ID> only that requirement (repeatable); without it, all active ones
                            --check            does not write: exit code 1 if there are unreviewed changes
          context <ID>    Requirement context for an agent: statement, criteria, status and how to mark the work.
          scaffold-tests  xUnit skeletons for criteria without tests.
                            --project <dir>    folder of the test project (required)
                            --requirement <ID> only that requirement
                            --namespace <ns>   root namespace of the test project
          story <ref>     A user story from the issue tracker (GitHub Issues) and the requirements that cite it.
                            <ref>              #12, owner/repo#12 or the issue URL
                            --format md|json
          stories         User stories of the tracker and the requirements that come from each one.
                            --label <name>     default: trackers.github.label in spectrace.json ("user-story"); "" = all
                            --state open|closed|all
                            --unspecified      only stories that no requirement cites yet
                            --fail-on-unspecified
                                               exit code 1 if a listed story has no requirement
                            --format md|json
          mcp             MCP server over stdio for agents (Claude Code, VS Code, Agent Framework...).
          import speckit <path>
                          Imports GitHub Spec Kit specs (a spec.md or a specs/ folder).
                            --prefix <P>       ID prefix (default: initials of the feature, e.g. PA1)
                            --out <file>       output (single feature only); default spectrace.spec.md next to the source
          import openspec <path>
                          Imports OpenSpec specs (openspec/, openspec/specs or a spec.md).
                            --prefix <P>       ID prefix (default: the capability name, e.g. AUTH)
                            --out <file>       output (single capability only); default <specs>/<capability>.spec.md
                          Common: --status approved|proposed (status of imported items with criteria), --dry-run
          init            Creates spectrace.json, specs/example.spec.md, the agent skill and .mcp.json.
                            --skills-dir <dir> skills folder (default .claude/skills)

        Common options:
          --root <dir>      folder to search spectrace.json from (default: the current one)

        GitHub access: public repositories work without a token. For private ones set GITHUB_TOKEN or GH_TOKEN,
        or log in with the GitHub CLI (gh auth login).
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
                        var other => throw new ArgumentException($"Unknown format '{other}'. Use md, json or github."),
                    };
                    Write(text, opts.Get("out"));
                    var gaps = model.Requirements.Count(r => r.HasGap) + model.UnknownLinks.Count;
                    var drift = model.Requirements.Count(r => r.HasDrift);
                    return (opts.Has("fail-on-gaps") && gaps > 0) || (opts.Has("fail-on-drift") && drift > 0) ? 1 : 0;
                }

                case "context":
                {
                    var id = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Missing requirement ID: dotnet spectrace context ORD-001");
                    Console.WriteLine(Reports.Context(TraceScanner.Scan(config), id));
                    return 0;
                }

                case "scaffold-tests":
                {
                    var project = opts.Get("project") ?? throw new ArgumentException("Missing --project <test project folder>");
                    var files = Scaffolder.ScaffoldTests(TraceScanner.Scan(config), Path.GetFullPath(project),
                        opts.Get("requirement"), opts.Get("namespace"));
                    Console.WriteLine(files.Count == 0
                        ? "No criteria without tests (or the files already exist)."
                        : "Created:\n" + string.Join("\n", files.Select(f => "  " + TraceScanner.Relative(config.Root, f))));
                    return 0;
                }

                case "mcp":
                {
                    // stdout is the protocol channel: everything else goes to stderr.
                    var server = new SpecTrace.Tool.Mcp.McpServer(config, Console.Error);
                    Console.Error.WriteLine($"[spectrace mcp] root: {config.Root}");
                    server.RunOnStdioAsync().GetAwaiter().GetResult();
                    return 0;
                }

                case "story":
                    return ShowStory(opts, config);

                case "stories":
                    return ListStories(opts, config);

                case "import":
                    return Import(opts, config);

                case "lock":
                    return Lock(opts, config);

                case "init":
                    return Init(opts);

                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}\n");
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or StoryException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int ShowStory(Options opts, SpecTraceConfig config)
    {
        var text = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Missing story: dotnet spectrace story #12");
        var tracker = GitHubIssues.FromConfig(config);
        var reference = tracker.Parse(text);
        var story = tracker.Get(reference);
        var specified = StoryReports.RequirementsFrom(TraceScanner.Scan(config), tracker, reference);

        Console.WriteLine(opts.Get("format") == "json"
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                key = story.Ref.Key,
                url = story.Ref.Url,
                title = story.Title,
                state = story.State,
                labels = story.Labels,
                author = story.Author,
                userStory = story.Narrative,
                acceptanceCriteria = story.AcceptanceCriteria,
                sections = story.Sections.Select(x => new { heading = x.Heading, text = x.Text }),
                comments = story.Comments.Select(c => new { author = c.Author, createdAt = c.CreatedAt, body = c.Body }),
                requirements = specified.Select(r => new { id = r.Id, title = r.Title, status = r.Status, spec = $"{r.FilePath}:{r.Line + 1}" }),
            }, JsonOptions)
            : StoryReports.Markdown(story, specified));
        return 0;
    }

    private static int ListStories(Options opts, SpecTraceConfig config)
    {
        var tracker = GitHubIssues.FromConfig(config);
        // "--label" without a value (or with "") lists all issues.
        var label = opts.Has("label") ? opts.Get("label") ?? "" : config.Trackers.GitHub.Label;
        var model = TraceScanner.Scan(config);
        var rows = tracker.List(new StoryQuery(label, opts.Get("state") ?? "open", Limit: 500))
            .Select(s => (Story: s, Specified: StoryReports.RequirementsFrom(model, tracker, s.Ref)))
            .Where(r => !opts.Has("unspecified") || r.Specified.Count == 0)
            .ToList();

        Console.WriteLine(opts.Get("format") == "json"
            ? System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new
            {
                key = r.Story.Ref.Key,
                url = r.Story.Ref.Url,
                title = r.Story.Title,
                state = r.Story.State,
                labels = r.Story.Labels,
                requirements = r.Specified.Select(x => x.Id),
            }), JsonOptions)
            : StoryReports.List(rows, tracker.DefaultRepository ?? "github", label));
        return opts.Has("fail-on-unspecified") && rows.Any(r => r.Specified.Count == 0) ? 1 : 0;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

        // With a GitHub remote, user stories are read from its issues (dotnet spectrace story / stories, get_story).
        var github = GitRemote.OriginRepository(root, "github.com");
        Create(SpecTraceConfig.FileName, github == null
            ? "{\n  \"specs\": \"specs\",\n  \"sources\": [\"src\", \"tests\"]\n}\n"
            : "{\n  \"specs\": \"specs\",\n  \"sources\": [\"src\", \"tests\"],\n  \"trackers\": {\n    \"github\": { \"repository\": \"" + github + "\", \"label\": \"user-story\" }\n  }\n}\n");
        Create(Path.Combine("specs", "example.spec.md"), Template("example.spec.md"));
        Create(Path.GetRelativePath(root, Path.Combine(skillsDir, "spectrace", "SKILL.md")), Template("SKILL.md"));
        // MCP server registration for agents that read .mcp.json at the project root (for example Claude Code).
        Create(".mcp.json", "{\n  \"mcpServers\": {\n    \"spectrace\": {\n      \"command\": \"dotnet\",\n      \"args\": [\"spectrace\", \"mcp\"]\n    }\n  }\n}\n");

        Console.WriteLine(created.Count == 0 ? "Everything already exists." : "Created:\n" + string.Join("\n", created.Select(c => "  " + c)));
        Console.WriteLine("\nNext step: add the package to your projects with  dotnet add package SpecTrace --prerelease");
        return 0;
    }

    /// <summary>
    /// Records in spectrace.lock the current fingerprint of the reviewed requirements. It is a human act: it confirms that
    /// the code and the tests are up to date with the spec text, just like approving a pull request.
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
                Console.WriteLine("No spectrace.lock: run `dotnet spectrace lock` to start detecting changes.");
                return 0;
            }

            foreach (var t in drifted)
            {
                var what = new List<string>();
                if (t.CodeNeedsReview) what.Add("statement");
                what.AddRange(t.CriteriaNeedingReview.Select(c => c.Id));
                Console.WriteLine($"{t.Requirement.Id}: changed {string.Join(", ", what)}");
            }

            Console.WriteLine(drifted.Count == 0 ? "No changes pending review." : $"{drifted.Count} requirement(s) with unreviewed changes.");
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
                    ?? throw new ArgumentException($"Requirement '{id}' does not exist.");
                if (t.Requirement.IsDeprecated) throw new ArgumentException($"{t.Requirement.Id} is deprecated: it is not recorded.");
                if (current.Find(t.Requirement.Id) == null) added++;
                else if (t.DriftInfo.Any) changed++;
                updated = updated.With(SpecTrace.Specs.SpecFingerprint.Entry(t.Requirement));
            }
        }
        else
        {
            // All active requirements; retired and draft ones are dropped from the file.
            var active = model.Requirements.Where(t => t.Requirement.IsEnforced).ToList();
            added = active.Count(t => current.Find(t.Requirement.Id) == null);
            changed = active.Count(t => t.DriftInfo.IsLocked && t.DriftInfo.Any);
            removed = current.Entries.Count(e => !active.Any(t => string.Equals(t.Requirement.Id, e.Id, StringComparison.OrdinalIgnoreCase)));
            updated = new SpecTrace.Specs.SpecLock(active.Select(t => SpecTrace.Specs.SpecFingerprint.Entry(t.Requirement)));
        }

        File.WriteAllText(path, updated.Render());
        Console.WriteLine($"{SpecTrace.Specs.SpecLock.FileName}: {updated.Count} requirements ({added} new, {changed} updated, {removed} removed).");
        if (ids.Count == 0 && changed > 0)
        {
            Console.WriteLine("Note: pending changes were marked as reviewed. If that was not intended, undo it with git.");
        }

        return 0;
    }

    private static int Import(Options opts, SpecTraceConfig config)
    {
        var kind = opts.Positional.ElementAtOrDefault(0)
            ?? throw new ArgumentException("Usage: dotnet spectrace import speckit|openspec <path>");
        var source = opts.Positional.ElementAtOrDefault(1)
            ?? throw new ArgumentException($"Missing path: dotnet spectrace import {kind} <path>");
        var status = opts.Get("status") ?? SpecTrace.Specs.SpecStatus.Approved;
        if (status is not ("approved" or "proposed")) throw new ArgumentException("--status must be approved or proposed.");
        var dryRun = opts.Has("dry-run");
        var outOption = opts.Get("out");

        var specs = new List<SpecTrace.Tool.Import.ImportedSpec>();
        switch (kind)
        {
            case "speckit":
            {
                var sources = SpecTrace.Tool.Import.SpecKitImporter.FindSources(source).ToList();
                if (sources.Count == 0) throw new ArgumentException($"No Spec Kit spec.md found in '{source}'.");
                if (outOption != null && sources.Count > 1) throw new ArgumentException("--out can only be used with a single feature.");
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
                if (sources.Count == 0) throw new ArgumentException($"No OpenSpec spec.md found in '{source}'.");
                if (outOption != null && sources.Count > 1) throw new ArgumentException("--out can only be used with a single capability.");
                if (opts.Get("prefix") != null && sources.Count > 1) throw new ArgumentException("--prefix can only be used with a single capability.");
                foreach (var (capability, file) in sources)
                {
                    var output = Path.GetFullPath(outOption ?? Path.Combine(config.Root, config.Specs, capability + SpecTrace.Specs.SpecParser.FileSuffix));
                    specs.Add(SpecTrace.Tool.Import.OpenSpecImporter.Parse(capability, file, File.ReadAllText(file), output,
                        opts.Get("prefix"), status, TraceScanner.Relative(config.Root, file)));
                }

                break;
            }

            default:
                throw new ArgumentException($"Unknown import format '{kind}'. Use speckit or openspec.");
        }

        foreach (var spec in specs)
        {
            var result = SpecTrace.Tool.Import.ImportWriter.Write(spec, dryRun);
            Console.WriteLine($"{(dryRun ? "[dry run] " : "")}{TraceScanner.Relative(config.Root, result.OutputPath)}: " +
                              $"{result.Requirements} requirements ({result.Added} new, {result.Kept} kept) with prefix {spec.SpecId}");
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
        Console.WriteLine($"Report written to {path}");
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
