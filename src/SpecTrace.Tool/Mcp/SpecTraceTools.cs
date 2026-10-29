using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SpecTrace.Specs;
using SpecTrace.Tool.Authoring;
using SpecTrace.Tool.Stories;

namespace SpecTrace.Tool.Mcp;

/// <summary>
/// SpecTrace MCP tools. Every call re-reads specs and code, so the agent always sees
/// the current state on disk. Paths it receives are confined to the repository root (spectrace.json).
/// </summary>
public sealed class SpecTraceTools
{
    private readonly SpecTraceConfig _config;
    private readonly Lazy<IStoryTracker> _tracker;
    private readonly Dictionary<string, (JsonObject Definition, Func<JsonObject, JsonObject> Handler)> _tools;

    /// <param name="tracker">Where user stories are read from. Default: GitHub Issues of this repository.</param>
    public SpecTraceTools(SpecTraceConfig config, Func<IStoryTracker>? tracker = null)
    {
        _config = config;
        _tracker = new Lazy<IStoryTracker>(tracker ?? (() => GitHubIssues.FromConfig(config)));
        _tools = new()
        {
            ["list_requirements"] = (Define("list_requirements", "List requirements",
                "Lists the requirements in the specs with their traceability status: implemented, tested and criteria covered. " +
                "Use only_gaps=true to see only what is missing.",
                Schema(
                    ("status", Str("Filter by status: draft, proposed, approved, implemented or deprecated.")),
                    ("only_gaps", Bool("If true, only active requirements that are not implemented, not tested or have criteria without tests.")),
                    ("only_changed", Bool("If true, only requirements whose text changed after they were reviewed (spectrace.lock)."))),
                readOnly: true), ListRequirements),

            ["get_requirement"] = (Define("get_requirement", "Requirement context",
                "Returns the statement, the acceptance criteria with their status, where it is implemented, which projects the code goes in " +
                "and with which usings, the related files and the exact markers ([Implements]/[Verifies]) to use. " +
                "Call it before implementing or testing a requirement.",
                Schema(new[] { "id" }, ("id", Str("Requirement ID, for example ORD-002."))),
                readOnly: true), GetRequirement),

            ["get_code"] = (Define("get_code", "Read repository code",
                "Returns the contents of code files so a change can use the real types instead of guessing them. " +
                "Give exactly one of: requirement_id (the files that implement and test the requirement or, if it has none yet, " +
                "those of other requirements in the same spec), type (the files that declare that type) or path (one file). " +
                "Only reads .cs, .csproj and .md files in the source and spec folders.",
                Schema(
                    ("requirement_id", Str("Requirement ID, for example ORD-003.")),
                    ("type", Str("Name of a C# type, for example Order or OrderService.")),
                    ("path", Str("Path of a file, relative to the repository root."))),
                readOnly: true), GetCode),

            ["get_traceability_report"] = (Define("get_traceability_report", "Traceability report",
                "Full requirement → code → test report, in Markdown (default) or JSON.",
                Schema(("format", EnumOf("Report format.", "md", "json"))),
                readOnly: true), GetReport),

            ["scaffold_tests"] = (Define("scaffold_tests", "Generate test skeletons",
                "Creates in <test_project>/Specs one xUnit file per requirement with a test (with Skip) for each criterion without a test. " +
                "Does not overwrite existing files. Each test must be completed and its Skip removed for it to count.",
                Schema(new[] { "test_project" },
                    ("test_project", Str("Test project folder, relative to the repository root.")),
                    ("requirement_id", Str("Only this requirement. If omitted, every requirement with criteria without tests."))),
                readOnly: false), ScaffoldTests),

            ["propose_requirement"] = (Define("propose_requirement", "Propose a requirement",
                "Appends a new requirement with Status: proposed to a spec (*.spec.md inside the specs folder). " +
                "Use it when the task asks for behavior that no spec describes; a person must approve it afterwards.",
                Schema(new[] { "spec_file", "title", "statement", "criteria" },
                    ("spec_file", Str("Target spec, relative to the specs folder (for example orders.spec.md). Created if it does not exist.")),
                    ("title", Str("Short title of the requirement.")),
                    ("statement", Str("Statement, preferably EARS: When/While/If ..., the system shall ...")),
                    ("criteria", new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["minItems"] = 1,
                        ["description"] = "Acceptance criteria in Given/When/Then form, one per item.",
                    }),
                    ("id", Str("Explicit ID. If omitted, the next number for the spec's prefix is used.")),
                    ("source", Str("Where the requirement comes from: a user story (#12, owner/repo#12, its URL) or any URL. Written as a Source: line.")),
                    ("open_questions", new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "What the source leaves undecided, one question per item. Written in the note for the person who approves the requirement.",
                    })),
                readOnly: false), ProposeRequirement),

            ["get_story"] = (Define("get_story", "Read a user story",
                "Reads a user story from the issue tracker (GitHub Issues): the user story, its acceptance criteria, notes and comments, " +
                "and the requirements that already cite it in a Source: line. Use it before turning a story into a spec.",
                Schema(new[] { "story" }, ("story", Str("The story: #12, owner/repo#12 or the issue URL."))),
                readOnly: true), GetStory),

            ["list_stories"] = (Define("list_stories", "List user stories",
                "Lists the user stories of the issue tracker (by default, open GitHub issues with the configured label) and the " +
                "requirements that come from each one. Use only_unspecified=true to see the stories that have no spec yet.",
                Schema(
                    ("label", Str("Only issues with this label. Default: trackers.github.label in spectrace.json (\"user-story\"); an empty string lists all issues.")),
                    ("state", EnumOf("Issue state. Default: open.", "open", "closed", "all")),
                    ("only_unspecified", Bool("If true, only stories that no requirement cites yet."))),
                readOnly: true), ListStories),
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
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or StoryException)
        {
            // Execution errors go in the result (isError) so the model can correct itself.
            return Text(ex.Message, isError: true);
        }
    }

    // ---------- Tools ----------

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
            ? "No requirements match the filter."
            : string.Join("\n", items.Select(i =>
                $"{i!["id"]} [{i["status"]}] {i["title"]} — " +
                $"{(i["implemented"]!.GetValue<bool>() ? "implemented" : "not implemented")}, " +
                $"criteria with tests {i["criteria_covered"]}/{i["criteria_total"]}" +
                (i["gap"]!.GetValue<bool>() ? " (gap)" : "") +
                (i["spec_changed"]!.GetValue<bool>() ? " (spec changed since the last review)" : "")));

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
            throw new ArgumentException("Give exactly one of: requirement_id, type or path.");
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
                throw new ArgumentException($"There is no type named '{type}' in {string.Join(", ", _config.Sources)}.");
            }

            intro = $"Files that declare `{type}`:\n\n";
        }
        else
        {
            var model = TraceScanner.Scan(_config);
            var trace = model.Requirements.FirstOrDefault(r => string.Equals(r.Requirement.Id, requirementId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Requirement '{requirementId}' does not exist.");
            var related = Reports.RelatedFiles(model, trace);
            var own = related.Where(f => f.Ids.Contains(trace.Requirement.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            files = (own.Count > 0 ? own : related).Select(f => f.File).Distinct().ToList();
            if (files.Count == 0)
            {
                throw new ArgumentException($"No file works with requirements from {trace.Requirement.FilePath} yet. Look up the domain types with type.");
            }

            intro = own.Count > 0
                ? $"Files that implement or test {trace.Requirement.Id}:\n\n"
                : $"{trace.Requirement.Id} has no code yet; these files work with other requirements from the same spec:\n\n";
        }

        var sb = new System.Text.StringBuilder(intro);
        var shown = 0;
        foreach (var relative in files)
        {
            if (shown == MaxFiles || sb.Length > MaxChars)
            {
                sb.AppendLine($"(… {files.Count - shown} more file(s): {string.Join(", ", files.Skip(shown))}. Ask for them with path.)");
                break;
            }

            var lines = File.ReadAllLines(Path.Combine(_config.Root, relative));
            var language = Path.GetExtension(relative).ToLowerInvariant() switch { ".cs" => "csharp", ".csproj" => "xml", _ => "markdown" };
            sb.AppendLine($"### {relative} ({lines.Length} lines)");
            sb.AppendLine();
            sb.AppendLine("```" + language);
            foreach (var line in lines.Take(MaxLinesPerFile)) sb.AppendLine(line);
            sb.AppendLine("```");
            if (lines.Length > MaxLinesPerFile) sb.AppendLine($"(truncated: showing {MaxLinesPerFile} of {lines.Length} lines)");
            sb.AppendLine();
            shown++;
        }

        return Text(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// A file get_code may read: inside the source or spec folders, with an allowed extension
    /// and outside bin/obj/.git. This keeps secrets (.env, appsettings.*.json, keys...) from being exposed.
    /// </summary>
    private string ReadableFile(string relative)
    {
        var full = InsideRoot(relative);
        var allowedRoots = _config.Sources.Append(_config.Specs).Select(s => Path.GetFullPath(Path.Combine(_config.Root, s)));
        if (!allowedRoots.Any(r => IsUnder(full, r)))
        {
            throw new ArgumentException($"'{relative}' is not in the source folders ({string.Join(", ", _config.Sources)}) or the specs folder ({_config.Specs}).");
        }

        if (!CodeExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
        {
            throw new ArgumentException($"get_code only reads {string.Join(", ", CodeExtensions)} files.");
        }

        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p is "bin" or "obj" or ".git"))
        {
            throw new ArgumentException("get_code does not read files in bin, obj or .git.");
        }

        if (!File.Exists(full)) throw new ArgumentException($"'{relative}' does not exist.");
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
        if (!Directory.Exists(project)) throw new ArgumentException($"Folder '{RequiredString(args, "test_project")}' does not exist.");

        var files = Scaffolder.ScaffoldTests(TraceScanner.Scan(_config), project, OptionalString(args, "requirement_id"), ns: null);
        return Text(files.Count == 0
            ? "There were no criteria without tests (or the files already existed). Nothing was created."
            : "Created (complete each test and remove its Skip):\n" + string.Join("\n", files.Select(f => "- " + TraceScanner.Relative(_config.Root, f))));
    }

    private JsonObject ProposeRequirement(JsonObject args)
    {
        var specsDir = Path.GetFullPath(Path.Combine(_config.Root, _config.Specs));
        var relative = RequiredString(args, "spec_file");
        if (!SpecParser.IsSpecFile(relative)) throw new ArgumentException($"'{relative}' must end in {SpecParser.FileSuffix}.");

        var path = Path.GetFullPath(Path.Combine(specsDir, relative));
        if (!IsUnder(path, specsDir)) throw new ArgumentException($"'{relative}' is outside the specs folder.");

        var title = RequiredString(args, "title");
        var statement = RequiredString(args, "statement");
        var criteria = (args["criteria"] as JsonArray)?.Select(c => c?.GetValue<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList()
            ?? new List<string>();
        if (criteria.Count == 0) throw new ArgumentException("At least one acceptance criterion is required.");

        var catalog = TraceScanner.Scan(_config).Catalog;
        var existing = File.Exists(path) ? SpecParser.Parse(path, File.ReadAllText(path)).Requirements.Select(r => r.Id).ToList() : new List<string>();

        // IDs already taken on the base branch (work merged after this branch started), so two people do not both
        // propose "the next ID". Best effort: without git or a base branch it is empty.
        var (baseName, baseIds) = BaseIds();

        var id = OptionalString(args, "id")?.Trim().ToUpperInvariant();
        if (id == null)
        {
            if (existing.Count == 0) throw new ArgumentException("The spec is empty: give the requirement ID (for example PAY-001).");
            var first = SpecWriter.NextId(existing);
            var prefix = first.Substring(0, first.LastIndexOf('-'));
            var taken = existing.Concat(baseIds).ToList();
            id = SpecWriter.NextId(taken, prefix);
            while (catalog.TryGet(id, out _)) id = SpecWriter.NextId(taken.Append(id), prefix);
        }

        if (!SpecWriter.IsValidId(id)) throw new ArgumentException($"'{id}' is not a valid ID (PREFIX-123).");
        if (catalog.TryGet(id, out var clash)) throw new ArgumentException($"ID {id} already exists in {clash.FilePath}.");
        if (baseIds.Contains(id)) throw new ArgumentException($"ID {id} is already used on {baseName}: pull it into this branch or leave the ID empty to get a free one.");

        var source = OptionalString(args, "source")?.Trim();
        if (source != null && _tracker.Value.TryParse(source, out var story)) source = story.Url;
        var questions = (args["open_questions"] as JsonArray)?.Select(q => q?.GetValue<string>()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q!).ToList()
            ?? new List<string>();

        var block = SpecWriter.Render(new RequirementDraft(id, title, SpecStatus.Proposed, statement, criteria,
            Note: $"Proposed by an agent on {DateTime.Now:yyyy-MM-dd}. Review it and change the status to approved to enforce it.")
        {
            Source = source,
            OpenQuestions = questions,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var separator = current.Length == 0 ? "" : current.EndsWith("\n\n", StringComparison.Ordinal) ? "" : current.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
        File.WriteAllText(path, current + separator + block);

        var siblings = source == null
            ? new List<string>()
            : catalog.Requirements.Where(r => r.Source != null && SameSource(r.Source, source)).Select(r => r.Id).ToList();
        return Text($"Added {id} ({title}) with Status: proposed to {TraceScanner.Relative(_config.Root, path)}. " +
                    "It is not enforced until someone approves it (Status: approved)." +
                    (questions.Count > 0 ? $" {questions.Count} open question(s) recorded for the reviewer." : "") +
                    (siblings.Count > 0 ? $" The same source is cited by {string.Join(", ", siblings)}." : ""));
    }

    private (string? Base, HashSet<string> Ids) BaseIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var git = Git.Open(_config.Root);
            var @base = git?.DefaultBase();
            if (git == null || @base == null) return (null, ids);

            var specsDir = git.Prefix + _config.Specs.Replace('\\', '/').TrimEnd('/');
            foreach (var path in git.ListFiles(@base, specsDir).Where(SpecParser.IsSpecFile))
            {
                foreach (var r in SpecParser.Parse(path, git.Show(@base, path)).Requirements) ids.Add(r.Id);
            }

            return (@base, ids);
        }
        catch (InvalidOperationException)
        {
            return (null, ids);
        }
    }

    private bool SameSource(string a, string b) =>
        _tracker.Value.TryParse(a, out var ra) && _tracker.Value.TryParse(b, out var rb)
            ? ra.SameAs(rb)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private JsonObject GetStory(JsonObject args)
    {
        var tracker = _tracker.Value;
        var reference = Parse(tracker, RequiredString(args, "story"));
        var story = tracker.Get(reference);
        var specified = StoryReports.RequirementsFrom(TraceScanner.Scan(_config), tracker, reference);

        var result = Text(StoryReports.Markdown(story, specified));
        result["structuredContent"] = new JsonObject
        {
            ["key"] = story.Ref.Key,
            ["url"] = story.Ref.Url,
            ["title"] = story.Title,
            ["state"] = story.State,
            ["labels"] = new JsonArray(story.Labels.Select(l => (JsonNode)l).ToArray()),
            ["user_story"] = story.Narrative,
            ["acceptance_criteria"] = new JsonArray(story.AcceptanceCriteria.Select(c => (JsonNode)c).ToArray()),
            ["requirements"] = new JsonArray(specified.Select(r => (JsonNode)r.Id).ToArray()),
        };
        return result;
    }

    private JsonObject ListStories(JsonObject args)
    {
        var tracker = _tracker.Value;
        var label = args["label"] is JsonValue v && v.TryGetValue<string>(out var l) ? l : _config.Trackers.GitHub.Label;
        var state = OptionalString(args, "state") ?? "open";
        var onlyUnspecified = args["only_unspecified"]?.GetValue<bool>() ?? false;

        var model = TraceScanner.Scan(_config);
        var rows = tracker.List(new StoryQuery(label, state))
            .Select(s => (Story: s, Specified: StoryReports.RequirementsFrom(model, tracker, s.Ref)))
            .Where(r => !onlyUnspecified || r.Specified.Count == 0)
            .ToList();

        var repository = (tracker as GitHubIssues)?.DefaultRepository ?? tracker.Name;
        var result = Text(StoryReports.List(rows, repository, label));
        result["structuredContent"] = new JsonObject
        {
            ["stories"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["key"] = r.Story.Ref.Key,
                ["url"] = r.Story.Ref.Url,
                ["title"] = r.Story.Title,
                ["state"] = r.Story.State,
                ["requirements"] = new JsonArray(r.Specified.Select(x => (JsonNode)x.Id).ToArray()),
            }).ToArray()),
        };
        return result;
    }

    private static StoryRef Parse(IStoryTracker tracker, string text) =>
        tracker is GitHubIssues github ? github.Parse(text)
        : tracker.TryParse(text, out var reference) ? reference
        : throw new ArgumentException($"'{text}' is not a {tracker.Name} story reference.");

    // ---------- Helpers ----------

    private string InsideRoot(string relative)
    {
        var root = Path.GetFullPath(_config.Root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnder(full, root)) throw new ArgumentException($"'{relative}' is outside the repository.");
        return full;
    }

    private static bool IsUnder(string path, string dir)
    {
        var d = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return path.StartsWith(d, StringComparison.Ordinal) || path == dir;
    }

    private static string RequiredString(JsonObject args, string name) =>
        OptionalString(args, name) ?? throw new ArgumentException($"Missing argument '{name}'.");

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
