using System;
using System.IO;
using System.Linq;
using SpecTrace.Specs;
using SpecTrace.Tool;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.9: architecture as a verifiable spec (kind: architecture, ARC rules, ST0015).</summary>
    public class V09Tests : IDisposable
    {
        private const string Spec = @"# Bookings

## BK-001: Create a booking
When an organizer books a free room, the system shall create the booking.

- AC-1: Given a free room, when it is booked, then the booking is created.
";

        private const string Architecture = @"---
spec: ARC
title: Architecture
kind: architecture
---
# Architecture

Minimal API. Business rules live in the domain; endpoints only translate HTTP.

## ARC-001: The domain does not depend on the web
In: App.Domain
Must not use: `Microsoft.AspNetCore`, App.Endpoints

The domain is tested without HTTP.

## ARC-002: Time is injected
Status: approved
In: App.Domain
Must not use: System.DateTime.Now, System.DateTime.UtcNow, System.DateTimeOffset.Now
Use instead: TimeProvider

## ARC-003: Endpoints go through the services
Status: proposed
In: App.Endpoints
Must not use: App.Infrastructure
";

        private const string Clean = @"
using System;
using SpecTrace;
using App.Specs;

namespace App.Domain
{
    public class BookingService
    {
        private readonly TimeProvider _time;
        public BookingService(TimeProvider time) => _time = time;

        [Implements(Requirements.BK_001)]
        public DateTime Create() => _time.GetLocalNow().DateTime;
    }
}

namespace App.Endpoints
{
    public static class BookingEndpoints
    {
        public static DateTime Now() => DateTime.Now;
    }
}
";

        // ---------- the spec ----------

        [Fact]
        public void Architecture_rules_are_read_and_are_not_requirements()
        {
            var doc = ArchitectureParser.Parse("specs/architecture.spec.md", Architecture);

            Assert.True(ArchitectureParser.IsArchitecture(Architecture));
            Assert.Empty(SpecParser.Parse("specs/architecture.spec.md", Architecture).Requirements);
            Assert.Empty(doc.Issues);
            Assert.Equal(new[] { "ARC-001", "ARC-002", "ARC-003" }, doc.Rules.Select(r => r.Id).ToArray());

            var web = doc.Rules[0];
            Assert.Equal(SpecStatus.Approved, web.Status);
            Assert.Equal(new[] { "App.Domain" }, web.Scope.ToArray());
            Assert.Equal(new[] { "Microsoft.AspNetCore", "App.Endpoints" }, web.Forbidden.ToArray());
            Assert.Equal("The domain is tested without HTTP.", web.Rationale);
            Assert.Equal("TimeProvider", doc.Rules[1].UseInstead);
            Assert.False(doc.Rules[2].IsEnforced);
            Assert.Equal("Minimal API. Business rules live in the domain; endpoints only translate HTTP.", doc.Principles);
        }

        [Fact]
        public void Names_are_matched_by_whole_segments()
        {
            var rule = ArchitectureParser.Parse("a.spec.md", Architecture).Rules[0];

            Assert.True(rule.AppliesTo("App.Domain"));
            Assert.True(rule.AppliesTo("App.Domain.Rooms"));
            Assert.False(rule.AppliesTo("App.DomainEvents"));
            Assert.Equal("Microsoft.AspNetCore", rule.Forbids("Microsoft.AspNetCore.Http.HttpContext"));
            Assert.Null(rule.Forbids("Microsoft.AspNetCoreX"));
            Assert.Equal("System.Collections.Generic.List", ArchitectureNames.Normalize("`global::System.Collections.Generic.List<T>`"));
        }

        [Fact]
        public void A_rule_without_scope_or_forbidden_names_is_a_format_issue()
        {
            var doc = ArchitectureParser.Parse("a.spec.md", "---\nkind: architecture\n---\n## ARC-001: Something\nIn: App.Domain\n\n## ARC-001: Again\nIn: X\nMust not use: Y\n## ARC-002: Odd\nStatus: maybe\nIn: X\nMust not use: Y\n");

            Assert.Contains(doc.Issues, i => i.Kind == SpecIssueKind.MissingStatement && i.Message.Contains("ARC-001"));
            Assert.Contains(doc.Issues, i => i.Kind == SpecIssueKind.UnknownStatus);
            var both = ArchitectureSpec.Build(new[] { doc, ArchitectureParser.Parse("b.spec.md", "---\nkind: architecture\n---\n## ARC-002: Twice\nIn: X\nMust not use: Y\n") });
            Assert.Contains(both.Issues, i => i.Kind == SpecIssueKind.DuplicateRequirement && i.Message.Contains("ARC-002"));
        }

        // ---------- the analyzer (ST0015) ----------

        [Fact]
        public void Code_that_follows_the_rules_has_no_warnings()
        {
            var result = TestHarness.RunWithArchitecture(Clean, Spec, Architecture);

            Assert.Empty(result.CompilerDiagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
            Assert.Empty(result.SpecTrace("ST0015"));
            Assert.Empty(result.SpecTrace("ST0003"));
        }

        [Fact]
        public void A_forbidden_member_is_reported_once_per_expression_with_the_alternative()
        {
            var source = Clean.Replace("public DateTime Create() => _time.GetLocalNow().DateTime;",
                "public DateTime Create() => DateTime.Now.Date;\n        public DateTime Later() => System.DateTime.UtcNow;");

            var found = TestHarness.RunWithArchitecture(source, Spec, Architecture).SpecTrace("ST0015").ToList();

            Assert.Equal(2, found.Count);
            Assert.Contains("ARC-002 (Time is injected): code in App.Domain must not use System.DateTime.Now. Use instead: TimeProvider", found[0].GetMessage());
            Assert.Contains("System.DateTime.UtcNow", found[1].GetMessage());
            Assert.Equal("DateTime.Now.Date", found[0].Location.SourceTree!.ToString().Substring(found[0].Location.SourceSpan.Start, found[0].Location.SourceSpan.Length));
        }

        [Fact]
        public void A_forbidden_namespace_is_reported_on_the_using_and_on_the_types_from_it()
        {
            var source = @"
using App.Endpoints;
using SpecTrace;
using App.Specs;

namespace App.Endpoints { public class Route { } }

namespace App.Domain
{
    public class BookingService
    {
        [Implements(Requirements.BK_001)]
        public object Create() => new Route();
    }
}
";
            var found = TestHarness.RunWithArchitecture(source, Spec, Architecture).SpecTrace("ST0015").ToList();

            // The using at the top is outside App.Domain (the file has two namespaces): only the use inside counts.
            var use = Assert.Single(found);
            Assert.Contains("must not use App.Endpoints.Route (App.Endpoints)", use.GetMessage());
        }

        [Fact]
        public void The_using_of_a_file_with_one_namespace_belongs_to_it()
        {
            var source = @"
using SpecTrace;
using App.Specs;

namespace App.Domain;

public class BookingService
{
    [Implements(Requirements.BK_001)]
    public void Create() { }
}
";
            var found = TestHarness.RunWithArchitecture(source, Spec,
                Architecture.Replace("Must not use: `Microsoft.AspNetCore`, App.Endpoints", "Must not use: App.Specs")).SpecTrace("ST0015").ToList();

            Assert.Equal(new[] { 2, 8 }, found.Select(d => d.Location.GetLineSpan().StartLinePosition.Line).OrderBy(l => l).ToArray());
            Assert.All(found, d => Assert.Contains("ARC-001", d.GetMessage()));
        }

        [Fact]
        public void Proposed_rules_and_other_namespaces_are_not_checked()
        {
            var source = Clean.Replace("public static DateTime Now() => DateTime.Now;",
                "public static DateTime Now() => DateTime.Now;\n        public static object Store() => new App.Infrastructure.Store();") +
                "\nnamespace App.Infrastructure { public class Store { } }\n";

            var found = TestHarness.RunWithArchitecture(source, Spec, Architecture).SpecTrace("ST0015");

            // ARC-003 is proposed, and DateTime.Now is only forbidden in App.Domain.
            Assert.Empty(found);
        }

        [Fact]
        public void Rule_format_issues_are_reported_as_ST0008_in_the_architecture_file()
        {
            var result = TestHarness.RunWithArchitecture(Clean, Spec, Architecture + "\n## ARC-004: Incomplete\nIn: App\n");

            var issue = Assert.Single(result.SpecTrace("ST0008"));
            Assert.Contains("ARC-004", issue.GetMessage());
            Assert.EndsWith("architecture.spec.md", issue.Location.GetLineSpan().Path);
        }

        // ---------- the tool ----------

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-v09-" + Guid.NewGuid().ToString("N"));

        public V09Tests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Write(string relative, string text)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private TraceModel Scan()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\"] }");
            Write("specs/bookings.spec.md", "---\nspec: BK\n---\n" + Spec);
            Write("specs/architecture.spec.md", Architecture);
            Write("src/BookingService.cs", "public class BookingService { [Implements(Requirements.BK_001)] public void Create() { } }");
            return TraceScanner.Scan(SpecTraceConfig.Load(_root));
        }

        [Fact]
        public void The_scanner_keeps_architecture_apart_from_requirements()
        {
            var model = Scan();

            Assert.Equal(new[] { "BK-001" }, model.Requirements.Select(r => r.Requirement.Id).ToArray());
            Assert.Equal(3, model.Architecture.Rules.Count);
            Assert.DoesNotContain(model.Requirements, r => r.HasGap && r.Requirement.Id.StartsWith("ARC", StringComparison.Ordinal));
        }

        [Fact]
        public void Agents_get_the_architecture_with_the_requirement_and_on_its_own()
        {
            Scan();
            var tools = new SpecTrace.Tool.Mcp.SpecTraceTools(SpecTraceConfig.Load(_root));

            var arch = Text(tools.Call("get_architecture", new System.Text.Json.Nodes.JsonObject()));
            var req = Text(tools.Call("get_requirement", new System.Text.Json.Nodes.JsonObject { ["id"] = "BK-001" }));

            Assert.Contains("Business rules live in the domain", arch);
            Assert.Contains("**ARC-002: Time is injected** (approved). Code in App.Domain must not use System.DateTime.Now", arch);
            Assert.Contains("Use instead: TimeProvider", arch);
            Assert.Contains("ARC-003: Endpoints go through the services** (proposed)", arch);
            Assert.Contains("## Architecture", req);
            Assert.Contains("ARC-001", req);
            Assert.Contains("get_architecture", SpecTrace.Tool.Mcp.SpecTracePrompts.Text("implement_requirement", "BK-001"));
            Assert.Contains("get_architecture", SpecTrace.Tool.Mcp.SpecTracePrompts.Text("review_spec", "BK-001"));
            Assert.Equal(0, Cli.Run(new[] { "architecture", "--root", _root }));
            Assert.Equal(0, Cli.Run(new[] { "architecture", "--format", "json", "--root", _root }));
        }

        [Fact]
        public void Without_an_architecture_spec_agents_are_told_so()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\"] }");
            Write("specs/bookings.spec.md", "---\nspec: BK\n---\n" + Spec);

            var model = TraceScanner.Scan(SpecTraceConfig.Load(_root));

            Assert.True(model.Architecture.IsEmpty);
            Assert.Contains("No architecture spec", model.Architecture.Markdown());
        }

        [Fact]
        public void Init_architecture_creates_a_proposed_starting_point_once()
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\"] }");
            Write("src/Shop.Api/Shop.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

            Assert.Equal(0, Cli.Run(new[] { "init", "--architecture", "--root", _root }));
            var text = File.ReadAllText(Path.Combine(_root, "specs", "architecture.spec.md"));
            File.WriteAllText(Path.Combine(_root, "specs", "architecture.spec.md"), "kept");
            Cli.Run(new[] { "init", "--architecture", "--root", _root });

            var doc = ArchitectureParser.Parse("architecture.spec.md", text);
            Assert.True(ArchitectureParser.IsArchitecture(text));
            Assert.Empty(doc.Issues);
            Assert.All(doc.Rules, r => Assert.Equal(SpecStatus.Proposed, r.Status));
            Assert.Contains(doc.Rules, r => r.Scope.Contains("Shop.Domain"));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(_root, "specs", "architecture.spec.md")));
        }

        private static string Text(System.Text.Json.Nodes.JsonObject result) =>
            result["content"]![0]!["text"]!.GetValue<string>();
    }
}
