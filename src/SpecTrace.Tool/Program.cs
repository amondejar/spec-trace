using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SpecTrace.Tool;
using SpecTrace.Tool.Automation;
using SpecTrace.Tool.Stories;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        SpecTrace: Spec-Driven Development traceability for .NET

        Usage: dotnet spectrace <command> [options]
               dotnet spectrace --version

        Commands:
          report          Requirement → code → test matrix.
                            --format md|json|github
                                               md by default; github = GitHub Actions annotations
                            --link-base <url>  prefix for absolute links in md (PR comments)
                            --out <file>       write to a file instead of the console
                            --fail-on-gaps     exit code 1 if active requirements are not implemented or not tested
                            --fail-on-drift    exit code 1 if there are unreviewed spec changes (spectrace.lock)
                            --test-results <path>
                                               .trx files or folders (dotnet test --logger trx): shows whether each
                                               criterion's tests pass; repeatable
                            --fail-on-failing  exit code 1 if a criterion has a failing test (needs --test-results)
                            --base <ref>       adds the requirements touched since that branch (see impact)
                            --mutation <path>  Stryker mutation-report.json, or a folder (the newest one in it):
                                               mutants detected in each requirement's code, and which survive
                            --min-mutation-score <n>
                                               exit code 1 if a requirement's mutants detected are below n %
          lock            Records the current version of the spec as reviewed (spectrace.lock).
                            --requirement <ID> only that requirement (repeatable); without it, all active ones
                            --check            does not write: exit code 1 if there are unreviewed changes
                                               or conflict markers
                            --tidy             only tidies the file after a merge (duplicate lines, conflict
                                               markers), keeping every review that matches the spec
          ids             Requirement IDs used twice, usually by two branches that proposed "the next ID".
                            --fix              renumbers what this branch added (spec, spectrace.lock and the
                                               code and tests added by the branch); review it with git diff
                            --base <ref>       branch to compare with (default origin/main, origin/master, main...)
          impact          Requirements touched by the current branch compared with its base: spec changes,
                          implementation and tests on changed lines, removed requirements, untraced code.
                            --base <ref>       default origin/main, origin/master, main or master
                            --format md|json
          pending         The work queue: approved requirements still missing their implementation or tests.
                            --since <ref>      only those whose spec changed since that commit (what a merge approved)
                            --requirement <ID> only that requirement (repeatable)
                            --format md|json|ids
                                               ids = one ID per line, for scripts
          prompt <name> <argument>
                          Prints an MCP prompt (implement_requirement, review_spec, strengthen_tests, story_to_spec) for
                          an agent that runs without MCP prompts, such as a CI job. Without a name it lists them.
          context <ID>    Requirement context for an agent: statement, criteria, status and how to mark the work.
          architecture    Principles and rules of the architecture specs (kind: architecture), as agents get them.
                            --format md|json
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
          init            Creates what is missing of spectrace.json, an example spec, the agent skill, .mcp.json and
                          the .gitattributes line for spectrace.lock. In a subfolder it uses the existing
                          spectrace.json above, or the root of the git repository.
                            --skills-dir <dir> skills folder (default .claude/skills)
                            --here             set up the current folder even inside another root
                            --architecture     also specs/architecture.spec.md: principles and rules (proposed) to adapt
                            --agent            also the GitHub Actions workflow in which an agent implements each
                                               approved requirement and opens a pull request for review

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

        if (args[0] is "--version" or "-v" or "version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        try
        {
            var opts = Options.Parse(args.Skip(1).ToArray());
            if (CommandOptions.TryGetValue(args[0], out var allowed)) opts.Check(args[0], allowed);
            var config = SpecTraceConfig.Load(opts.Get("root") ?? Directory.GetCurrentDirectory());

            switch (args[0])
            {
                case "report":
                {
                    var model = TraceScanner.Scan(config);
                    var results = opts.GetAll("test-results");
                    if (results.Count > 0) model = model with { TestResults = SpecTrace.Tool.Results.TestResults.Load(results) };
                    if (opts.Get("mutation") is { } mutation) model = model with { Mutations = SpecTrace.Tool.Results.MutationReport.Load(mutation) };
                    var repoRoot = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } ws
                        ? ws
                        : Directory.GetCurrentDirectory();
                    var impact = opts.Has("base") ? Impact.Compute(config, model, opts.Get("base")) : null;
                    var text = opts.Get("format") switch
                    {
                        "json" => Reports.Json(model, impact),
                        "github" => Reports.GitHub(model, repoRoot),
                        "md" or null => Reports.Markdown(model, opts.Get("link-base"), impact),
                        var other => throw new ArgumentException($"Unknown format '{other}'. Use md, json or github."),
                    };
                    Write(text, opts.Get("out"));
                    var gaps = model.Requirements.Count(r => r.HasGap) + model.UnknownLinks.Count;
                    var drift = model.Requirements.Count(r => r.HasDrift);
                    var failing = model.Requirements.Sum(t => t.Requirement.Criteria.Count(c => model.OutcomeOf(t, c.Id) == SpecTrace.Tool.Results.TestOutcome.Failed));
                    var weak = opts.Get("min-mutation-score") is { } min && double.TryParse(min, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold)
                        ? model.Requirements.Count(t => t.Requirement.IsEnforced && model.MutationOf(t)?.Score is { } score && score < threshold)
                        : 0;
                    return (opts.Has("fail-on-gaps") && gaps > 0) || (opts.Has("fail-on-drift") && drift > 0) || (opts.Has("fail-on-failing") && failing > 0) || weak > 0 ? 1 : 0;
                }

                case "impact":
                {
                    var model = TraceScanner.Scan(config);
                    var impact = Impact.Compute(config, model, opts.Get("base"));
                    Console.WriteLine(opts.Get("format") == "json"
                        ? System.Text.Json.JsonSerializer.Serialize(Impact.Json(impact), JsonOptions)
                        : Impact.Markdown(impact));
                    return 0;
                }

                case "pending":
                {
                    var model = TraceScanner.Scan(config);
                    var since = opts.Get("since");
                    if (opts.Has("since") && since == null) throw new ArgumentException("--since needs a commit or branch.");
                    var work = SpecTrace.Tool.Automation.WorkQueue.Compute(config, model, since, opts.GetAll("requirement"));
                    var text = opts.Get("format") switch
                    {
                        "json" => System.Text.Json.JsonSerializer.Serialize(SpecTrace.Tool.Automation.WorkQueue.Json(work), JsonOptions),
                        "ids" => SpecTrace.Tool.Automation.WorkQueue.Ids(work),
                        "md" or null => SpecTrace.Tool.Automation.WorkQueue.Markdown(work, since),
                        var other => throw new ArgumentException($"Unknown format '{other}'. Use md, json or ids."),
                    };
                    // An empty queue in the ids format is empty output, so scripts can test it with [ -s file ].
                    if (text.Length > 0) Console.WriteLine(text);
                    return 0;
                }

                case "prompt":
                {
                    var name = opts.Positional.ElementAtOrDefault(0);
                    if (name == null)
                    {
                        foreach (var p in SpecTrace.Tool.Mcp.SpecTracePrompts.Catalog)
                        {
                            Console.WriteLine($"{p.Name} <{p.Argument}>\n    {p.Description}");
                        }

                        return 0;
                    }

                    var argument = string.Join(" ", opts.Positional.Skip(1));
                    Console.WriteLine(SpecTrace.Tool.Mcp.SpecTracePrompts.Text(name, argument));
                    return 0;
                }

                case "architecture":
                {
                    var architecture = TraceScanner.Scan(config).Architecture;
                    Console.WriteLine(opts.Get("format") switch
                    {
                        "json" => System.Text.Json.JsonSerializer.Serialize(new
                        {
                            principles = architecture.Principles.Select(p => new { file = p.FilePath, text = p.Text }),
                            rules = architecture.Rules.Select(r => new
                            {
                                id = r.Id,
                                title = r.Title,
                                status = r.Status,
                                enforced = r.IsEnforced,
                                @in = r.Scope,
                                mustNotUse = r.Forbidden,
                                useInstead = r.UseInstead,
                                rationale = r.Rationale,
                                spec = $"{r.FilePath}:{r.Line + 1}",
                            }),
                            issues = architecture.Issues.Select(i => new { message = i.Message, spec = $"{i.FilePath}:{i.Line + 1}" }),
                        }, JsonOptions),
                        "md" or null => architecture.Markdown(),
                        var other => throw new ArgumentException($"Unknown format '{other}'. Use md or json."),
                    });
                    return 0;
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

                case "ids":
                    return Ids(opts, config);

                case "init":
                    return Init(opts);

                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}\n");
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or StoryException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>The package version, without the commit the SDK appends ("0.7.1-preview.1+abc123" → "0.7.1-preview.1").</summary>
    internal static string Version =>
        (typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    /// <summary>The options each command understands; anything else is a typo and fails instead of being ignored.</summary>
    private static readonly Dictionary<string, string[]> CommandOptions = new()
    {
        ["report"] = new[] { "format", "link-base", "out", "fail-on-gaps", "fail-on-drift", "test-results", "fail-on-failing", "base", "mutation", "min-mutation-score" },
        ["lock"] = new[] { "requirement", "check", "tidy" },
        ["ids"] = new[] { "fix", "base" },
        ["impact"] = new[] { "base", "format" },
        ["context"] = Array.Empty<string>(),
        ["architecture"] = new[] { "format" },
        ["pending"] = new[] { "since", "requirement", "format" },
        ["prompt"] = Array.Empty<string>(),
        ["scaffold-tests"] = new[] { "project", "requirement", "namespace" },
        ["story"] = new[] { "format" },
        ["stories"] = new[] { "label", "state", "unspecified", "fail-on-unspecified", "format" },
        ["mcp"] = Array.Empty<string>(),
        ["import"] = new[] { "prefix", "out", "status", "dry-run" },
        ["init"] = new[] { "skills-dir", "here", "agent", "architecture" },
    };

    /// <summary>
    /// IDs used twice after two branches proposed "the next ID" of the same spec. Without --fix it only reports them
    /// (exit code 1), so it can run in CI right after a merge.
    /// </summary>
    private static int Ids(Options opts, SpecTraceConfig config)
    {
        var plan = SpecTrace.Tool.Authoring.IdFixer.Plan(config, opts.Get("base"));
        foreach (var note in plan.Notes) Console.WriteLine("Note: " + note);
        var against = plan.Base != null ? $" (compared with {plan.Base})" : "";

        if (!plan.HasWork)
        {
            Console.WriteLine($"No requirement ID is used twice{against}.");
            return 0;
        }

        Console.WriteLine($"Requirement IDs used twice{against}:");
        foreach (var c in plan.Collisions)
        {
            var o = c.Occurrence;
            Console.WriteLine($"  {o.Id} \"{o.Title}\" ({o.File}:{o.Line + 1}) → {c.NewId ?? "rename by hand"}: {c.Reason}");
        }

        if (!opts.Has("fix"))
        {
            Console.WriteLine("\nRun `dotnet spectrace ids --fix` to renumber the requirements this branch added.");
            return 1;
        }

        Console.WriteLine();
        foreach (var change in SpecTrace.Tool.Authoring.IdFixer.Apply(config, plan))
        {
            Console.WriteLine($"Renumbered {change.OldId} → {change.NewId} in {string.Join(", ", change.Files)} " +
                              $"({change.CodeReferences} code reference(s), {change.LockLines} spectrace.lock line(s)).");
        }

        Console.WriteLine("Review it with `git diff` and build: ST0007 should be gone.");
        return plan.Collisions.Any(c => c.NewId == null) ? 1 : 0;
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
        var root = InitRoot(Directory.GetCurrentDirectory(), opts.Get("root"), opts.Has("here"), Console.Out);
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
        // The example only for a new setup: an existing specs folder with specs does not need it.
        var specsDir = Path.Combine(root, SpecTraceConfig.Load(root).Specs);
        if (!(Directory.Exists(specsDir) && Directory.EnumerateFiles(specsDir, "*" + SpecTrace.Specs.SpecParser.FileSuffix, SearchOption.AllDirectories).Any()))
        {
            Create(Path.Combine("specs", "example.spec.md"), Template("example.spec.md"));
        }
        Create(Path.GetRelativePath(root, Path.Combine(skillsDir, "spectrace", "SKILL.md")), Template("SKILL.md"));
        // MCP server registration for agents that read .mcp.json at the project root (for example Claude Code).
        Create(".mcp.json", "{\n  \"mcpServers\": {\n    \"spectrace\": {\n      \"command\": \"dotnet\",\n      \"args\": [\"spectrace\", \"mcp\"]\n    }\n  }\n}\n");

        // Two branches that review different requirements add lines next to each other in spectrace.lock: with the union
        // driver git keeps both instead of reporting a conflict (local merges and rebases; GitHub's web merge ignores it).
        const string unionLine = "spectrace.lock merge=union";
        var attributes = Path.Combine(root, ".gitattributes");
        var existing = File.Exists(attributes) ? File.ReadAllText(attributes) : null;
        if (existing == null || !existing.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim().StartsWith("spectrace.lock", StringComparison.Ordinal)))
        {
            var prefix = string.IsNullOrEmpty(existing) || existing.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n";
            File.AppendAllText(attributes, prefix + unionLine + "\n");
            created.Add(existing == null ? ".gitattributes" : ".gitattributes (spectrace.lock merge=union)");
        }

        if (opts.Has("architecture"))
        {
            var specsFolder = SpecTraceConfig.Load(root).Specs;
            var hasArchitecture = Directory.Exists(Path.Combine(root, specsFolder)) && Directory
                .EnumerateFiles(Path.Combine(root, specsFolder), "*" + SpecTrace.Specs.SpecParser.FileSuffix, SearchOption.AllDirectories)
                .Any(f => SpecTrace.Specs.ArchitectureParser.IsArchitecture(File.ReadAllText(f)));
            if (hasArchitecture) Console.WriteLine("There is already an architecture spec (kind: architecture): not creating another one.");
            else Create(Path.Combine(specsFolder, "architecture.spec.md"), Template("architecture.spec.md").Replace("{{root}}", RootNamespace(root)));
        }

        if (opts.Has("agent"))
        {
            var workflow = Path.Combine(".github", "workflows", AgentWorkflow.FileName);
            if (File.Exists(Path.Combine(root, workflow)))
            {
                Console.WriteLine($"{workflow.Replace('\\', '/')} already exists and was not changed: delete it and run `dotnet spectrace init --agent` again for this version's template.");
            }

            Create(workflow, AgentWorkflow.Render(Template(AgentWorkflow.FileName), SpecTraceConfig.Load(root).Specs, AgentWorkflow.DefaultBranch(root),
                Git.Open(root)?.Prefix ?? ""));
        }

        Console.WriteLine(created.Count == 0 ? "Everything already exists." : "Created:\n" + string.Join("\n", created.Select(c => "  " + c)));
        Console.WriteLine("\nNext step: add the package to your projects with  dotnet add package SpecTrace --prerelease");
        if (opts.Has("agent"))
        {
            foreach (var line in AgentWorkflow.Checklist(root)) Console.WriteLine(line);
        }

        return 0;
    }

    /// <summary>
    /// The application's root namespace for the architecture example: the first segment of the first project that is not a
    /// test project (Shop.Api → Shop), or "MyApp".
    /// </summary>
    private static string RootNamespace(string root)
    {
        var projects = TraceScanner.FindProjects(SpecTraceConfig.Load(root)).Where(p => !p.IsTest).OrderBy(p => p.Directory, StringComparer.Ordinal);
        var name = projects.Select(p => p.Name).FirstOrDefault();
        return string.IsNullOrEmpty(name) ? "MyApp" : name!.Split('.')[0];
    }

    /// <summary>
    /// Where init works: --root as given; otherwise the folder of an existing spectrace.json above the current one
    /// (so running it inside a project completes the repository's setup instead of creating a second one), then the git
    /// top level, then the current folder. --here forces the current folder.
    /// </summary>
    internal static string InitRoot(string current, string? explicitRoot, bool here, TextWriter log)
    {
        if (explicitRoot != null) return Path.GetFullPath(explicitRoot);
        current = Path.GetFullPath(current);
        if (here) return current;

        for (var dir = new DirectoryInfo(current); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, SpecTraceConfig.FileName))) continue;
            if (!string.Equals(dir.FullName, current, StringComparison.OrdinalIgnoreCase))
            {
                log.WriteLine($"Using {dir.FullName}: its {SpecTraceConfig.FileName} already configures this folder (--here to set up a separate root).");
            }

            return dir.FullName;
        }

        var git = Git.Open(current);
        if (git != null)
        {
            var top = Path.GetFullPath(git.TopLevel);
            if (!string.Equals(top.TrimEnd(Path.DirectorySeparatorChar), current.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                log.WriteLine($"Using the repository root {top} (--here to set up {current} instead).");
            }

            return top;
        }

        return current;
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

            if (model.Lock!.ConflictMarkerLines.Count > 0)
            {
                Console.WriteLine($"spectrace.lock has git conflict markers (line {model.Lock.ConflictMarkerLines[0] + 1}): run `dotnet spectrace lock --tidy`.");
            }

            if (model.Lock.Duplicates.Count > 0)
            {
                Console.WriteLine($"Merged reviews for {string.Join(", ", model.Lock.Duplicates)} (one line per branch); the one matching the spec is used. " +
                                  "`dotnet spectrace lock --tidy` leaves one line each.");
            }

            foreach (var t in drifted)
            {
                var what = new List<string>();
                if (t.CodeNeedsReview) what.Add("statement");
                what.AddRange(t.CriteriaNeedingReview.Select(c => c.Id));
                Console.WriteLine($"{t.Requirement.Id}: changed {string.Join(", ", what)}");
            }

            Console.WriteLine(drifted.Count == 0 ? "No changes pending review." : $"{drifted.Count} requirement(s) with unreviewed changes.");
            return drifted.Count == 0 && model.Lock.ConflictMarkerLines.Count == 0 ? 0 : 1;
        }

        var ids = opts.GetAll("requirement");
        // After a merge the file can have one line per branch for a requirement: keep the review that matches the spec.
        var merged = model.Lock ?? new SpecTrace.Specs.SpecLock(Array.Empty<SpecTrace.Specs.SpecLockEntry>());
        var tidied = merged.Duplicates.Count + (merged.ConflictMarkerLines.Count > 0 ? 1 : 0);
        var current = merged.Resolve(model.Catalog.Requirements);

        if (opts.Has("tidy"))
        {
            if (!model.HasLock) throw new ArgumentException("There is no spectrace.lock to tidy.");
            File.WriteAllText(path, current.Render());
            Console.WriteLine(tidied == 0
                ? "spectrace.lock was already tidy."
                : $"spectrace.lock tidied: {merged.Duplicates.Count} requirement(s) had one line per branch" +
                  (merged.ConflictMarkerLines.Count > 0 ? " and the conflict markers were removed" : "") + ". No review was added or changed.");
            return 0;
        }
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

        /// <summary>Fails on an option the command does not know, suggesting the closest one (--root is always valid).</summary>
        public void Check(string command, IReadOnlyCollection<string> allowed)
        {
            var known = allowed.Append("root").ToList();
            foreach (var name in _named.Keys.Where(n => !known.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                var closest = SpecTrace.Specs.SpecNaming.Closest(known, name, 1).FirstOrDefault();
                throw new ArgumentException($"Unknown option --{name} for '{command}'." +
                    (closest != null ? $" Did you mean --{closest}?" : "") +
                    $" Options: {string.Join(", ", known.Select(k => "--" + k))}.");
            }
        }

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
