using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SpecTrace.Tool;
using SpecTrace.Tool.Automation;
using SpecTrace.Tool.Mcp;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.8: the work queue (pending), prompts for CI agents and the agent workflow (init --agent).</summary>
    public class V08Tests : IDisposable
    {
        private const string Header = "---\nspec: BK\ntitle: Bookings\n---\n# Bookings\n";

        private const string Create = "\n## BK-001: Create a booking\nStatus: approved\n\nWhen an organizer books a free room, the system shall create the booking.\n\n- AC-1: Given a free room, when it is booked, then the booking is created.\n";

        private static string Move(string status, bool secondCriterion = false) =>
            $"\n## BK-002: Move a booking\nStatus: {status}\n\nWhen the organizer moves a booking, the system shall keep it with the new time.\n\n" +
            "- AC-1: Given a booking at 10:00, when it is moved to 12:00, then it starts at 12:00.\n" +
            (secondCriterion ? "- AC-2: Given a booking, when it is moved onto another booking, then it is rejected.\n" : "");

        private const string CreateCode = "public class Create\n{\n    [Implements(Requirements.BK_001)] public void Run() { }\n}\n";
        private const string CreateTests = "public class CreateTests\n{\n    [Fact, Verifies(Requirements.BK_001, AcceptanceCriteria.BK_001.AC_1)] public void Creates() { }\n}\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-v08-" + Guid.NewGuid().ToString("N"));

        public V08Tests() => Directory.CreateDirectory(_root);

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

        // ---------- pending ----------

        [Fact]
        public void Pending_lists_approved_requirements_with_what_they_miss()
        {
            Setup(Header + Create + Move("approved"));
            Write("src/Move.cs", "public class Move\n{\n    [Implements(Requirements.BK_002)] public void Run() { }\n}\n");

            var work = Queue();

            var move = Assert.Single(work);
            Assert.Equal("BK-002", move.Requirement.Id);
            Assert.Equal(new[] { "tests" }, move.Missing.ToArray());
        }

        [Fact]
        public void Pending_ignores_proposed_requirements()
        {
            Setup(Header + Create + Move("proposed"));

            Assert.Empty(Queue());
        }

        [Fact]
        public void Pending_names_the_criteria_without_tests()
        {
            Setup(Header + Create + Move("approved", secondCriterion: true));
            Write("src/Move.cs", "public class Move\n{\n    [Implements(Requirements.BK_002)] public void Run() { }\n}\n");
            Write("tests/MoveTests.cs", "public class MoveTests\n{\n    [Fact, Verifies(Requirements.BK_002, AcceptanceCriteria.BK_002.AC_1)] public void Moves() { }\n}\n");

            var move = Assert.Single(Queue());

            Assert.Equal(new[] { "tests for AC-2" }, move.Missing.ToArray());
        }

        [Fact]
        public void Pending_since_a_commit_returns_only_what_that_change_approved()
        {
            Git("init", "-q", "-b", "main");
            // BK-003 was approved long ago and still waits: it is not new work for this merge.
            Setup(Header + Create + Move("proposed") + Old);
            Git("add", "-A");
            Git("commit", "-q", "-m", "start");
            var before = Git("rev-parse", "HEAD").Trim();

            Write("specs/bookings.spec.md", Header + Create + Move("approved") + Old);
            Git("commit", "-q", "-am", "approve BK-002");

            var all = Queue();
            var merged = Queue(since: before);

            Assert.Equal(new[] { "BK-002", "BK-003" }, all.Select(w => w.Requirement.Id).ToArray());
            var move = Assert.Single(merged);
            Assert.Equal("BK-002", move.Requirement.Id);
            Assert.Equal(new[] { "status proposed → approved" }, move.Changes.ToArray());
        }

        [Fact]
        public void A_criterion_added_to_an_implemented_requirement_is_new_work()
        {
            Git("init", "-q", "-b", "main");
            Setup(Header + Create + Move("approved"));
            Write("src/Move.cs", "public class Move\n{\n    [Implements(Requirements.BK_002)] public void Run() { }\n}\n");
            Write("tests/MoveTests.cs", "public class MoveTests\n{\n    [Fact, Verifies(Requirements.BK_002, AcceptanceCriteria.BK_002.AC_1)] public void Moves() { }\n}\n");
            Git("add", "-A");
            Git("commit", "-q", "-m", "start");
            var before = Git("rev-parse", "HEAD").Trim();

            Write("specs/bookings.spec.md", Header + Create + Move("approved", secondCriterion: true));
            Git("commit", "-q", "-am", "BK-002 AC-2");

            var move = Assert.Single(Queue(since: before));
            Assert.Equal(new[] { "tests for AC-2" }, move.Missing.ToArray());
            Assert.Equal(new[] { "AC-2 added" }, move.Changes.ToArray());
        }

        [Fact]
        public void Pending_for_one_requirement_and_an_unknown_one()
        {
            Setup(Header + Create + Move("approved"));

            Assert.Equal("BK-002", Assert.Single(Queue(only: "bk-002")).Requirement.Id);
            Assert.Empty(Queue(only: "BK-001"));
            var error = Assert.Throws<ArgumentException>(() => Queue(only: "BK-02"));
            Assert.Contains("Did you mean BK-002", error.Message);
        }

        [Fact]
        public void Pending_ids_format_is_one_per_line_and_the_command_checks_its_options()
        {
            Setup(Header + Create + Move("approved"));

            Assert.Equal("BK-002", WorkQueue.Ids(Queue()));
            Assert.Equal(0, Cli.Run(new[] { "pending", "--format", "ids", "--root", _root }));
            Assert.Equal(2, Cli.Run(new[] { "pending", "--sinse", "HEAD", "--root", _root }));
            Assert.Equal(2, Cli.Run(new[] { "pending", "--format", "xml", "--root", _root }));
        }

        // ---------- prompt ----------

        [Fact]
        public void The_prompt_command_prints_the_same_text_as_the_mcp_prompt()
        {
            var text = SpecTracePrompts.Text("implement_requirement", " BK-002 ");
            var mcp = SpecTracePrompts.Get("implement_requirement", new System.Text.Json.Nodes.JsonObject { ["id"] = "BK-002" });

            Assert.Equal(mcp["messages"]![0]!["content"]!["text"]!.GetValue<string>(), text);
            Assert.Contains("Implement requirement BK-002", text);
            Assert.Contains("Do not run dotnet spectrace lock", text);
            Assert.Throws<ArgumentException>(() => SpecTracePrompts.Text("implement", "BK-002"));
            Assert.Throws<ArgumentException>(() => SpecTracePrompts.Text("implement_requirement", " "));
            Assert.Equal(4, SpecTracePrompts.Catalog.Count());
        }

        // ---------- init --agent ----------

        [Fact]
        public void Init_agent_writes_the_workflow_for_the_default_branch_and_specs_folder()
        {
            Git("init", "-q", "-b", "trunk");
            Write("spectrace.json", "{ \"specs\": \"docs/specs\", \"sources\": [\"src\"] }");

            Assert.Equal(0, Cli.Run(new[] { "init", "--agent", "--root", _root }));

            var yml = File.ReadAllText(Path.Combine(_root, ".github", "workflows", AgentWorkflow.FileName));
            Assert.False(yml.Replace("${{", "").Contains("{{"));
            Assert.Contains("branches: [trunk]", yml);
            Assert.Contains("paths: [\"docs/specs/**\"]", yml);
            Assert.Contains("gh workflow run spectrace-implement.yml", yml);
            Assert.Contains("working-directory: .", yml);
            Assert.Contains("claude_code_oauth_token: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}", yml);
            Assert.Contains("--disallowedTools \"Bash(dotnet spectrace lock:*)\"", yml);
            Assert.Contains("Bash(git push -u origin ${{ steps.req.outputs.branch }})", yml);
            Assert.Contains("dotnet spectrace prompt implement_requirement \"$id\"", yml);
            // v0.8.1: the agent works on the spec pull request when it gets the label.
            Assert.Contains("types: [labeled]", yml);
            Assert.Contains("github.event.label.name == 'spectrace:implement'", yml);
            Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", yml);
            Assert.Contains("pending --since \"origin/$BASE_REF\"", yml);
            Assert.Contains("Bash(git push origin HEAD:${{ steps.req.outputs.branch }})", yml);
            Assert.Contains("gh pr edit \"$PR\" --remove-label \"spectrace:implement\"", yml);
            // Without --agent nothing is written.
            File.Delete(Path.Combine(_root, ".github", "workflows", AgentWorkflow.FileName));
            Cli.Run(new[] { "init", "--root", _root });
            Assert.False(File.Exists(Path.Combine(_root, ".github", "workflows", AgentWorkflow.FileName)));
        }

        [Fact]
        public void Init_agent_does_not_overwrite_an_existing_workflow()
        {
            Write(".github/workflows/" + AgentWorkflow.FileName, "name: mine\n");

            Assert.Equal(0, Cli.Run(new[] { "init", "--agent", "--root", _root }));

            Assert.Equal("name: mine\n", File.ReadAllText(Path.Combine(_root, ".github", "workflows", AgentWorkflow.FileName)));
        }

        [Fact]
        public void The_workflow_of_a_root_below_the_repository_works_from_there()
        {
            var yml = AgentWorkflow.Render("{{branch}} {{specs}} {{workdir}} {{workflow}}", "specs", "main", "samples/Orders/");

            Assert.Equal("main samples/Orders/specs samples/Orders spectrace-implement.yml", yml);
        }

        [Fact]
        public void Without_origin_the_default_branch_is_the_local_main_or_master()
        {
            Assert.Equal("main", AgentWorkflow.DefaultBranch(_root));
            Git("init", "-q", "-b", "master");
            Write("a.txt", "a");
            Git("add", "-A");
            Git("commit", "-q", "-m", "a");

            Assert.Equal("master", AgentWorkflow.DefaultBranch(_root));
        }

        // ---------- helpers ----------

        private const string Old = "\n## BK-003: Cancel a booking\nStatus: approved\n\nWhen the organizer cancels a booking, the system shall free the room.\n\n- AC-1: Given a booking, when it is cancelled, then the room is free.\n";

        private void Setup(string spec)
        {
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            Write("specs/bookings.spec.md", spec);
            Write("src/Create.cs", CreateCode);
            Write("tests/CreateTests.cs", CreateTests);
        }

        private System.Collections.Generic.IReadOnlyList<PendingWork> Queue(string? since = null, string? only = null)
        {
            var config = SpecTraceConfig.Load(_root);
            return WorkQueue.Compute(config, TraceScanner.Scan(config), since, only == null ? null : new[] { only });
        }

        private string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args)) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private void Write(string relative, string text)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }
}
