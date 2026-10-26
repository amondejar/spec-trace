using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Mcp;
using Xunit;

namespace SpecTrace.Tests
{
    public class McpServerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-mcp-" + Guid.NewGuid().ToString("N"));
        private readonly McpServer _server;

        public McpServerTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "specs"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "tests", "App.Tests"));
            File.WriteAllText(Path.Combine(_root, "spectrace.json"), "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            File.WriteAllText(Path.Combine(_root, "tests", "App.Tests", "App.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(_root, "src", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Shop.App</RootNamespace></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(_root, "src", "Order.cs"), "namespace Shop.App;\npublic sealed class Order { public decimal Total => 0m; }\n");
            File.WriteAllText(Path.Combine(_root, "src", "appsettings.Secret.json"), "{ \"ApiKey\": \"no-debe-leerse\" }");
            Directory.CreateDirectory(Path.Combine(_root, "src", "obj"));
            File.WriteAllText(Path.Combine(_root, "src", "obj", "Generated.cs"), "class Generated { }");
            File.WriteAllText(Path.Combine(_root, "specs", "orders.spec.md"), @"# Orders

## ORD-001: Create café order
The system shall create orders.

- AC-1: Given 2 lines, then 2 lines.

## ORD-002: Cancel order
The system shall cancel orders.

- AC-1: Given Pending, then Cancelled.
- AC-2: Given Shipped, then rejected.
");
            File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"), @"
public class OrderService
{
    [Implements(Requirements.ORD_001)] public void Create() { }
}");
            File.WriteAllText(Path.Combine(_root, "tests", "App.Tests", "OrderTests.cs"), @"
public class OrderTests
{
    [Fact, Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)] public void Creates() { }
}");
            _server = new McpServer(SpecTraceConfig.Load(_root));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private JsonObject Send(string json) => _server.Handle(json) ?? throw new InvalidOperationException("Sin respuesta");

        private JsonObject Call(string tool, string arguments) =>
            (JsonObject)Send($"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}")["result"]!;

        private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

        [Theory]
        [InlineData("2025-11-25", "2025-11-25")]
        [InlineData("2024-11-05", "2024-11-05")]
        [InlineData("2099-01-01", "2025-11-25")]
        public void Initialize_negotiates_the_protocol_version(string requested, string expected)
        {
            var response = Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"protocolVersion\":\"{requested}\",\"capabilities\":{{}},\"clientInfo\":{{\"name\":\"t\",\"version\":\"1\"}}}}}}");

            Assert.Equal(expected, response["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("spectrace", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Equal(1, response["id"]!.GetValue<int>());
        }

        [Fact]
        public void Notifications_get_no_response_and_unknown_methods_fail()
        {
            Assert.Null(_server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));

            var discover = Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"server/discover\"}");
            Assert.Equal(-32601, discover["error"]!["code"]!.GetValue<int>());

            var parse = Send("{ no es json");
            Assert.Equal(-32700, parse["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public void Tools_list_marks_read_only_tools()
        {
            var tools = (JsonArray)Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}")["result"]!["tools"]!;

            var readOnly = tools.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            Assert.Equal(6, readOnly.Count);
            Assert.True(readOnly["get_requirement"]);
            Assert.True(readOnly["get_code"]);
            Assert.False(readOnly["propose_requirement"]);
            Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        }

        [Fact]
        public void List_requirements_returns_structured_gaps()
        {
            var result = Call("list_requirements", "{\"only_gaps\":true}");

            Assert.False(result["isError"]!.GetValue<bool>());
            var items = (JsonArray)result["structuredContent"]!["requirements"]!;
            var gap = Assert.Single(items);
            Assert.Equal("ORD-002", gap!["id"]!.GetValue<string>());
            Assert.False(gap["implemented"]!.GetValue<bool>());
        }

        [Fact]
        public void Get_requirement_returns_context_or_a_tool_error()
        {
            Assert.Contains("NO TEST", TextOf(Call("get_requirement", "{\"id\":\"ORD-002\"}")));

            var missing = Call("get_requirement", "{\"id\":\"ORD-999\"}");
            Assert.True(missing["isError"]!.GetValue<bool>());
        }

        [Fact]
        public void Get_requirement_says_where_the_code_goes_and_what_to_read()
        {
            var text = TextOf(Call("get_requirement", "{\"id\":\"ORD-002\"}"));

            // Implementation: the .csproj RootNamespace + ".Specs". Tests: the project name + ".Specs".
            Assert.Contains("Implementation in `App` (src): `using SpecTrace;` and `using Shop.App.Specs;`", text);
            Assert.Contains("Tests in `App.Tests` (tests/App.Tests): `using SpecTrace;` and `using App.Tests.Specs;`", text);
            // ORD-002 has no code yet: the files of ORD-001, from the same spec, are offered.
            Assert.Contains("- src/OrderService.cs — implements ORD-001", text);
            Assert.Contains("- tests/App.Tests/OrderTests.cs — verifies ORD-001", text);
        }

        [Fact]
        public void Get_code_by_requirement_falls_back_to_the_same_spec()
        {
            var result = Call("get_code", "{\"requirement_id\":\"ORD-002\"}");

            Assert.False(result["isError"]!.GetValue<bool>());
            var text = TextOf(result);
            Assert.Contains("ORD-002 has no code yet", text);
            Assert.Contains("### src/OrderService.cs", text);
            Assert.Contains("[Implements(Requirements.ORD_001)]", text);
            Assert.Contains("### tests/App.Tests/OrderTests.cs", text);
        }

        [Fact]
        public void Get_code_by_type_finds_the_declaring_file()
        {
            var text = TextOf(Call("get_code", "{\"type\":\"Order\"}"));

            Assert.Contains("### src/Order.cs", text);
            Assert.Contains("public decimal Total => 0m;", text);
            Assert.DoesNotContain(new[] { text }, t => t.Contains("### src/OrderService.cs"));

            Assert.True(Call("get_code", "{\"type\":\"Nope\"}")["isError"]!.GetValue<bool>());
        }

        [Theory]
        [InlineData("{\"path\":\"src/Order.cs\"}", false)]
        [InlineData("{\"path\":\"specs/orders.spec.md\"}", false)]
        [InlineData("{\"path\":\"src/appsettings.Secret.json\"}", true)]
        [InlineData("{\"path\":\"spectrace.json\"}", true)]
        [InlineData("{\"path\":\"src/obj/Generated.cs\"}", true)]
        [InlineData("{\"path\":\"../../etc/passwd\"}", true)]
        [InlineData("{\"path\":\"src/Order.cs\",\"type\":\"Order\"}", true)]
        [InlineData("{}", true)]
        public void Get_code_only_reads_code_and_specs(string arguments, bool isError)
        {
            Assert.Equal(isError, Call("get_code", arguments)["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Output_keeps_accents_unescaped()
        {
            var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_requirement\",\"arguments\":{\"id\":\"ORD-001\"}}}\n");
            var output = new StringWriter();

            await _server.RunAsync(input, output);

            Assert.Contains("Create café order", output.ToString());
            Assert.DoesNotContain(new[] { output.ToString() }, o => o.Contains("\\u00E9"));
        }

        [Fact]
        public void Unknown_tool_is_a_protocol_error()
        {
            var response = Send("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"rm_rf\",\"arguments\":{}}}");

            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public void Scaffold_tests_writes_inside_the_repo_only()
        {
            var ok = Call("scaffold_tests", "{\"test_project\":\"tests/App.Tests\",\"requirement_id\":\"ORD-002\"}");
            Assert.False(ok["isError"]!.GetValue<bool>());
            Assert.True(File.Exists(Path.Combine(_root, "tests", "App.Tests", "Specs", "ORD_002_CancelOrderTests.cs")));

            var escape = Call("scaffold_tests", "{\"test_project\":\"../../\"}");
            Assert.True(escape["isError"]!.GetValue<bool>());
        }

        [Fact]
        public void Propose_requirement_appends_a_proposed_requirement_with_the_next_id()
        {
            var result = Call("propose_requirement",
                "{\"spec_file\":\"orders.spec.md\",\"title\":\"Apply coupon\",\"statement\":\"When a coupon is applied, the system shall reduce the total.\",\"criteria\":[\"Given 100 EUR and 10%, then 90 EUR.\"]}");

            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Contains("ORD-003", TextOf(result));
            var doc = SpecParser.Parse("orders.spec.md", File.ReadAllText(Path.Combine(_root, "specs", "orders.spec.md")));
            var added = doc.Requirements.Single(r => r.Id == "ORD-003");
            Assert.Equal(SpecStatus.Proposed, added.Status);
            Assert.Equal("When a coupon is applied, the system shall reduce the total.", added.Statement);
            Assert.Single(added.Criteria);
            Assert.Empty(doc.Issues);
        }

        [Fact]
        public void Propose_requirement_rejects_paths_outside_specs_and_duplicate_ids()
        {
            var outside = Call("propose_requirement", "{\"spec_file\":\"../evil.spec.md\",\"title\":\"x\",\"statement\":\"y\",\"criteria\":[\"z\"]}");
            Assert.True(outside["isError"]!.GetValue<bool>());

            var duplicate = Call("propose_requirement", "{\"spec_file\":\"orders.spec.md\",\"id\":\"ORD-001\",\"title\":\"x\",\"statement\":\"y\",\"criteria\":[\"z\"]}");
            Assert.True(duplicate["isError"]!.GetValue<bool>());
            Assert.Contains("already exists", TextOf(duplicate));
        }

        [Fact]
        public async Task RunAsync_speaks_one_json_message_per_line()
        {
            var input = new StringReader(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}\n" +
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n");
            var output = new StringWriter();

            await _server.RunAsync(input, output);

            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.NotNull(JsonNode.Parse(l)));
        }
    }
}
