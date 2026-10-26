using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SpecTrace.Tests
{
    public class CodeFixTests
    {
        private const string Spec = @"# Pedidos

## ORD-001: Crear pedido
When the customer submits a cart, the system shall create an order.

- AC-1: Given 2 lines, then the order has 2 lines.
- AC-2: Given an empty cart, then it is rejected.

## ORD-002: Cancelar pedido
While an order is Pending, when it is cancelled, the system shall mark it Cancelled.

- AC-1: Given a Pending order, then it is Cancelled.
- AC-2: Given a Shipped order, then it is rejected.

## ORD-003: Exportar XML
Status: deprecated
";

        private const string XunitShim = @"
namespace Xunit { public class FactAttribute : System.Attribute { public string? Skip { get; set; } } }
";

        [Fact]
        public async Task ST0001_offers_the_closest_existing_ids()
        {
            using var ws = new WorkspaceHarness(Spec, @"
using SpecTrace;
public class OrderService { [Implements(""ORD-01"")] public void Create() { } }");
            var doc = ws.Code(ws.AppId);
            var diagnostic = (await WorkspaceHarness.AnalyzeAsync(doc.Project)).Single(d => d.Id == "ST0001");

            var fixes = await WorkspaceHarness.FixesAsync(doc, diagnostic);

            Assert.Equal("Cambiar a 'ORD-001'", fixes.First().Title);
            var fixedText = await WorkspaceHarness.TextAsync(await WorkspaceHarness.ApplyAsync(fixes.First()), doc.Id);
            Assert.Contains("[Implements(\"ORD-001\")]", fixedText);
        }

        [Fact]
        public async Task ST0002_offers_the_valid_criteria()
        {
            using var ws = new WorkspaceHarness(Spec, @"
using SpecTrace;
public class T { [Verifies(""ORD-001"", ""AC-9"")] public void M() { } }");
            var doc = ws.Code(ws.AppId);
            var diagnostic = (await WorkspaceHarness.AnalyzeAsync(doc.Project)).Single(d => d.Id == "ST0002");

            var fixes = await WorkspaceHarness.FixesAsync(doc, diagnostic);

            Assert.Equal(new[] { "Cambiar a 'AC-1'", "Cambiar a 'AC-2'" }, fixes.Select(f => f.Title).ToArray());
            var fixedText = await WorkspaceHarness.TextAsync(await WorkspaceHarness.ApplyAsync(fixes[1]), doc.Id);
            Assert.Contains("[Verifies(\"ORD-001\", \"AC-2\")]", fixedText);
        }

        [Fact]
        public async Task ST0006_removes_the_attribute_and_keeps_the_doc_comment()
        {
            using var ws = new WorkspaceHarness(Spec, @"
using SpecTrace;
public class Legacy
{
    /// <summary>Exporta.</summary>
    [Implements(""ORD-003"")]
    public void Export() { }

    [System.Obsolete, Implements(""ORD-003"")]
    public void Export2() { }
}");
            var doc = ws.Code(ws.AppId);
            var diagnostics = (await WorkspaceHarness.AnalyzeAsync(doc.Project)).Where(d => d.Id == "ST0006")
                .OrderBy(d => d.Location.SourceSpan.Start).ToList();
            Assert.Equal(2, diagnostics.Count);

            var first = await WorkspaceHarness.ApplyAsync((await WorkspaceHarness.FixesAsync(doc, diagnostics[0])).Single());
            var text1 = await WorkspaceHarness.TextAsync(first, doc.Id);
            Assert.Contains("    /// <summary>Exporta.</summary>\n    public void Export() { }", text1.Replace("\r\n", "\n"));

            var second = await WorkspaceHarness.ApplyAsync((await WorkspaceHarness.FixesAsync(doc, diagnostics[1])).Single());
            var text2 = await WorkspaceHarness.TextAsync(second, doc.Id);
            Assert.Contains("[System.Obsolete]", text2);
            Assert.DoesNotContain(new[] { text2 }, t => t.Contains("Implements(\"ORD-003\")]\n    public void Export2"));
        }

        [Fact]
        public async Task Literals_can_be_replaced_by_catalog_constants_adding_the_using()
        {
            using var ws = new WorkspaceHarness(Spec, @"using SpecTrace;

public class OrderTests
{
    [Verifies(""ORD-001"", ""AC-2"")]
    public void Rejects_empty_cart() { }
}");
            var doc = ws.Code(ws.AppId);

            var action = (await WorkspaceHarness.RefactoringsAsync(doc, "\"ORD-001\"")).Single(a => a.Title.StartsWith("Usar constantes"));
            var text = await WorkspaceHarness.TextAsync(await WorkspaceHarness.ApplyAsync(action), doc.Id);

            Assert.Contains("[Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)]", text);
            Assert.Contains("using App.Specs;", text);
            Assert.Contains("using SpecTrace;", text);
        }

        [Fact]
        public async Task No_constant_refactoring_when_already_using_constants()
        {
            using var ws = new WorkspaceHarness(Spec, @"using SpecTrace;
using App.Specs;
public class S { [Implements(Requirements.ORD_001)] public void M() { } }");
            var doc = ws.Code(ws.AppId);

            var actions = await WorkspaceHarness.RefactoringsAsync(doc, "Requirements.ORD_001");

            Assert.DoesNotContain(actions, a => a.Title.StartsWith("Usar constantes"));
        }

        [Fact]
        public async Task Generate_tests_creates_a_compiling_file_with_only_the_missing_criteria()
        {
            using var ws = new WorkspaceHarness(Spec,
                @"using SpecTrace;
using App.Specs;
public class OrderService { [Implements(Requirements.ORD_002)] public void Cancel() { } }",
                @"using SpecTrace;
using App.Tests.Specs;
public class Existing
{
    [Xunit.Fact]
    [Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)]
    public void Cancels_pending() { }
}" + XunitShim);
            var doc = ws.Code(ws.AppId);

            var action = (await WorkspaceHarness.RefactoringsAsync(doc, "Requirements.ORD_002")).Single(a => a.Title.StartsWith("Generar tests"));
            Assert.Equal("Generar tests de ORD-002 en App.Tests (1 criterio)", action.Title);

            var solution = await WorkspaceHarness.ApplyAsync(action);
            var tests = solution.GetProject(ws.TestsId!)!;
            var generated = tests.Documents.Single(d => d.Name == "ORD_002_CancelarPedidoTests.cs");
            var text = (await generated.GetTextAsync()).ToString();

            Assert.Equal(new[] { "Specs" }, generated.Folders.ToArray());
            Assert.Contains("namespace App.Tests.Specs;", text);
            Assert.Contains("AcceptanceCriteria.ORD_002.AC_2", text);
            Assert.DoesNotContain(new[] { text }, t => t.Contains("AcceptanceCriteria.ORD_002.AC_1"));

            var compilation = (await tests.GetCompilationAsync())!;
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            // El test generado lleva Skip: el criterio sigue pendiente (ST0005).
            var after = await WorkspaceHarness.AnalyzeAsync(tests);
            Assert.Contains(after, d => d.Id == "ST0005" && d.GetMessage().Contains("AC-2"));
        }

        [Fact]
        public async Task Generate_tests_is_not_offered_without_a_test_project()
        {
            using var ws = new WorkspaceHarness(Spec, @"using SpecTrace;
public class OrderService { [Implements(""ORD-002"")] public void Cancel() { } }");
            var doc = ws.Code(ws.AppId);

            var actions = await WorkspaceHarness.RefactoringsAsync(doc, "\"ORD-002\"");

            Assert.DoesNotContain(actions, a => a.Title.StartsWith("Generar tests"));
        }
    }
}
