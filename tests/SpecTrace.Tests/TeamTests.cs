using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Authoring;
using SpecTrace.Tool.Mcp;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.5.1: several people (and agents) on the same repository: merge-safe spectrace.lock and ID collisions.</summary>
    public class TeamTests : IDisposable
    {
        private const string Header = "---\nspec: BK\ntitle: Bookings\n---\n# Bookings\n";

        private const string Create = "\n## BK-001: Create a booking\nStatus: approved\n\nWhen an organizer books a free room, the system shall create the booking.\n\n- AC-1: Given a free room, when it is booked, then the booking is created.\n";
        private const string Move = "\n## BK-002: Move a booking\nStatus: approved\n\nWhen the organizer moves a booking, the system shall keep it with the new time.\n\n- AC-1: Given a booking at 10:00, when it is moved to 12:00, then it starts at 12:00.\n";
        private const string Photos = "\n## BK-002: Room photos\nStatus: approved\n\nWhen a client requests a room, the system shall include its photo.\n\n- AC-1: Given a room with a photo, when it is requested, then the photo URL is returned.\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-team-" + Guid.NewGuid().ToString("N"));

        public TeamTests() => Directory.CreateDirectory(_root);

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

        private static SpecRequirement Req(string block) => SpecParser.Parse("x.spec.md", block).Requirements.Single();

        private static string LockLine(string block) =>
            new SpecLock(new[] { SpecFingerprint.Entry(Req(block)) }).Render().Split('\n').First(l => l.StartsWith("BK-", StringComparison.Ordinal));

        // ---------- spectrace.lock after a merge ----------

        [Fact]
        public void A_requirement_with_one_line_per_branch_uses_the_line_that_matches_the_spec()
        {
            var edited = Move.Replace("12:00, then it starts at 12:00", "12:00, then it starts at 12:00 and keeps its id");
            var specLock = SpecLock.Parse(LockLine(Move) + "\n" + LockLine(Create) + "\n" + LockLine(edited) + "\n");

            Assert.Equal(new[] { "BK-002" }, specLock.Duplicates.ToArray());
            Assert.False(SpecDrift.Compare(Req(Move), specLock.FindFor(Req(Move))).Any);
            Assert.False(SpecDrift.Compare(Req(edited), specLock.FindFor(Req(edited))).Any);
        }

        [Fact]
        public void Identical_lines_from_both_branches_are_one_review()
        {
            var specLock = SpecLock.Parse(LockLine(Move) + "\n" + LockLine(Move) + "\n");

            Assert.Empty(specLock.Duplicates);
            Assert.Equal(1, specLock.Count);
        }

        [Fact]
        public void Conflict_markers_are_detected_and_both_sides_are_still_read()
        {
            var edited = Move.Replace("new time", "new time and the same id");
            var text = "<<<<<<< HEAD\n" + LockLine(Move) + "\n=======\n" + LockLine(edited) + "\n>>>>>>> main\n";

            var specLock = SpecLock.Parse(text);

            Assert.Equal(new[] { 0, 2, 4 }, specLock.ConflictMarkerLines.ToArray());
            Assert.False(SpecDrift.Compare(Req(edited), specLock.FindFor(Req(edited))).Any);
        }

        [Fact]
        public void Resolving_keeps_the_review_that_matches_and_writes_one_line_per_requirement()
        {
            var edited = Move.Replace("new time", "new time and the same id");
            var specLock = SpecLock.Parse(LockLine(edited) + "\n" + LockLine(Move) + "\n" + LockLine(Create) + "\n");

            var tidy = SpecLock.Parse(specLock.Resolve(new[] { Req(edited), Req(Create) }).Render());

            Assert.Empty(tidy.Duplicates);
            Assert.Equal(2, tidy.Count);
            Assert.False(SpecDrift.Compare(Req(edited), tidy.Find("BK-002")).Any);
        }

        [Fact]
        public void The_build_reports_conflict_markers_in_the_lock()
        {
            var lockText = "<<<<<<< HEAD\n" + LockLine(Create) + "\n=======\n" + LockLine(Create) + "\n>>>>>>> main\n";

            var result = TestHarness.RunWithLock("public class Plain { [SpecTrace.Implements(\"BK-001\")] public void M() { } }", Header + Create, lockText);

            var d = Assert.Single(result.SpecTrace("ST0008"));
            Assert.Contains("conflict markers", d.GetMessage());
            Assert.Contains("lock --tidy", d.GetMessage());
            Assert.Empty(result.SpecTrace("ST0010"));
        }

        [Fact]
        public void Lock_tidy_removes_duplicates_and_markers_without_reviewing_anything()
        {
            Directory.CreateDirectory(Path.Combine(_root, "specs"));
            File.WriteAllText(Path.Combine(_root, "spectrace.json"), "{ \"specs\": \"specs\", \"sources\": [] }");
            var edited = Move.Replace("new time", "new time and the same id");
            File.WriteAllText(Path.Combine(_root, "specs", "bookings.spec.md"), Header + Create + edited);
            File.WriteAllText(Path.Combine(_root, "spectrace.lock"),
                "<<<<<<< HEAD\n" + LockLine(Move) + "\n=======\n" + LockLine(edited) + "\n>>>>>>> main\n" + LockLine(Create) + "\n");

            Assert.Equal(1, Cli.Run(new[] { "lock", "--check", "--root", _root }));
            Assert.Equal(0, Cli.Run(new[] { "lock", "--tidy", "--root", _root }));

            var text = File.ReadAllText(Path.Combine(_root, "spectrace.lock"));
            Assert.DoesNotContain(new[] { text }, t => t.Contains("<<<<<<<") || t.Contains("======="));
            Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("BK-", StringComparison.Ordinal)));
            Assert.Equal(0, Cli.Run(new[] { "lock", "--check", "--root", _root }));
        }

        [Fact]
        public void Init_marks_the_lock_for_union_merges_once()
        {
            File.WriteAllText(Path.Combine(_root, ".gitattributes"), "* text=auto");

            Cli.Run(new[] { "init", "--root", _root });
            Cli.Run(new[] { "init", "--root", _root });

            Assert.Equal("* text=auto\nspectrace.lock merge=union\n", File.ReadAllText(Path.Combine(_root, ".gitattributes")).Replace("\r\n", "\n"));
        }

        // ---------- IDs used twice by two branches ----------

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

        private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative)).Replace("\r\n", "\n");

        private SpecTraceConfig Config => SpecTraceConfig.Load(_root);

        /// <summary>main with BK-001; "feature" branches off it.</summary>
        private void Start()
        {
            Git("init", "-q", "-b", "main");
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"] }");
            Write(".gitattributes", "spectrace.lock merge=union\n");
            Write("specs/bookings.spec.md", Header + Create);
            Write("src/Create.cs", "public class Create\n{\n    [Implements(Requirements.BK_001)] public void Run() { }\n}\n");
            Write("spectrace.lock", "# lock\n" + LockLine(Create) + "\n");
            Git("add", "-A");
            Git("commit", "-q", "-m", "BK-001");
            Git("branch", "feature");
        }

        /// <summary>main gets BK-002 "Move a booking", with code and its review.</summary>
        private void MainAddsMove()
        {
            Git("checkout", "-q", "main");
            Write("specs/bookings.spec.md", Header + Create + Move);
            Write("src/Move.cs", "public class Move\n{\n    [Implements(Requirements.BK_002)] public void Run() { }\n}\n");
            Write("spectrace.lock", "# lock\n" + LockLine(Create) + "\n" + LockLine(Move) + "\n");
            Git("add", "-A");
            Git("commit", "-q", "-m", "BK-002 move");
        }

        /// <summary>feature gets BK-002 "Room photos", with code, a test and its review.</summary>
        private void FeatureAddsPhotos()
        {
            Git("checkout", "-q", "feature");
            Write("specs/bookings.spec.md", Header + Create + Photos);
            Write("src/Photos.cs", "public class Photos\n{\n    [Implements(Requirements.BK_002)] public void Run() { }\n    [Implements(\"BK-002\")] public void Legacy() { }\n}\n");
            Write("tests/PhotosTests.cs", "public class PhotosTests\n{\n    [Fact, Verifies(Requirements.BK_002, AcceptanceCriteria.BK_002.AC_1)] public void Returns_photo() { }\n}\n");
            Write("spectrace.lock", "# lock\n" + LockLine(Create) + "\n" + LockLine(Photos) + "\n");
            Git("add", "-A");
            Git("commit", "-q", "-m", "BK-002 photos");
        }

        [Fact]
        public void After_merging_main_the_branch_renumbers_only_its_own_requirement()
        {
            Start();
            MainAddsMove();
            FeatureAddsPhotos();

            Git("merge", "-q", "main", "-m", "merge main");
            // Both branches appended to the same spec: a real conflict, resolved by keeping both requirements.
            Write("specs/bookings.spec.md", Header + Create + Move + Photos);
            Git("add", "-A");
            Git("commit", "-q", "-m", "merge main");
            // The union driver merged the two reviews without a conflict.
            Assert.DoesNotContain(new[] { Read("spectrace.lock") }, t => t.Contains("<<<<<<<"));
            Assert.Equal(3, Read("spectrace.lock").Split('\n').Count(l => l.StartsWith("BK-", StringComparison.Ordinal)));

            var plan = IdFixer.Plan(Config, "main");

            var collision = Assert.Single(plan.Collisions);
            Assert.Equal("Room photos", collision.Occurrence.Title);
            Assert.Equal("BK-003", collision.NewId);

            IdFixer.Apply(Config, plan);

            var spec = Read("specs/bookings.spec.md");
            Assert.Contains("## BK-002: Move a booking", spec);
            Assert.Contains("## BK-003: Room photos", spec);
            Assert.Contains("[Implements(Requirements.BK_002)]", Read("src/Move.cs"));
            Assert.Contains("[Implements(Requirements.BK_003)] public void Run()", Read("src/Photos.cs"));
            Assert.Contains("[Implements(\"BK-003\")] public void Legacy()", Read("src/Photos.cs"));
            Assert.Contains("Verifies(Requirements.BK_003, AcceptanceCriteria.BK_003.AC_1)", Read("tests/PhotosTests.cs"));

            var catalog = SpecCatalog.Build(new[] { SpecParser.Parse("specs/bookings.spec.md", spec) });
            Assert.Empty(catalog.Issues);
            var specLock = SpecLock.Parse(Read("spectrace.lock"));
            Assert.Empty(specLock.Duplicates);
            Assert.All(catalog.Requirements, r => Assert.False(SpecDrift.Compare(r, specLock.FindFor(r)).Any));
            Assert.False(IdFixer.Plan(Config, "main").HasWork);
        }

        [Fact]
        public void Before_merging_the_branch_learns_that_main_already_used_its_id()
        {
            Start();
            MainAddsMove();
            FeatureAddsPhotos();

            var plan = IdFixer.Plan(Config, "main");

            var collision = Assert.Single(plan.Collisions);
            Assert.Equal("BK-003", collision.NewId);
            Assert.Contains("\"Move a booking\" on main", collision.Reason);

            IdFixer.Apply(Config, plan);
            Assert.Contains("## BK-003: Room photos", Read("specs/bookings.spec.md"));
            Assert.Contains("Requirements.BK_003", Read("src/Photos.cs"));
        }

        [Fact]
        public void Editing_an_existing_requirement_is_not_a_collision()
        {
            Start();
            MainAddsMove();
            Git("checkout", "-q", "-b", "edit", "main");
            Write("specs/bookings.spec.md", Header + Create + Move.Replace("Move a booking", "Move a booking to another time"));

            Assert.False(IdFixer.Plan(Config, "main").HasWork);
        }

        [Fact]
        public void Proposed_requirements_skip_ids_already_used_on_main()
        {
            Start();
            MainAddsMove();
            Git("checkout", "-q", "feature");
            var server = new McpServer(Config);

            var result = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"propose_requirement\",\"arguments\":" +
                "{\"spec_file\":\"bookings.spec.md\",\"title\":\"Room photos\",\"statement\":\"When a client requests a room, the system shall include its photo.\"," +
                "\"criteria\":[\"Given a room with a photo, when it is requested, then the photo URL is returned.\"]}}}")!;

            Assert.Contains("Added BK-003", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
    }
}
