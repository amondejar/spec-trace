using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Mcp;
using SpecTrace.Tool.Results;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.6: test results per criterion, requirements touched by a change, spec quality and init.</summary>
    public class V06Tests : IDisposable
    {
        private const string Spec = @"---
spec: BK
---
# Bookings

## BK-001: Create a booking
Status: approved

When an organizer books a free room, the system shall create the booking.

- AC-1: Given a free room, when it is booked, then the booking is created.
- AC-2: Given a taken room, when it is booked, then the request is rejected.
- AC-3: Given an unknown room, when it is booked, then the response is 404.

## BK-002: Cancel a booking
Status: approved

When the organizer cancels a booking, the system shall mark it cancelled.

- AC-1: Given a booking, when its organizer cancels it, then it is cancelled.
";

        private const string Tests = @"namespace Shop.Tests;

public class BookingTests
{
    [Fact, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_1)]
    public void Creates() { }

    [Theory, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_2)]
    public void Rejects_taken(int hour) { }

    [Fact, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_3)]
    public void Unknown_room() { }
}

[Verifies(Requirements.BK_002, AcceptanceCriteria.BK_002.AC_1)]
public class CancelTests
{
    [Fact] public void Cancels() { }
    [Fact] public void Cancels_twice() { }
}
";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-v06-" + Guid.NewGuid().ToString("N"));

        public V06Tests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Write(string relative, string text)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative)).Replace("\r\n", "\n");

        private SpecTraceConfig Config => SpecTraceConfig.Load(_root);

        // ---------- Test results (.trx) ----------

        /// <summary>A .trx as dotnet test writes it: definitions with the method, results with the outcome.</summary>
        internal static string Trx(params (string ClassName, string Name, string Outcome, string? Message)[] results)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var ids = results.Select(_ => Guid.NewGuid().ToString()).ToList();
            var doc = new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "Results", results.Select((r, i) => new XElement(ns + "UnitTestResult",
                    new XAttribute("testId", ids[i]), new XAttribute("testName", r.ClassName + "." + r.Name), new XAttribute("outcome", r.Outcome),
                    r.Message == null ? null : new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", r.Message)))))),
                new XElement(ns + "TestDefinitions", results.Select((r, i) => new XElement(ns + "UnitTest",
                    new XAttribute("id", ids[i]), new XAttribute("name", r.Name),
                    new XElement(ns + "TestMethod", new XAttribute("className", r.ClassName), new XAttribute("name", r.Name)))))));
            return doc.ToString();
        }

        private void Project(string trx)
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"tests\"] }");
            Write("specs/bookings.spec.md", Spec);
            Write("tests/BookingTests.cs", Tests);
            Write("TestResults/run.trx", trx);
        }

        private static string StandardTrx => Trx(
            ("Shop.Tests.BookingTests", "Creates", "Passed", null),
            ("Shop.Tests.BookingTests", "Rejects_taken(hour: 9)", "Passed", null),
            ("Shop.Tests.BookingTests", "Rejects_taken(hour: 10)", "Failed", "Assert.Equal() Failure: Expected 409, Actual 201\nat line 12"),
            ("Shop.Tests.CancelTests", "Cancels", "Passed", null),
            ("Shop.Tests.CancelTests", "Cancels_twice", "Passed", null));

        [Fact]
        public void Trx_results_are_read_per_method_and_theory_rows_combine()
        {
            var results = TestResults.Load(new[] { WriteTrx(StandardTrx) });

            Assert.Equal(TestOutcome.Passed, results.OutcomeOf("BookingTests.Creates"));
            Assert.Equal(TestOutcome.Failed, results.OutcomeOf("BookingTests.Rejects_taken"));
            Assert.Equal(2, results.Find("BookingTests.Rejects_taken")!.Runs);
            Assert.Equal(TestOutcome.NotRun, results.OutcomeOf("BookingTests.Unknown_room"));
            Assert.Equal(TestOutcome.Passed, results.OutcomeOf("CancelTests"));
            Assert.StartsWith("Assert.Equal() Failure", results.Find("BookingTests.Rejects_taken")!.Message);
        }

        private string WriteTrx(string trx)
        {
            Write("results/a.trx", trx);
            return Path.Combine(_root, "results", "a.trx");
        }

        [Fact]
        public void Results_from_several_files_combine_and_a_failure_wins()
        {
            Write("TestResults/one/a.trx", Trx(("Shop.Tests.BookingTests", "Creates", "Passed", null)));
            Write("TestResults/two/b.trx", Trx(("Shop.Tests.BookingTests", "Creates", "Failed", "boom")));

            var results = TestResults.Load(new[] { Path.Combine(_root, "TestResults") });

            Assert.Equal(2, results.Files.Count);
            Assert.Equal(TestOutcome.Failed, results.OutcomeOf("BookingTests.Creates"));
        }

        [Theory]
        [InlineData("Skipped", "NotExecuted")]
        [InlineData("Timeout", "Timeout")]
        public void Other_outcomes_map_to_not_run_or_failed(string _, string outcome)
        {
            var results = TestResults.Load(new[] { WriteTrx(Trx(("Shop.Tests.BookingTests", "Creates", outcome, null))) });

            Assert.Equal(outcome == "Timeout" ? TestOutcome.Failed : TestOutcome.NotRun, results.OutcomeOf("BookingTests.Creates"));
        }

        [Fact]
        public void Missing_results_explain_how_to_produce_them()
        {
            var error = Assert.Throws<ArgumentException>(() => TestResults.Load(new[] { Path.Combine(_root, "nope") }));

            Assert.Contains("dotnet test --logger trx", error.Message);
        }

        [Fact]
        public void Criteria_get_the_outcome_of_their_tests()
        {
            Project(StandardTrx);
            var model = TraceScanner.Scan(Config) with { TestResults = TestResults.Load(new[] { Path.Combine(_root, "TestResults") }) };
            var create = model.Requirements.Single(t => t.Requirement.Id == "BK-001");
            var cancel = model.Requirements.Single(t => t.Requirement.Id == "BK-002");

            Assert.Equal(TestOutcome.Passed, model.OutcomeOf(create, "AC-1"));
            Assert.Equal(TestOutcome.Failed, model.OutcomeOf(create, "AC-2"));
            Assert.Equal(TestOutcome.NotRun, model.OutcomeOf(create, "AC-3"));
            Assert.Equal(TestOutcome.Passed, model.OutcomeOf(cancel, "AC-1"));
        }

        [Fact]
        public void The_report_shows_which_criteria_pass_and_which_fail()
        {
            Project(StandardTrx);
            var model = TraceScanner.Scan(Config) with { TestResults = TestResults.Load(new[] { Path.Combine(_root, "TestResults") }) };

            var md = Reports.Markdown(model);
            var json = JsonNode.Parse(Reports.Json(model))!;
            var github = Reports.GitHub(model, _root);

            Assert.Contains("- Acceptance criteria passing: **2/4** · ❌ 1 failing · ⚪ 1 not run", md);
            Assert.Contains("`BookingTests.Rejects_taken` (tests/BookingTests.cs:8) (AC-2) ❌", md);
            Assert.Contains("| 3/3 · ❌ 1 |", md);
            Assert.Contains("| 1/1 ✅ |", md);
            Assert.Contains("## Failing criteria", md);
            Assert.Contains("\"Given a taken room, when it is booked, then the request is rejected.\": `BookingTests.Rejects_taken` (tests/BookingTests.cs:8) failed — Assert.Equal() Failure: Expected 409, Actual 201", md);
            Assert.Equal("failed", json["requirements"]![0]!["criteria"]![1]!["result"]!.GetValue<string>());
            Assert.Equal(1, json["testResults"]!["failing"]!.GetValue<int>());
            Assert.Contains("::error file=specs/bookings.spec.md,line=12,title=Tests Criterion failing::BK-001 AC-2", github);
        }

        [Fact]
        public void Report_can_fail_on_failing_criteria()
        {
            Project(StandardTrx);
            var results = Path.Combine(_root, "TestResults");

            Assert.Equal(0, Cli.Run(new[] { "report", "--root", _root, "--test-results", results, "--out", Path.Combine(_root, "r.md") }));
            Assert.Equal(1, Cli.Run(new[] { "report", "--root", _root, "--test-results", results, "--fail-on-failing", "--out", Path.Combine(_root, "r.md") }));
        }

        [Fact]
        public void Without_results_nothing_changes_in_the_report()
        {
            Project(StandardTrx);
            var md = Reports.Markdown(TraceScanner.Scan(Config));

            Assert.DoesNotContain(new[] { md }, m => m.Contains("passing") || m.Contains("✅") || m.Contains("Failing criteria"));
        }

        // ---------- Requirements touched by a change ----------

        private string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args)) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private const string Service = @"public class BookingService
{
    [Implements(Requirements.BK_001)]
    public void Create()
    {
        var x = 1;
    }

    [Implements(Requirements.BK_002)]
    public void Cancel()
    {
        var y = 2;
    }
}
";

        private void StartRepo()
        {
            Git("init", "-q", "-b", "main");
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            Write("specs/bookings.spec.md", Spec + "\n## BK-003: Export\nStatus: approved\n\nThe system shall export bookings.\n\n- AC-1: Given bookings, when exported, then a file is produced.\n");
            Write("src/BookingService.cs", Service);
            Write("src/Store.cs", "public class Store\n{\n    public int Count;\n}\n");
            Write("tests/BookingTests.cs", Tests);
            Git("add", "-A");
            Git("commit", "-q", "-m", "base");
            Git("checkout", "-q", "-b", "feature");
        }

        [Fact]
        public void A_change_lists_the_requirements_it_touches()
        {
            StartRepo();
            // Spec: BK-001 AC-2 reworded, BK-003 removed, BK-004 new. Code: only Cancel's body. Tests: Unknown_room. Untraced: Store.
            Write("specs/bookings.spec.md", Spec.Replace("then the request is rejected.", "then the request is rejected with 409.") +
                "\n## BK-004: Notify\nStatus: proposed\n\nWhen a booking is created, the system shall notify the organizer.\n\n- AC-1: Given a booking, when created, then an email is sent.\n");
            Write("src/BookingService.cs", Service.Replace("var y = 2;", "var y = 3;"));
            Write("src/Store.cs", "public class Store\n{\n    public int Count = 1;\n}\n");
            Write("tests/BookingTests.cs", Tests.Replace("public void Unknown_room() { }", "public void Unknown_room() { var z = 0; }"));

            var impact = Impact.Compute(Config, TraceScanner.Scan(Config), "main");

            var ids = impact.Touched.Select(i => i.Requirement.Id).ToList();
            Assert.Equal(new[] { "BK-001", "BK-002", "BK-004" }, ids.ToArray());
            var create = impact.Touched.Single(i => i.Requirement.Id == "BK-001");
            Assert.Equal(new[] { "AC-2 changed" }, create.SpecChanges.ToArray());
            Assert.Empty(create.Code);
            Assert.Equal("BookingTests.Unknown_room", create.Tests.Single().Symbol);
            var cancel = impact.Touched.Single(i => i.Requirement.Id == "BK-002");
            Assert.Empty(cancel.SpecChanges);
            Assert.Equal("BookingService.Cancel", cancel.Code.Single().Symbol);
            Assert.Equal(new[] { "new" }, impact.Touched.Single(i => i.Requirement.Id == "BK-004").SpecChanges.ToArray());
            Assert.Equal("BK-003", impact.Removed.Single().Id);
            Assert.Equal(new[] { "src/Store.cs" }, impact.UntracedFiles.ToArray());

            var md = Impact.Markdown(impact);
            Assert.Contains("Compared with `main`: **3** requirement(s) touched, **1** removed.", md);
            Assert.Contains("| BK-002 Cancel a booking | – | `BookingService.Cancel` | – | – |", md);
            Assert.Contains("Removed from the specs: BK-003 Export.", md);
            Assert.Contains("`src/Store.cs`", md);
        }

        [Fact]
        public void Status_and_new_criteria_count_as_spec_changes()
        {
            var before = SpecParser.Parse("x.spec.md", "## A-1: One\nStatus: proposed\n\nThe system shall one.\n\n- AC-1: Given x, then y.\n- AC-2: Given z, then w.\n").Requirements[0];
            var after = SpecParser.Parse("x.spec.md", "## A-1: One\nStatus: approved\n\nThe system shall one.\n\n- AC-1: Given x, then y.\n- AC-3: Given q, then r.\n").Requirements[0];

            Assert.Equal(new[] { "status proposed → approved", "AC-3 added", "AC-2 removed" }, Impact.SpecChanges(before, after).ToArray());
        }

        [Fact]
        public void The_report_starts_with_the_impact_when_given_a_base()
        {
            StartRepo();
            Write("src/BookingService.cs", Service.Replace("var x = 1;", "var x = 5;"));

            Cli.Run(new[] { "report", "--root", _root, "--base", "main", "--out", Path.Combine(_root, "r.md") });

            var md = Read("r.md");
            Assert.True(md.IndexOf("## Requirements touched by this change", StringComparison.Ordinal) < md.IndexOf("## All requirements", StringComparison.Ordinal));
            Assert.Contains("| BK-001 Create a booking | – | `BookingService.Create` | – | – |", md);
        }

        // ---------- Spec quality ----------

        [Fact]
        public void Spec_quality_finds_what_cannot_be_tested_as_written()
        {
            const string bad = @"## Q-1: Search
Status: approved

The search should be fast and easy to use, etc.

- AC-1: Given 1000 rooms, when searching, then results appear quickly.
- AC-2: The search works with filters.

## Q-2: Buscar
Status: proposed

Cuando el usuario busca, el sistema deberá responder rápidamente.

- AC-1: Dado un texto, cuando se busca, entonces se muestran resultados.

## Q-3: Good
Status: approved

When a client searches by name, the system shall return the rooms whose name contains the text.

- AC-1: Given rooms Atlas and Boreal, when searching ""at"", then only Atlas is returned.

## Q-4: Old
Status: deprecated

It should be fast.
";
            var issues = SpecLint.Check(SpecParser.Parse("q.spec.md", bad).Requirements);

            Assert.Contains(issues, i => i.RequirementId == "Q-1" && i.Kind == SpecQualityKind.NotEars);
            Assert.Equal(new[] { "fast", "easy", "etc" }, issues.Where(i => i.RequirementId == "Q-1" && i.CriterionId == null && i.Kind == SpecQualityKind.VagueTerm).Select(i => i.Term).ToArray());
            Assert.Contains(issues, i => i.RequirementId == "Q-1" && i.CriterionId == "AC-1" && i.Term == "quickly");
            Assert.Contains(issues, i => i.RequirementId == "Q-1" && i.CriterionId == "AC-2" && i.Kind == SpecQualityKind.NoOutcome);
            Assert.Equal(new[] { "rápidamente" }, issues.Where(i => i.RequirementId == "Q-2").Select(i => i.Term).ToArray());
            Assert.DoesNotContain(issues, i => i.RequirementId == "Q-3" || i.RequirementId == "Q-4");
        }

        [Fact]
        public void The_build_reports_vague_terms_on_the_spec_line()
        {
            const string spec = "# S\n\n## Q-1: Search\n\nWhen a client searches, the system shall answer fast.\n\n- AC-1: Given rooms, when searching, then results appear.\n";

            var result = TestHarness.Run("public class Search { [SpecTrace.Implements(\"Q-1\")] public void Run() { } }", spec);

            var d = Assert.Single(result.SpecTrace("ST0013"));
            Assert.Equal("Warning", d.Severity.ToString());
            Assert.Equal(2, d.Location.GetLineSpan().StartLinePosition.Line); // the heading "## Q-1: Search" (zero-based)
            Assert.Contains("'fast'", d.GetMessage());
        }

        [Fact]
        public void The_agent_sees_spec_quality_in_the_requirement_context()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [] }");
            Write("specs/s.spec.md", "## Q-1: Search\n\nWhen a client searches, the system shall answer fast.\n\n- AC-1: Given rooms, when searching, then results appear.\n");

            var context = Reports.Context(TraceScanner.Scan(Config), "Q-1");

            Assert.Contains("## Spec quality", context);
            Assert.Contains("'fast' in the statement of 'Q-1'", context);
            Assert.Contains("ask a person instead of choosing one", context);
        }

        [Fact]
        public void Review_spec_is_offered_as_a_prompt()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [] }");
            var server = new McpServer(Config);

            var prompts = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/list\"}")!["result"]!["prompts"]!.AsArray();
            var text = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"prompts/get\",\"params\":{\"name\":\"review_spec\",\"arguments\":{\"target\":\"BK-008\"}}}")!
                ["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>();

            Assert.Contains(prompts, p => p!["name"]!.GetValue<string>() == "review_spec");
            Assert.Contains("Review BK-008 before it is approved. Do not change any file", text);
            Assert.Contains("Contradictions", text);
        }

        // ---------- init ----------

        [Fact]
        public void Init_in_a_subfolder_completes_the_existing_root()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\"] }");
            Write("specs/bookings.spec.md", Spec);
            Directory.CreateDirectory(Path.Combine(_root, "src", "Api"));
            var log = new StringWriter();

            var root = Cli.InitRoot(Path.Combine(_root, "src", "Api"), null, here: false, log);

            Assert.Equal(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar));
            Assert.Contains("already configures this folder", log.ToString());
            Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src", "Api")), Cli.InitRoot(Path.Combine(_root, "src", "Api"), null, here: true, log));

            Cli.Run(new[] { "init", "--root", _root });
            Assert.False(File.Exists(Path.Combine(_root, "specs", "example.spec.md")));
            Assert.False(File.Exists(Path.Combine(_root, "src", "Api", "spectrace.json")));
        }

        [Fact]
        public void Init_in_a_git_subfolder_without_config_uses_the_repository_root()
        {
            Git("init", "-q", "-b", "main");
            Directory.CreateDirectory(Path.Combine(_root, "src", "Api"));

            var root = Cli.InitRoot(Path.Combine(_root, "src", "Api"), null, here: false, TextWriter.Null);

            Assert.Equal(Path.GetFullPath(new DirectoryInfo(_root).FullName).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
        }
    }
}
