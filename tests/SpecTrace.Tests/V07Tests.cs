using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpecTrace.Tool;
using SpecTrace.Tool.Mcp;
using SpecTrace.Tool.Results;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.7: mutation testing per requirement and criterion, from Stryker's mutation-report.json.</summary>
    public class V07Tests : IDisposable
    {
        private const string Spec = @"# Bookings

## BK-001: Create a booking

When an organizer books a room, the system shall accept up to 4 seats.

- AC-1: Given 2 seats, when booked, then it is accepted.
- AC-2: Given 9 seats, when booked, then it is rejected.

## BK-002: Cancel a booking

When the organizer cancels a booking, the system shall mark it done.

- AC-1: Given a booking, when cancelled, then it is done.
";

        // Line numbers matter: Create spans lines 3-8, Cancel lines 10-11, the field is line 13.
        private const string Service = @"public class BookingService
{
    [Implements(Requirements.BK_001)]
    public bool Create(int seats)
    {
        if (seats < 1) return false;
        return seats <= 4;
    }

    [Implements(Requirements.BK_002)]
    public void Cancel() { Done = true; }

    public bool Done;
}
";

        private const string Tests = @"namespace Shop.Tests;

public class BookingTests
{
    [Fact, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_1)] public void Creates() { }
    [Theory, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_2)] public void Rejects_taken(int hour) { }
}

public class CancelTests
{
    [Fact, Verifies(Requirements.BK_002, AcceptanceCriteria.BK_002.AC_1)] public void Cancels() { }
}
";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-v07-" + Guid.NewGuid().ToString("N"));

        public V07Tests()
        {
            Directory.CreateDirectory(_root);
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            Write("specs/bookings.spec.md", Spec);
            Write("src/BookingService.cs", Service);
            Write("tests/BookingTests.cs", Tests);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private void Write(string relative, string text)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private SpecTraceConfig Config => SpecTraceConfig.Load(_root);

        private static JsonObject Mutant(string id, int line, string status, string mutator, string? replacement = null, string[]? killedBy = null) => new()
        {
            ["id"] = id,
            ["mutatorName"] = mutator,
            ["replacement"] = replacement,
            ["status"] = status,
            ["location"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["column"] = 9 },
                ["end"] = new JsonObject { ["line"] = line, ["column"] = 20 },
            },
            ["killedBy"] = killedBy == null ? null : new JsonArray(killedBy.Select(k => (JsonNode)k).ToArray()),
        };

        /// <summary>A report shaped like mutation-testing-report-schema 3.x.</summary>
        /// <param name="everyKiller">Like Stryker with --disable-bail: a mutant lists every test that detects it.</param>
        private static string Report(string serviceKey = "src/BookingService.cs", bool everyKiller = true) => new JsonObject
        {
            ["schemaVersion"] = "2",
            ["thresholds"] = new JsonObject { ["high"] = 80, ["low"] = 60 },
            ["files"] = new JsonObject
            {
                [serviceKey] = new JsonObject
                {
                    ["language"] = "cs",
                    ["source"] = Service,
                    ["mutants"] = new JsonArray(
                        Mutant("1", 6, "Killed", "EqualityOperator", "<=", new[] { "t1" }),
                        Mutant("2", 7, "Survived", "EqualityOperator", "<"),
                        Mutant("3", 6, "NoCoverage", "BooleanLiteral", "true"),
                        Mutant("4", 7, "CompileError", "Statement"),
                        Mutant("5", 7, "Timeout", "Statement"),
                        Mutant("6", 11, "Killed", "BooleanLiteral", "false", new[] { "t3" }),
                        Mutant("7", 13, "Survived", "Initializer"),
                        Mutant("8", 11, "Killed", "Statement", null, everyKiller ? new[] { "t3", "t1" } : new[] { "t3" })),
                },
            },
            ["testFiles"] = new JsonObject
            {
                ["tests/BookingTests.cs"] = new JsonObject
                {
                    ["tests"] = new JsonArray(
                        new JsonObject { ["id"] = "t1", ["name"] = "Shop.Tests.BookingTests.Creates" },
                        new JsonObject { ["id"] = "t2", ["name"] = "Shop.Tests.BookingTests.Rejects_taken(hour: 9)" },
                        new JsonObject { ["id"] = "t3", ["name"] = "Shop.Tests.CancelTests.Cancels" }),
                },
            },
        }.ToJsonString();

        private TraceModel Model(string? report = null)
        {
            Write("tests/StrykerOutput/2026-10-29.12-00-00/reports/mutation-report.json", report ?? Report());
            return TraceScanner.Scan(Config) with { Mutations = MutationReport.Load(Path.Combine(_root, "tests", "StrykerOutput")) };
        }

        [Fact]
        public void The_report_is_read_with_test_names_instead_of_ids()
        {
            var report = MutationReport.Parse(JsonDocument.Parse(Report()).RootElement);

            Assert.Equal(8, report.Mutants.Count);
            Assert.True(report.ListsEveryKiller);
            Assert.Equal(new[] { "Shop.Tests.BookingTests.Creates" }, report.Mutants[0].KilledBy.ToArray());
            Assert.Equal(MutantStatus.NoCoverage, report.Mutants[2].Status);
            Assert.Equal(7, report.Mutants[1].StartLine);
        }

        [Theory]
        [InlineData("src/BookingService.cs", "src/BookingService.cs", true)]
        [InlineData("C:\\work\\shop\\src\\BookingService.cs", "src/BookingService.cs", true)]
        [InlineData("/home/ci/shop/src/BookingService.cs", "src/BookingService.cs", true)]
        [InlineData("BookingService.cs", "src/BookingService.cs", true)]
        [InlineData("src/OtherBookingService.cs", "src/BookingService.cs", false)]
        public void Report_files_match_source_files_by_path(string reportFile, string relative, bool same)
        {
            Assert.Equal(same, MutationReport.SameFile(reportFile, relative));
        }

        [Theory]
        [InlineData("Shop.Tests.BookingTests.Creates", "BookingTests.Creates", true)]
        [InlineData("Shop.Tests.BookingTests.Rejects_taken(hour: 9)", "BookingTests.Rejects_taken", true)]
        [InlineData("Shop.Tests.Outer+BookingTests.Creates", "BookingTests.Creates", true)]
        [InlineData("Shop.Tests.CancelTests.Cancels", "CancelTests", true)]
        [InlineData("Shop.Tests.BookingTests.CreatesMore", "BookingTests.Creates", false)]
        public void Report_tests_match_verifies_marks(string name, string symbol, bool same)
        {
            Assert.Equal(same, MutationReport.SameTest(name, symbol));
        }

        [Fact]
        public void Each_requirement_gets_the_mutants_in_its_code()
        {
            var model = Model();
            var create = model.MutationOf(model.Requirements.Single(t => t.Requirement.Id == "BK-001"))!;
            var cancel = model.MutationOf(model.Requirements.Single(t => t.Requirement.Id == "BK-002"))!;

            // Killed and Timeout are detected; Survived and NoCoverage are not; CompileError does not count; line 13 is no requirement's.
            Assert.Equal(2, create.Detected);
            Assert.Equal(2, create.Undetected);
            Assert.Equal(50, create.Score);
            Assert.Equal(new[] { "3", "2" }, create.Survivors.Select(m => m.Id).ToArray());
            Assert.Equal(new[] { "AC-2" }, create.CriteriaDetectingNothing.ToArray());
            Assert.Equal(100, cancel.Score);
        }

        [Fact]
        public void Absolute_paths_in_the_report_work_too()
        {
            var model = Model(Report(Path.Combine(_root, "src", "BookingService.cs")));

            Assert.Equal(50, model.MutationOf(model.Requirements.Single(t => t.Requirement.Id == "BK-001"))!.Score);
        }

        [Fact]
        public void The_report_shows_what_the_tests_do_not_detect()
        {
            var model = Model();

            var md = Reports.Markdown(model);
            var json = JsonNode.Parse(Reports.Json(model))!;
            var github = Reports.GitHub(model, _root);

            Assert.Contains("- Mutants detected by the tests: **66%** (4/6) in the code of the requirements", md);
            Assert.Contains("| Criteria | Mutants detected |", md);
            Assert.Contains("| 50% (2/4) ⚠️ |", md);
            Assert.Contains("| 100% (2/2) |", md);
            Assert.Contains("## Changes the tests do not detect", md);
            Assert.Contains("[src/BookingService.cs:7](src/BookingService.cs#L7) EqualityOperator: `<` (survived)", md);
            Assert.Contains("[src/BookingService.cs:6](src/BookingService.cs#L6) BooleanLiteral: `true` (no test runs this code)", md);
            Assert.Contains("- The tests of AC-2 detect none of the changes in this code.", md);
            Assert.Equal(50, json["requirements"]![0]!["mutation"]!["score"]!.GetValue<double>());
            Assert.Contains("::warning file=src/BookingService.cs,line=7,title=Mutation Change not detected::BK-001: EqualityOperator", github);
        }

        [Fact]
        public void With_strykers_bail_the_criteria_line_says_what_it_cannot_know()
        {
            var model = Model(Report(everyKiller: false));
            var create = model.MutationOf(model.Requirements.Single(t => t.Requirement.Id == "BK-001"))!;

            Assert.False(create.Exact);
            Assert.Equal("The tests of AC-2 were never the first to fail with these changes. Stryker stops at the first failing test: " +
                         "run it with --disable-bail to know whether they detect any.", MutationAnalysis.SilentCriteriaLine(create));
            Assert.DoesNotContain(new[] { Reports.Markdown(model) }, md => md.Contains("detect none"));
        }

        [Fact]
        public void The_version_has_no_commit_suffix()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", Cli.Version);
            Assert.Equal(0, Cli.Run(new[] { "--version" }));
        }

        [Theory]
        [InlineData("report", "--mutations")]
        [InlineData("lock", "--requirements")]
        [InlineData("report", "--test-result")]
        public void Unknown_options_fail_instead_of_being_ignored(string command, string option)
        {
            Assert.Equal(2, Cli.Run(new[] { command, "--root", _root, option, "x" }));
        }

        [Fact]
        public void Without_a_mutation_report_nothing_changes()
        {
            var md = Reports.Markdown(TraceScanner.Scan(Config));

            Assert.DoesNotContain(new[] { md }, m => m.Contains("Mutants") || m.Contains("do not detect"));
        }

        [Fact]
        public void The_command_line_can_require_a_minimum_score()
        {
            Model();
            var folder = Path.Combine(_root, "tests", "StrykerOutput");
            var output = Path.Combine(_root, "r.md");

            Assert.Equal(1, Cli.Run(new[] { "report", "--root", _root, "--mutation", folder, "--min-mutation-score", "60", "--out", output }));
            Assert.Equal(0, Cli.Run(new[] { "report", "--root", _root, "--mutation", folder, "--min-mutation-score", "40", "--out", output }));
        }

        [Fact]
        public void A_missing_report_says_how_to_produce_it()
        {
            var error = Assert.Throws<ArgumentException>(() => MutationReport.Load(Path.Combine(_root, "nope")));

            Assert.Contains("dotnet stryker --reporter json", error.Message);
        }

        [Fact]
        public void The_agent_sees_the_surviving_mutants_without_being_told_where_the_report_is()
        {
            Model();
            var server = new McpServer(Config);

            var text = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_requirement\",\"arguments\":{\"id\":\"BK-001\"}}}")!
                ["result"]!["content"]![0]!["text"]!.GetValue<string>();

            Assert.Contains("## Changes the tests do not detect", text);
            Assert.Contains("The tests detect 2 of 4 mutants in this requirement's code", text);
            Assert.Contains("- src/BookingService.cs:7 EqualityOperator: `<` (survived)", text);
            Assert.Contains("The tests of AC-2 detect none", text);
        }

        [Fact]
        public void Strengthen_tests_is_offered_as_a_prompt()
        {
            var server = new McpServer(Config);

            var text = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"prompts/get\",\"params\":{\"name\":\"strengthen_tests\",\"arguments\":{\"id\":\"BK-001\"}}}")!
                ["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>();

            Assert.Contains("Strengthen the tests of requirement BK-001", text);
            Assert.Contains("Do not change the production code", text);
        }
    }
}
