using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SpecTrace.Tests
{
    public class AnalyzerTests
    {
        private const string Spec = @"# Pedidos

## ORD-001: Crear pedido
When the customer submits a cart, the system shall create an order.

- AC-1: Given 2 lines, then the order has 2 lines.
- AC-2: Given an empty cart, then it is rejected.

## ORD-002: Cancelar pedido
Status: approved

While an order is Pending, when it is cancelled, the system shall mark it Cancelled.

- AC-1: Given a Pending order, then it is Cancelled.

## ORD-003: Exportar XML
Status: deprecated

## ORD-004: Notificar
Status: draft

When the state changes, the system shall send an email.
";

        private const string Implementation = @"
using SpecTrace;
using App.Specs;

public class OrderService
{
    [Implements(Requirements.ORD_001)]
    public void Create() { }

    [Implements(""ORD-002"")]
    public void Cancel() { }
}";

        [Fact]
        public void Generated_catalog_compiles_and_contains_all_requirements()
        {
            var result = TestHarness.Run(Implementation, Spec);

            Assert.Empty(result.CompilerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            var catalog = result.GeneratedText("SpecTrace.Requirements.g.cs");
            Assert.Contains("public const string ORD_001 = \"ORD-001\";", catalog);
            Assert.Contains("public const string AC_2 = \"AC-2\";", catalog);
            Assert.Contains("[global::System.Obsolete(", catalog);
            Assert.Contains("Enforced { get; } = new string[] { \"ORD-001\", \"ORD-002\" }", catalog);
        }

        [Fact]
        public void Implemented_requirements_are_emitted_as_assembly_attributes()
        {
            var result = TestHarness.Run(Implementation, Spec);

            var text = result.GeneratedText("SpecTrace.Implemented.g.cs");
            Assert.Contains("SpecTraceImplemented(\"ORD-001\")", text);
            Assert.Contains("SpecTraceImplemented(\"ORD-002\")", text);
        }

        [Fact]
        public void Fully_implemented_project_has_no_coverage_warnings()
        {
            var result = TestHarness.Run(Implementation, Spec);

            Assert.Empty(result.SpecTrace());
        }

        [Fact]
        public void Missing_implementation_reports_ST0003_on_the_spec_line()
        {
            const string source = @"
using SpecTrace;
public class OrderService { [Implements(""ORD-001"")] public void Create() { } }";

            var result = TestHarness.Run(source, Spec);

            var diagnostic = Assert.Single(result.SpecTrace("ST0003"));
            Assert.Contains("ORD-002", diagnostic.GetMessage());
            var span = diagnostic.Location.GetLineSpan();
            Assert.Equal("/repo/specs/test.spec.md", span.Path);
            Assert.Equal(8, span.StartLinePosition.Line);
        }

        [Fact]
        public void Draft_and_deprecated_requirements_are_not_required()
        {
            var result = TestHarness.Run(Implementation, Spec);

            Assert.DoesNotContain(result.SpecTrace("ST0003"), d => d.GetMessage().Contains("ORD-003") || d.GetMessage().Contains("ORD-004"));
        }

        [Fact]
        public void Unknown_requirement_reports_ST0001_at_the_argument()
        {
            const string source = @"
using SpecTrace;
public class X { [Implements(""ORD-999"")] public void M() { } }";

            var result = TestHarness.Run(source, Spec);

            var diagnostic = Assert.Single(result.SpecTrace("ST0001"));
            Assert.Equal("\"ORD-999\"", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }

        [Fact]
        public void Referencing_a_deprecated_requirement_reports_ST0006()
        {
            const string source = Implementation + @"
public class Legacy { [SpecTrace.Implements(""ORD-003"")] public void Export() { } }";

            var result = TestHarness.Run(source, Spec);

            Assert.Single(result.SpecTrace("ST0006"));
        }

        [Fact]
        public void Unknown_criterion_reports_ST0002()
        {
            var impl = TestHarness.Run(Implementation, Spec, assemblyName: "App");
            const string tests = @"
using SpecTrace;
public class T { [Verifies(""ORD-001"", ""AC-9"")] public void M() { } }";

            var result = TestHarness.Run(tests, Spec, isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            var diagnostic = Assert.Single(result.SpecTrace("ST0002"));
            Assert.Contains("AC-1, AC-2", diagnostic.GetMessage());
        }

        [Fact]
        public void Test_project_reports_implemented_but_unverified_requirements()
        {
            var impl = TestHarness.Run(Implementation, Spec, assemblyName: "App");
            const string tests = @"
using SpecTrace;
using App.Tests.Specs;

public class OrderTests
{
    [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)]
    public void Creates() { }
}";

            var result = TestHarness.Run(tests, Spec, isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            var notVerified = Assert.Single(result.SpecTrace("ST0004"));
            Assert.Contains("ORD-002", notVerified.GetMessage());
            var criterion = Assert.Single(result.SpecTrace("ST0005"));
            Assert.Contains("AC-2", criterion.GetMessage());
            Assert.Empty(result.SpecTrace("ST0003"));
        }

        [Fact]
        public void Skipped_tests_do_not_count_as_verification()
        {
            var impl = TestHarness.Run(Implementation, Spec, assemblyName: "App");
            const string tests = @"
using SpecTrace;
using App.Tests.Specs;

[System.AttributeUsage(System.AttributeTargets.Method)]
public class FactAttribute : System.Attribute { public string? Skip { get; set; } }

public class OrderTests
{
    [Fact]
    [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)]
    [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)]
    [Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)]
    public void Real() { }

    [Fact(Skip = ""pendiente"")]
    [Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)]
    public void Pending() { }
}";
            var withSkipOnly = tests.Replace("[Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)]\n    public void Real()", "public void Real()");

            var all = TestHarness.Run(tests, Spec, isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());
            var skipOnly = TestHarness.Run(withSkipOnly, Spec, isTestProject: true, assemblyName: "App.Tests",
                rootNamespace: "App.Tests", impl.ToReference());

            Assert.Empty(all.SpecTrace());
            var diagnostic = Assert.Single(skipOnly.SpecTrace("ST0004"));
            Assert.Contains("ORD-002", diagnostic.GetMessage());
        }

        [Fact]
        public void Duplicate_ids_report_ST0007()
        {
            var spec = Spec + "\n## ORD-001: Otra vez\nThe system shall do it.\n\n- AC-1: x\n";

            var result = TestHarness.Run(Implementation, spec);

            Assert.Single(result.SpecTrace("ST0007"));
        }

        [Fact]
        public void Approved_requirement_without_criteria_reports_ST0008()
        {
            var spec = Spec + "\n## ORD-010: Sin criterios\nThe system shall do something.\n";
            const string source = Implementation + @"
public class Extra { [SpecTrace.Implements(""ORD-010"")] public void M() { } }";

            var result = TestHarness.Run(source, spec);

            var diagnostic = Assert.Single(result.SpecTrace("ST0008"));
            Assert.Contains("ORD-010", diagnostic.GetMessage());
        }

        [Fact]
        public void Nothing_happens_without_specs_reference()
        {
            var result = TestHarness.Run("public class Plain { }", "# Sin requisitos\n");

            Assert.Empty(result.SpecTrace());
            Assert.Empty(result.Generated);
        }
    }
}
