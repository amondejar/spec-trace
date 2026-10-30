using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpecTrace.Specs;
using SpecTrace.Tool;
using SpecTrace.Tool.Flow;
using SpecTrace.Tool.Stories;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.10: the story flow (next, start, approve, submit, accept) on real git repositories.</summary>
    public class V10Tests : IDisposable
    {
        private const string Spec = @"---
spec: CLI
title: Clientes
---
# Clientes

## CLI-001: Alta de un cliente
Status: proposed
Source: #2

> Proposed by an agent on 2026-10-30. Review it and change the status to approved to enforce it.
> Open questions:
> - ¿Se valida la letra del DNI?

When an employee registers a customer, the system shall store it.

- AC-1: Given valid data, when the customer is registered, then the response is 201.

## CLI-002: Email obligatorio
Status: proposed
Source: #2

If the email is missing, then the system shall reject the registration.

- AC-1: Given no email, when registering, then the response is 400.
";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "spectrace-v10-" + Guid.NewGuid().ToString("N"));
        private string Work => Path.Combine(_root, "work");
        private string Origin => Path.Combine(_root, "origin.git");
        private readonly FakeGh _gh = new();
        private readonly StringWriter _out = new();

        public V10Tests()
        {
            Directory.CreateDirectory(Work);
            Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
            Git(Work, "init", "-q", "-b", "main");
            Identity(Work);
            Write("spectrace.json", "{ \"specs\": \"specs\", \"sources\": [\"src\", \"tests\"], \"trackers\": { \"github\": { \"repository\": \"o/r\" } } }");
            Write("specs/README.md", "Specs");
            Git(Work, "add", "-A");
            Git(Work, "commit", "-q", "-m", "start");
            Git(Work, "remote", "add", "origin", Origin);
            Git(Work, "push", "-q", "-u", "origin", "main");
        }

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

        private FlowContext Ctx() => new(SpecTraceConfig.Load(Work), _out, new FakeTracker(), new GitHubCli(Work, _gh.Run));

        private NextStep Next() => StoryFlow.Next(Ctx());

        [Fact]
        public void A_story_goes_from_start_to_merge_with_one_command_per_step()
        {
            // 1. Outside a story.
            Assert.Equal(1, Next().Step);

            // Start: the story's branch, named after the issue.
            Assert.Equal(0, StoryFlow.Start(Ctx(), "#2"));
            Assert.Equal("spec/gh-2-alta-de-clientes", Git(Work, "branch", "--show-current").Trim());
            var spec = Next();
            Assert.Equal(2, spec.Step);
            Assert.Contains("/mcp__spectrace__story_to_spec #2", spec.Commands);

            // 2 → 3. The agent proposed two requirements; one has an open question.
            Write("specs/clientes.spec.md", Spec);
            var questions = Next();
            Assert.Equal(3, questions.Step);
            Assert.Contains("1 open question", questions.Do);
            Assert.Equal(1, StoryFlow.Approve(Ctx(), null, force: false));
            Assert.Contains("¿Se valida la letra del DNI?", _out.ToString());
            Assert.Equal(Spec, Read("specs/clientes.spec.md"));

            // 3. The PO answered: the question becomes a criterion. Approve takes the whole story.
            Write("specs/clientes.spec.md", Spec
                .Replace("> Open questions:\n> - ¿Se valida la letra del DNI?\n", "")
                .Replace("- AC-1: Given valid data, when the customer is registered, then the response is 201.",
                    "- AC-1: Given valid data, when the customer is registered, then the response is 201.\n- AC-2: Given the DNI 12345678A with a wrong letter, when registering, then the response is 400."));
            Assert.Equal(3, Next().Step);
            Assert.Equal(0, StoryFlow.Approve(Ctx(), null, force: false));
            var approved = Read("specs/clientes.spec.md");
            Assert.DoesNotContain("Status: proposed", approved);
            Assert.DoesNotContain("Proposed by an agent", approved);
            Assert.Equal(2, approved.Split("Status: approved").Length - 1);

            // 4. Submit: commit, push, pull request and label.
            var send = Next();
            Assert.Equal(4, send.Step);
            Assert.Contains("dotnet spectrace submit", send.Commands);
            Assert.Equal(0, StoryFlow.Submit(Ctx()));
            var pr = Assert.Single(_gh.Prs);
            Assert.Equal("spec/gh-2-alta-de-clientes", pr.Branch);
            Assert.Equal("main", pr.Base);
            Assert.Contains("Closes #2", pr.Body);
            Assert.Contains("spectrace:implement", pr.Labels);
            Assert.Contains("specs approved (CLI-001, CLI-002)", Git(Work, "log", "-1", "--format=%s"));
            Assert.Equal("", Git(Work, "status", "--porcelain").Trim());
            Assert.Equal("The agent is working", Next().Title);

            // The agent pushes code and tests to the same branch from its own clone.
            var agent = Path.Combine(_root, "agent");
            Git(_root, "clone", "-q", "-b", "spec/gh-2-alta-de-clientes", Origin, agent);
            Identity(agent);
            File.WriteAllText(Path.Combine(agent, "src", "Clientes.cs").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
                "public class Clientes\n{\n    [Implements(Requirements.CLI_001)] public void Alta() { }\n    [Implements(Requirements.CLI_002)] public void Email() { }\n}\n");
            File.WriteAllText(Path.Combine(agent, "tests", "ClientesTests.cs").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
                "public class ClientesTests\n{\n" +
                "    [Fact, Verifies(Requirements.CLI_001, AcceptanceCriteria.CLI_001.AC_1)] public void Alta() { }\n" +
                "    [Fact, Verifies(Requirements.CLI_001, AcceptanceCriteria.CLI_001.AC_2)] public void Dni() { }\n" +
                "    [Fact, Verifies(Requirements.CLI_002, AcceptanceCriteria.CLI_002.AC_1)] public void Email() { }\n}\n");
            Git(agent, "add", "-A");
            Git(agent, "commit", "-q", "-m", "CLI-001, CLI-002: implementation");
            Git(agent, "push", "-q");
            pr.Labels.Remove("spectrace:implement");

            var pull = Next();
            Assert.Equal("Get the agent's work", pull.Title);
            Assert.Contains("git pull", pull.Commands);
            Git(Work, "pull", "-q");

            // 5. Review, then accept: the lock of the whole story, committed and pushed.
            var review = Next();
            Assert.Equal(5, review.Step);
            Assert.Contains("dotnet spectrace accept", review.Commands);
            Assert.Equal(0, StoryFlow.Accept(Ctx()));
            var model = TraceScanner.Scan(SpecTraceConfig.Load(Work));
            Assert.All(model.Requirements, t => Assert.False(t.DriftInfo.Any || !t.DriftInfo.IsLocked));
            Assert.Contains("#2: reviewed (CLI-001, CLI-002)", Git(Work, "log", "-1", "--format=%s"));
            Assert.Equal("0", Git(Work, "rev-list", "--count", "origin/spec/gh-2-alta-de-clientes..HEAD").Trim());

            // 6. Merge, then done.
            Assert.Equal(6, Next().Step);
            Assert.Equal("Merge", Next().Title);
            pr.State = "MERGED";
            Assert.Equal("Done", Next().Title);

            // Without gh, git alone tells that the branch is in main.
            _gh.LoggedIn = false;
            Git(Work, "push", "-q", "origin", "HEAD:main");
            Assert.Equal("Done", Next().Title);
        }

        [Fact]
        public void Accept_refuses_a_story_approved_by_halves_and_work_still_missing()
        {
            StoryFlow.Start(Ctx(), "#2");
            Write("specs/clientes.spec.md", Spec.Replace("> Open questions:\n> - ¿Se valida la letra del DNI?\n", "")
                .Replace("## CLI-001: Alta de un cliente\nStatus: proposed", "## CLI-001: Alta de un cliente\nStatus: approved"));

            var half = Assert.Throws<InvalidOperationException>(() => StoryFlow.Accept(Ctx()));
            Assert.Contains("CLI-002", half.Message);
            Assert.Contains("accepted whole", half.Message);

            StoryFlow.Approve(Ctx(), null, force: false);
            Assert.Equal(1, StoryFlow.Accept(Ctx()));
            Assert.Contains("CLI-001: missing implementation, tests", _out.ToString());
        }

        [Fact]
        public void Submit_refuses_when_the_pull_request_of_the_branch_is_already_merged()
        {
            StoryFlow.Start(Ctx(), "#2");
            Write("specs/clientes.spec.md", Spec.Replace("> Open questions:\n> - ¿Se valida la letra del DNI?\n", ""));
            StoryFlow.Approve(Ctx(), null, force: false);
            _gh.Prs.Add(new FakePr(7, "spec/gh-2-alta-de-clientes", "main", "", "") { State = "MERGED" });

            var error = Assert.Throws<InvalidOperationException>(() => StoryFlow.Submit(Ctx()));

            Assert.Contains("already merged", error.Message);
            Assert.Contains("dotnet spectrace start #2", error.Message);
        }

        [Fact]
        public void Without_gh_submit_pushes_and_says_what_to_do_on_github()
        {
            _gh.LoggedIn = false;
            StoryFlow.Start(Ctx(), "#2");
            Write("specs/clientes.spec.md", Spec.Replace("> Open questions:\n> - ¿Se valida la letra del DNI?\n", ""));
            StoryFlow.Approve(Ctx(), null, force: false);

            Assert.Equal(0, StoryFlow.Submit(Ctx()));

            Assert.Empty(_gh.Prs);
            Assert.Contains("compare/main...spec/gh-2-alta-de-clientes", _out.ToString());
            Assert.Contains("Add the label spectrace:implement", _out.ToString());
            Assert.Contains("spec/gh-2-alta-de-clientes", Git(Work, "ls-remote", "--heads", "origin"));
        }

        [Fact]
        public void Start_refuses_uncommitted_changes_and_reuses_the_story_branch()
        {
            Write("notes.txt", "draft");
            Assert.Throws<InvalidOperationException>(() => StoryFlow.Start(Ctx(), "#2"));
            File.Delete(Path.Combine(Work, "notes.txt"));

            StoryFlow.Start(Ctx(), "#2");
            Git(Work, "checkout", "-q", "main");
            StoryFlow.Start(Ctx(), "#2");

            Assert.Equal("spec/gh-2-alta-de-clientes", Git(Work, "branch", "--show-current").Trim());
            Assert.Contains("already has the branch", _out.ToString());
        }

        [Fact]
        public void Spec_edits_keep_everything_else()
        {
            var r = SpecParser.Parse("c.spec.md", Spec).Requirements[0];
            Assert.Equal(new[] { "¿Se valida la letra del DNI?" }, SpecEdits.OpenQuestions(Spec, r).ToArray());
            Assert.Empty(SpecEdits.OpenQuestions(Spec, SpecParser.Parse("c.spec.md", Spec).Requirements[1]));

            var approved = SpecEdits.Approve(Spec, r);
            Assert.Contains("## CLI-001: Alta de un cliente\nStatus: approved\nSource: #2", approved);
            Assert.Contains("## CLI-002: Email obligatorio\nStatus: proposed", approved);
            Assert.Contains("> Open questions:", approved);

            var noStatus = "## X-1: T\n\nWhen a, the system shall b.\n\n- AC-1: Given a, then b.\n";
            Assert.StartsWith("## X-1: T\nStatus: approved\n", SpecEdits.Approve(noStatus, SpecParser.Parse("x.spec.md", noStatus).Requirements[0]));
            Assert.Equal("spec/gh-12-alta-de-clientes-y-socios", StoryFlow.BranchName(new StoryRef("github", "o/r#12", "u"), "¡Alta de clientes y socios!"));
            Assert.Equal("spec/gh-12", StoryFlow.BranchName(new StoryRef("github", "o/r#12", "u"), null));
        }

        // ---------- helpers ----------

        private void Write(string relative, string text)
        {
            var path = Path.Combine(Work, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private string Read(string relative) => File.ReadAllText(Path.Combine(Work, relative)).Replace("\r\n", "\n");

        private static void Identity(string dir)
        {
            Git(dir, "config", "user.name", "Test");
            Git(dir, "config", "user.email", "test@example.com");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "config", "core.autocrlf", "false");
        }

        private static string Git(string dir, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private sealed class FakeTracker : IStoryTracker
        {
            public string Name => "github";

            public bool TryParse(string text, out StoryRef reference)
            {
                var t = text.Trim().Replace("o/r#", "#");
                reference = null!;
                if (!t.StartsWith("#", StringComparison.Ordinal) || !int.TryParse(t.Substring(1), out var n)) return false;
                reference = new StoryRef("github", $"o/r#{n}", $"https://github.com/o/r/issues/{n}") { Display = $"#{n}" };
                return true;
            }

            public Story Get(StoryRef reference) =>
                new(reference, "Alta de clientes", "open", new[] { "user-story" }, "po", "body", Array.Empty<StoryComment>());

            public IReadOnlyList<StorySummary> List(StoryQuery query) => throw new StoryException("offline");
        }

        private sealed class FakePr
        {
            public FakePr(int number, string branch, string @base, string title, string body)
            {
                Number = number;
                Branch = branch;
                Base = @base;
                Title = title;
                Body = body;
            }

            public int Number { get; }
            public string Branch { get; }
            public string Base { get; }
            public string Title { get; }
            public string Body { get; }
            public string State { get; set; } = "OPEN";
            public List<string> Labels { get; } = new();
        }

        private sealed class FakeGh
        {
            public bool LoggedIn { get; set; } = true;
            public List<FakePr> Prs { get; } = new();

            public (int, string) Run(string[] args)
            {
                string Arg(string name) => args[Array.IndexOf(args, name) + 1];
                switch (args[0], args.ElementAtOrDefault(1))
                {
                    case ("auth", "status"):
                        return LoggedIn ? (0, "logged in") : (1, "not logged in");
                    case ("pr", "list"):
                        var pr = Prs.LastOrDefault(p => p.Branch == Arg("--head"));
                        return (0, pr == null ? "[]" : JsonSerializer.Serialize(new[]
                        {
                            new { number = pr.Number, url = $"https://github.com/o/r/pull/{pr.Number}", state = pr.State, labels = pr.Labels.Select(l => new { name = l }) },
                        }));
                    case ("pr", "create"):
                        Prs.Add(new FakePr(Prs.Count + 10, Arg("--head"), Arg("--base"), Arg("--title"), Arg("--body")));
                        return (0, "created");
                    case ("pr", "edit"):
                        Prs.Single(p => p.Number == int.Parse(args[2])).Labels.Add(Arg("--add-label"));
                        return (0, "");
                    default:
                        return (1, "unknown gh command " + string.Join(" ", args));
                }
            }
        }
    }

    internal static class TestExtensions
    {
        public static T Also<T>(this T value, Action<T> action)
        {
            action(value);
            return value;
        }
    }
}
