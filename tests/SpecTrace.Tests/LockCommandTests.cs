using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Mcp;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.4 en la CLI y el servidor MCP: dotnet spectrace lock, informe y herramientas con cambios en la spec.</summary>
    public class LockCommandTests : IDisposable
    {
        private const string Spec = @"# Pedidos

## ORD-001: Crear pedido
The system shall create orders.

- AC-1: Given 2 lines, then 2 lines.
- AC-2: Given an empty cart, then it is rejected.

## ORD-002: Cancelar pedido
The system shall cancel orders.

- AC-1: Given Pending, then Cancelled.

## ORD-009: Borrador
Status: draft

The system shall maybe do something.
";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-lock-" + Guid.NewGuid().ToString("N"));

        public LockCommandTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "specs"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "tests"));
            File.WriteAllText(Path.Combine(_root, "spectrace.json"), "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            File.WriteAllText(SpecPath, Spec);
            File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"), @"
public class OrderService
{
    [Implements(Requirements.ORD_001)] public void Create() { }
    [Implements(Requirements.ORD_002)] public void Cancel() { }
}");
            File.WriteAllText(Path.Combine(_root, "tests", "OrderTests.cs"), @"
public class OrderTests
{
    [Fact, Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)] public void Two() { }
    [Fact, Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)] public void Empty_rejected() { }
    [Fact, Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)] public void Cancels() { }
}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string SpecPath => Path.Combine(_root, "specs", "orders.spec.md");

        private string LockPath => Path.Combine(_root, SpecLock.FileName);

        private SpecTraceConfig Config => SpecTraceConfig.Load(_root);

        private int Cli(params string[] args) => global::Cli.Run(args.Concat(new[] { "--root", _root }).ToArray());

        private void EditSpec(string from, string to) => File.WriteAllText(SpecPath, File.ReadAllText(SpecPath).Replace(from, to));

        [Fact]
        public void Lock_records_only_active_requirements()
        {
            Assert.Equal(0, Cli("lock"));

            var specLock = SpecLock.Parse(File.ReadAllText(LockPath));
            Assert.Equal(2, specLock.Count);
            Assert.Null(specLock.Find("ORD-009"));
        }

        [Fact]
        public void Without_a_lock_nothing_is_drift_and_check_passes()
        {
            var model = TraceScanner.Scan(Config);

            Assert.False(model.HasLock);
            Assert.DoesNotContain(model.Requirements, t => t.HasDrift);
            Assert.Equal(0, Cli("lock", "--check"));
            Assert.Contains("Sin `spectrace.lock`", Reports.Markdown(model));
        }

        [Fact]
        public void Edited_criterion_is_drift_until_it_is_locked_again()
        {
            Cli("lock");
            EditSpec("then it is rejected.", "then it is rejected with an error.");

            var model = TraceScanner.Scan(Config);
            var ord1 = model.Requirements.Single(t => t.Requirement.Id == "ORD-001");
            Assert.True(ord1.HasDrift);
            Assert.False(ord1.CodeNeedsReview);
            Assert.Equal("AC-2", Assert.Single(ord1.CriteriaNeedingReview).Id);
            Assert.Equal(1, Cli("lock", "--check"));
            Assert.Equal(1, Cli("report", "--fail-on-drift"));
            Assert.Equal(0, Cli("report"));

            var md = Reports.Markdown(model);
            Assert.Contains("- Cambios en la spec sin revisar: **1**", md);
            Assert.Contains("→ revisa `OrderTests.Empty_rejected`", md);
            Assert.Contains("ST0011", Reports.GitHub(model, _root));
            var json = JsonNode.Parse(Reports.Json(model))!;
            var drift = json["requirements"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == "ORD-001")!["drift"]!;
            Assert.Equal("AC-2", Assert.Single(drift["criteriaChanged"]!.AsArray())!.GetValue<string>());
            Assert.True(drift["pending"]!.GetValue<bool>());

            Assert.Equal(0, Cli("lock", "--requirement", "ORD-001"));
            Assert.Equal(0, Cli("lock", "--check"));
        }

        [Fact]
        public void Locking_one_requirement_keeps_the_others_pending()
        {
            Cli("lock");
            EditSpec("The system shall create orders.", "The system shall create orders and send a receipt.");
            EditSpec("The system shall cancel orders.", "The system shall cancel orders and refund them.");

            Cli("lock", "--requirement", "ORD-001");

            var model = TraceScanner.Scan(Config);
            Assert.False(model.Requirements.Single(t => t.Requirement.Id == "ORD-001").HasDrift);
            Assert.True(model.Requirements.Single(t => t.Requirement.Id == "ORD-002").CodeNeedsReview);
        }

        [Fact]
        public void Lock_rejects_unknown_requirements()
        {
            Assert.Equal(2, Cli("lock", "--requirement", "ORD-404"));
            Assert.False(File.Exists(LockPath));
        }

        [Fact]
        public void Mcp_tools_show_spec_changes_and_tell_the_agent_not_to_lock()
        {
            Cli("lock");
            EditSpec("The system shall cancel orders.", "The system shall cancel orders and refund them.");
            var server = new McpServer(Config);

            var list = (JsonObject)server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_requirements\",\"arguments\":{\"only_changed\":true}}}")!["result"]!;
            var item = Assert.Single((JsonArray)list["structuredContent"]!["requirements"]!);
            Assert.Equal("ORD-002", item!["id"]!.GetValue<string>());
            Assert.True(item["spec_changed"]!.GetValue<bool>());

            var context = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_requirement\",\"arguments\":{\"id\":\"ORD-002\"}}}")!
                ["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("## Cambios desde la última revisión", context);
            Assert.Contains("El enunciado cambió: revisa que `OrderService.Cancel`", context);
            Assert.Contains("No lo ejecutes tú sin su visto bueno", context);
        }
    }
}
