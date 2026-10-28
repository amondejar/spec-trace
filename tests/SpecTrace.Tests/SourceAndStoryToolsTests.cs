using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Authoring;
using SpecTrace.Tool.Mcp;
using SpecTrace.Tool.Stories;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.5: the Source: line in specs, and the story tools and prompts of the MCP server.</summary>
    public class SourceAndStoryToolsTests : IDisposable
    {
        private const string Spec = @"# Orders

## ORD-001: Create order
Status: approved
Source: https://github.com/acme/shop/issues/3

The system shall create orders.

- AC-1: Given 2 lines, then 2 lines.

## ORD-002: Cancel order
Status: approved

> Source: [#4](https://github.com/acme/shop/issues/4) (from the planning meeting)

The system shall cancel orders.

- AC-1: Given Pending, then Cancelled.
";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-stories-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGitHub _github = new();
        private readonly McpServer _server;

        public SourceAndStoryToolsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "specs"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            File.WriteAllText(Path.Combine(_root, "spectrace.json"),
                "{ \"specs\": \"specs\", \"sources\": [\"src\"], \"trackers\": { \"github\": { \"repository\": \"acme/shop\" } } }");
            File.WriteAllText(Path.Combine(_root, "specs", "orders.spec.md"), Spec);
            File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"),
                "public class OrderService\n{\n    [Implements(Requirements.ORD_001)] public void Create() { }\n}\n");

            var config = SpecTraceConfig.Load(_root);
            _server = new McpServer(config, tracker: () => new GitHubIssues(config.Trackers.GitHub, "acme/shop", () => null, _github));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private JsonObject Send(string json) => _server.Handle(json) ?? throw new InvalidOperationException("No response");

        private JsonObject Call(string tool, string arguments) =>
            (JsonObject)Send($"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}")["result"]!;

        private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

        // ---------- Source: in the spec format ----------

        [Fact]
        public void Source_lines_are_metadata_of_the_requirement()
        {
            var doc = SpecParser.Parse("orders.spec.md", Spec);

            Assert.Equal("https://github.com/acme/shop/issues/3", doc.Requirements[0].Source);
            Assert.Equal("https://github.com/acme/shop/issues/4", doc.Requirements[1].Source);
            Assert.Equal("The system shall create orders.", doc.Requirements[0].Statement);
            Assert.Equal("The system shall cancel orders.", doc.Requirements[1].Statement);
            Assert.Empty(doc.Issues);
        }

        [Fact]
        public void A_source_after_the_statement_is_just_text()
        {
            var doc = SpecParser.Parse("x.spec.md", "## A-1: One\nThe system shall one.\nSource: the old wiki.\n\n- AC-1: x\n");

            Assert.Null(doc.Requirements[0].Source);
            Assert.Equal("The system shall one. Source: the old wiki.", doc.Requirements[0].Statement);
        }

        [Fact]
        public void Adding_a_source_is_not_a_spec_change()
        {
            var without = SpecParser.Parse("x.spec.md", "## A-1: One\nThe system shall one.\n\n- AC-1: x\n").Requirements[0];
            var with = SpecParser.Parse("x.spec.md", "## A-1: One\nSource: #12\n\nThe system shall one.\n\n- AC-1: x\n").Requirements[0];

            Assert.Equal("#12", with.Source);
            Assert.Equal(new SpecLock(new[] { SpecFingerprint.Entry(without) }).Render(), new SpecLock(new[] { SpecFingerprint.Entry(with) }).Render());
        }

        [Fact]
        public void The_generated_catalog_links_to_the_source()
        {
            var result = TestHarness.Run("public class Plain { }", Spec);

            var catalog = string.Join("\n", result.Generated.Select(g => g.SourceText.ToString()));
            Assert.Contains("/// <seealso href=\"https://github.com/acme/shop/issues/3\">https://github.com/acme/shop/issues/3</seealso>", catalog);
        }

        [Fact]
        public void Written_requirements_keep_source_and_open_questions_out_of_the_statement()
        {
            var text = SpecWriter.Render(new RequirementDraft("ORD-003", "Move an order", SpecStatus.Proposed,
                "When the customer moves an order, the system shall keep it.", new[] { "Given an order, when moved, then kept." },
                Note: "Proposed by an agent.")
            {
                Source = "https://github.com/acme/shop/issues/5",
                OpenQuestions = new[] { "Can it change day?", "Can it change room?" },
            });

            Assert.Contains("Status: proposed\nSource: https://github.com/acme/shop/issues/5\n\n> Proposed by an agent.\n> Open questions:\n> - Can it change day?\n", text.Replace("\r\n", "\n"));
            var parsed = SpecParser.Parse("x.spec.md", text).Requirements.Single();
            Assert.Equal("https://github.com/acme/shop/issues/5", parsed.Source);
            Assert.Equal("When the customer moves an order, the system shall keep it.", parsed.Statement);
            Assert.Single(parsed.Criteria);
        }

        [Fact]
        public void Reports_link_each_requirement_to_its_source()
        {
            var model = TraceScanner.Scan(SpecTraceConfig.Load(_root));

            Assert.Contains("Create order · [#3](https://github.com/acme/shop/issues/3) |", Reports.Markdown(model));
            Assert.Contains("\"source\": \"https://github.com/acme/shop/issues/4\"", Reports.Json(model));
            Assert.Contains("· Source: https://github.com/acme/shop/issues/3", Reports.Context(model, "ORD-001"));
        }

        // ---------- MCP tools ----------

        [Fact]
        public void The_story_tools_are_listed()
        {
            var tools = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>()).ToList();

            Assert.Contains(tools, t => t == "get_story");
            Assert.Contains(tools, t => t == "list_stories");
        }

        [Fact]
        public void Get_story_shows_the_story_and_the_requirements_that_cite_it()
        {
            _github.Issue("acme/shop", 3, "Create an order", StoriesTests.FormBody, new[] { "user-story" }, comments: 1)
                .Route("/repos/acme/shop/issues/3/comments?per_page=100", FakeGitHub.Comments(("luis", "Same day only.")));

            var result = Call("get_story", "{\"story\":\"#3\"}");
            var text = TextOf(result);

            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Contains("# Story #3: Create an order", text);
            Assert.Contains("As an organizer, I want to move one of my bookings", text);
            Assert.Contains("- Only I can move my bookings.", text);
            Assert.Contains("> Same day only.", text);
            Assert.Contains("- ORD-001: Create order (approved) — specs/orders.spec.md:3", text);
            Assert.DoesNotContain(new[] { text }, t => t.Contains("## Notes"));
            Assert.Equal("ORD-001", result["structuredContent"]!["requirements"]![0]!.GetValue<string>());
        }

        [Fact]
        public void Get_story_errors_are_tool_errors_the_agent_can_read()
        {
            var result = Call("get_story", "{\"story\":\"#99\"}");

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("Issue #99 was not found in acme/shop", TextOf(result));
        }

        [Fact]
        public void List_stories_shows_which_stories_have_no_spec()
        {
            _github.Route("/repos/acme/shop/issues?state=open&per_page=100&page=1&labels=user-story",
                FakeGitHub.IssueList((3, "Create", false), (4, "Cancel", false), (5, "Fair use", false), (6, "A pull request", true)));

            var all = TextOf(Call("list_stories", "{}"));
            var pending = Call("list_stories", "{\"only_unspecified\":true}");

            Assert.Contains("3 stories, 2 with a spec, 1 without.", all);
            Assert.Contains("| [#4](https://github.com/acme/shop/issues/4) | Cancel | open | ORD-002 (approved) |", all);
            var keys = pending["structuredContent"]!["stories"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "acme/shop#5" }, keys.ToArray());
        }

        [Fact]
        public void Propose_requirement_records_the_source_and_the_open_questions()
        {
            var result = Call("propose_requirement",
                "{\"spec_file\":\"orders.spec.md\",\"title\":\"Fair use of big rooms\",\"statement\":\"When a team books the big room, the system shall limit it.\"," +
                "\"criteria\":[\"Given 2 bookings this week, when a third is requested, then it is rejected.\"],\"source\":\"#5\"," +
                "\"open_questions\":[\"What is a team?\",\"Which rooms are big?\"]}");

            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Contains("2 open question(s)", TextOf(result));
            var doc = SpecParser.Parse("orders.spec.md", File.ReadAllText(Path.Combine(_root, "specs", "orders.spec.md")));
            var added = doc.Requirements.Single(r => r.Id == "ORD-003");
            Assert.Equal("https://github.com/acme/shop/issues/5", added.Source);
            Assert.Equal("When a team books the big room, the system shall limit it.", added.Statement);
            Assert.Empty(doc.Issues);
            Assert.Contains("> - Which rooms are big?", File.ReadAllText(Path.Combine(_root, "specs", "orders.spec.md")));
        }

        [Fact]
        public void Propose_requirement_says_when_a_story_already_has_requirements()
        {
            var result = Call("propose_requirement",
                "{\"spec_file\":\"orders.spec.md\",\"title\":\"Order history\",\"statement\":\"The system shall keep orders.\"," +
                "\"criteria\":[\"Given an order, then it is kept.\"],\"source\":\"https://github.com/acme/shop/issues/3\"}");

            Assert.Contains("The same source is cited by ORD-001.", TextOf(result));
        }

        // ---------- MCP prompts ----------

        [Fact]
        public void Initialize_advertises_prompts()
        {
            var capabilities = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}")["result"]!["capabilities"]!;

            Assert.NotNull(capabilities["prompts"]);
        }

        [Fact]
        public void Prompts_are_listed_with_their_argument()
        {
            var prompts = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/list\"}")["result"]!["prompts"]!.AsArray();

            var story = prompts.Single(p => p!["name"]!.GetValue<string>() == "story_to_spec")!;
            Assert.Equal("story", story["arguments"]![0]!["name"]!.GetValue<string>());
            Assert.True(story["arguments"]![0]!["required"]!.GetValue<bool>());
            Assert.Contains(prompts, p => p!["name"]!.GetValue<string>() == "implement_requirement");
        }

        [Fact]
        public void Story_to_spec_tells_the_agent_to_use_the_tools_and_not_to_invent_rules()
        {
            var result = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/get\",\"params\":{\"name\":\"story_to_spec\",\"arguments\":{\"story\":\"#5\"}}}")["result"]!;
            var text = result["messages"]![0]!["content"]!["text"]!.GetValue<string>();

            Assert.Equal("user", result["messages"]![0]!["role"]!.GetValue<string>());
            Assert.Contains("Turn the user story #5 into a requirement", text);
            Assert.Contains("get_story (#5)", text);
            Assert.Contains("passing source = \"#5\"", text);
            Assert.Contains("Do not invent rules.", text);
        }

        [Theory]
        [InlineData("{\"name\":\"story_to_spec\",\"arguments\":{}}", "Missing argument 'story'.")]
        [InlineData("{\"name\":\"nope\"}", "Unknown prompt: nope")]
        public void Bad_prompt_requests_are_protocol_errors(string @params, string message)
        {
            var error = Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/get\",\"params\":" + @params + "}")["error"]!;

            Assert.Equal(-32602, error["code"]!.GetValue<int>());
            Assert.Equal(message, error["message"]!.GetValue<string>());
        }
    }
}
