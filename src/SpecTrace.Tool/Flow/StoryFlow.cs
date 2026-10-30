using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SpecTrace.Specs;
using SpecTrace.Tool.Automation;
using SpecTrace.Tool.Stories;

namespace SpecTrace.Tool.Flow;

/// <summary>What there is to do now, and the command that does it.</summary>
/// <param name="Step">1 start · 2 spec · 3 approve · 4 agent · 5 review · 6 merge; 0 outside a story.</param>
public sealed record NextStep(int Step, string Title, IReadOnlyList<string> Facts, string Do, IReadOnlyList<string> Commands)
{
    public const int Steps = 6;
}

/// <summary>Everything the flow commands work with; the tracker and gh are replaceable in tests.</summary>
public sealed class FlowContext
{
    public FlowContext(SpecTraceConfig config, TextWriter output, IStoryTracker? tracker = null, GitHubCli? gh = null)
    {
        Config = config;
        Out = output;
        Git = Git.Open(config.Root) ?? throw new ArgumentException("The story flow needs a git repository (and git on the PATH).");
        Tracker = tracker ?? GitHubIssues.FromConfig(config);
        Gh = gh ?? new GitHubCli(config.Root);
    }

    public SpecTraceConfig Config { get; }
    public TextWriter Out { get; }
    public Git Git { get; }
    public IStoryTracker Tracker { get; }
    public GitHubCli Gh { get; }

    public string DefaultBranch => AgentWorkflow.DefaultBranch(Config.Root);
}

/// <summary>
/// The whole cycle of a user story as six steps, with a command for each human moment: <c>start</c> (branch for the
/// story), <c>approve</c> (all of the story's requirements at once), <c>submit</c> (commit, push, pull request and the
/// label that starts the agent), <c>accept</c> (record the review of everything the story covers) and <c>next</c>,
/// which says at any time where the story is and what to run.
/// </summary>
public static class StoryFlow
{
    public const string BranchPrefix = "spec/gh-";

    private static readonly Regex BranchStory = new(@"^spec/gh-(?<n>\d+)(?:-|$)", RegexOptions.Compiled);

    // ---------- Story and branch ----------

    /// <summary>The story of a branch named spec/gh-N-..., or null.</summary>
    public static StoryRef? StoryOfBranch(string? branch, IStoryTracker tracker)
    {
        var m = branch == null ? Match.Empty : BranchStory.Match(branch);
        return m.Success && tracker.TryParse("#" + m.Groups["n"].Value, out var reference) ? reference : null;
    }

    /// <summary>spec/gh-2-alta-de-clientes: the issue number and up to five words of the title.</summary>
    public static string BranchName(StoryRef story, string? title)
    {
        var number = story.Key.Substring(story.Key.LastIndexOf('#') + 1);
        var slug = Slug(title);
        return BranchPrefix + number + (slug.Length > 0 ? "-" + slug : "");
    }

    internal static string Slug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var plain = new string(title.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : ' ')
            .ToArray());
        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5);
        var slug = string.Join("-", words);
        return slug.Length > 40 ? slug.Substring(0, 40).TrimEnd('-') : slug;
    }

    /// <summary>The story's requirements that still count (not deprecated).</summary>
    public static IReadOnlyList<RequirementTrace> RequirementsOf(TraceModel model, IStoryTracker tracker, StoryRef story)
    {
        var ids = new HashSet<string>(StoryReports.RequirementsFrom(model, tracker, story).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        return model.Requirements.Where(t => ids.Contains(t.Requirement.Id) && !t.Requirement.IsDeprecated).ToList();
    }

    private static StoryRef CurrentStory(FlowContext ctx, string? explicitStory)
    {
        if (explicitStory != null) return Parse(ctx.Tracker, explicitStory);
        var branch = ctx.Git.CurrentBranch();
        return StoryOfBranch(branch, ctx.Tracker)
               ?? throw new ArgumentException($"The branch '{branch}' is not a story branch ({BranchPrefix}<issue>-...). " +
                                              "Start one with `dotnet spectrace start #N`, or name the story: --story #N.");
    }

    private static StoryRef Parse(IStoryTracker tracker, string text) =>
        tracker is GitHubIssues github ? github.Parse(text)
        : tracker.TryParse(text, out var reference) ? reference
        : throw new ArgumentException($"'{text}' is not a story reference: use #12, owner/repo#12 or the issue URL.");

    private static IReadOnlyList<string> OpenQuestions(SpecTraceConfig config, SpecRequirement r)
    {
        var path = Path.Combine(config.Root, r.FilePath);
        return File.Exists(path) ? SpecEdits.OpenQuestions(File.ReadAllText(path), r) : Array.Empty<string>();
    }

    private static bool IsReviewed(RequirementTrace t) => t.DriftInfo.IsLocked && !t.DriftInfo.Any;

    // ---------- next ----------

    public static NextStep Next(FlowContext ctx, string? explicitStory = null)
    {
        var git = ctx.Git;
        var branch = git.CurrentBranch();
        var story = explicitStory != null ? Parse(ctx.Tracker, explicitStory) : StoryOfBranch(branch, ctx.Tracker);
        var model = TraceScanner.Scan(ctx.Config);

        if (story == null) return Outside(ctx, model, branch);

        var facts = new List<string> { $"Story {story.Display} · branch {branch}" };
        var reqs = RequirementsOf(model, ctx.Tracker, story);
        if (reqs.Count == 0)
        {
            return new NextStep(2, "Write the spec", facts,
                "No requirement cites this story yet. Ask the agent to turn it into proposed requirements (in Claude Code):",
                new[] { $"/mcp__spectrace__story_to_spec {story.Display}" });
        }

        foreach (var t in reqs) facts.Add("  " + Describe(ctx.Config, t));

        var proposed = reqs.Where(t => !t.Requirement.IsEnforced).ToList();
        if (proposed.Count > 0)
        {
            var questions = proposed.Sum(t => OpenQuestions(ctx.Config, t.Requirement).Count);
            if (questions > 0)
            {
                var file = proposed[0].Requirement.FilePath;
                return new NextStep(3, "Answer the open questions", facts,
                    $"{questions} open question(s). As PO, answer them in the issue; as developer, turn each answer into a criterion " +
                    "(or a change to the statement) and delete the questions. A requirement the story should not have: Status: deprecated. " +
                    "An agent can review the spec first (in Claude Code). Then approve:",
                    new[] { $"/mcp__spectrace__review_spec {Path.GetFileName(file)}", "dotnet spectrace approve" });
            }

            return new NextStep(3, "Approve the story", facts,
                "The requirements have no open questions. Approve all of them at once:", new[] { "dotnet spectrace approve" });
        }

        // Where the branch stands against GitHub.
        var upstream = git.Upstream();
        if (upstream != null) git.Command("fetch", "--quiet", "origin", branch!, ctx.DefaultBranch);
        var behind = upstream == null ? 0 : git.Count(upstream, "HEAD");
        var unpushed = upstream == null ? int.MaxValue : git.Count("HEAD", upstream);
        var specsDir = (git.Prefix + ctx.Config.Specs.Replace('\\', '/')).Trim('/') + "/";
        var dirty = git.DirtyFiles();
        var dirtySpecs = dirty.Where(f => f.StartsWith(specsDir, StringComparison.Ordinal)).ToList();
        var pr = ctx.Gh.Available ? ctx.Gh.ForBranch(branch!) : null;
        if (pr != null) facts.Add($"Pull request #{pr.Number} ({pr.State.ToLowerInvariant()}){(pr.HasLabel(AgentWorkflow.Label) ? $", label {AgentWorkflow.Label}" : "")}: {pr.Url}");
        if (dirty.Count > dirtySpecs.Count) facts.Add($"Uncommitted changes outside the specs: {string.Join(", ", dirty.Except(dirtySpecs).Take(5))}");

        // Merged even without gh: everything the branch has pushed is already in the default branch.
        var inMain = upstream != null && dirtySpecs.Count == 0 && unpushed == 0 &&
                     git.RefExists("origin/" + ctx.DefaultBranch) &&
                     git.Command("merge-base", "--is-ancestor", "HEAD", "origin/" + ctx.DefaultBranch).Code == 0;
        if (pr is { IsMerged: true } || (pr == null && inMain))
        {
            var unreviewed = reqs.Where(t => t.Requirement.IsEnforced && !IsReviewed(t)).Select(t => t.Requirement.Id).ToList();
            if (unreviewed.Count > 0)
            {
                return new NextStep(6, "Merged without a review", facts,
                    $"The pull request is merged, but {string.Join(", ", unreviewed)} has no recorded review (spectrace.lock). Review it and " +
                    $"record it through a small pull request from {ctx.DefaultBranch}:",
                    new[]
                    {
                        $"git checkout {ctx.DefaultBranch}", "git pull", $"git checkout -b chore/review-{Number(story)}",
                        "dotnet spectrace lock " + string.Join(" ", unreviewed.Select(id => "--requirement " + id)),
                        "git commit -am \"" + story.Display + ": reviewed\"", "git push -u origin HEAD",
                    });
            }

            return new NextStep(6, "Done", facts, $"The pull request of this branch is merged. Go back to {ctx.DefaultBranch} for the next story:",
                new[] { $"git checkout {ctx.DefaultBranch}", "git pull", "dotnet spectrace next" });
        }

        var enforced = reqs.Where(t => t.Requirement.IsEnforced).ToList();
        if (behind > 0)
        {
            return new NextStep(enforced.Any(t => t.HasGap) ? 4 : 5, "Get the agent's work", facts,
                $"The branch on GitHub has {behind} commit(s) you do not have (usually the agent's). Bring them, then run next again:",
                new[] { "git pull", "dotnet spectrace next" });
        }

        if (enforced.Any(t => t.HasGap))
        {
            var waiting = pr != null && pr.IsOpen && pr.HasLabel(AgentWorkflow.Label) && dirtySpecs.Count == 0 && unpushed == 0;
            if (!waiting && (dirtySpecs.Count > 0 || unpushed > 0 || pr == null || !pr.HasLabel(AgentWorkflow.Label)))
            {
                return new NextStep(4, "Send it to the agent", facts,
                    "The story is approved and nothing implements it yet. Commit and push the specs, open the pull request and start the agent:",
                    new[] { "dotnet spectrace submit" });
            }

            return new NextStep(4, "The agent is working", facts,
                "Wait for the agent's comment on the pull request (Actions tab: SpecTrace implement). Then run next again:",
                new[] { "dotnet spectrace next" });
        }

        if (enforced.Any(t => !IsReviewed(t)))
        {
            return new NextStep(5, "Review", facts,
                "Everything has code and a test per criterion. Review it as a developer (the agent's comment on the pull request lists " +
                "each criterion and its test), then accept it: that records the review and pushes it.",
                new[] { "dotnet test", "dotnet spectrace report", "dotnet spectrace accept" });
        }

        if (dirty.Count > 0 || unpushed > 0)
        {
            return new NextStep(5, "Push", facts, "Reviewed, but not everything is on GitHub yet:", new[] { "git status", "git push" });
        }

        return new NextStep(6, "Merge", facts,
            $"Reviewed and pushed. Merge the pull request on GitHub when CI is green{(pr != null ? $" ({pr.Url})" : "")}, then:",
            new[] { $"git checkout {ctx.DefaultBranch}", "git pull" });
    }

    private static NextStep Outside(FlowContext ctx, TraceModel model, string? branch)
    {
        var facts = new List<string> { $"Branch {branch ?? "(detached)"}: not a story branch" };
        var pending = Automation.WorkQueue.Compute(ctx.Config, model);
        if (pending.Count > 0) facts.Add($"Approved but not finished: {string.Join(", ", pending.Select(p => p.Requirement.Id))}");

        try
        {
            var unspecified = ctx.Tracker.List(new StoryQuery(ctx.Config.Trackers.GitHub.Label, "open", Limit: 20))
                .Where(s => StoryReports.RequirementsFrom(model, ctx.Tracker, s.Ref).Count == 0)
                .Take(5)
                .ToList();
            if (unspecified.Count > 0) facts.Add("Stories without a spec: " + string.Join(", ", unspecified.Select(s => $"{s.Ref.Display} {s.Title}")));
        }
        catch (Exception ex) when (ex is StoryException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException)
        {
            // The tracker is optional here: no network or no token is not an error for next.
        }

        return new NextStep(1, "Start a story", facts,
            "As PO, write the story in an issue (label user-story). As developer, start it: this creates its branch.",
            new[] { "dotnet spectrace start #N" });
    }

    private static string Describe(SpecTraceConfig config, RequirementTrace t)
    {
        var r = t.Requirement;
        var parts = new List<string> { $"{r.Id} {r.Title}: {r.Status}" };
        if (!r.IsEnforced)
        {
            var q = OpenQuestions(config, r).Count;
            if (q > 0) parts.Add($"{q} open question(s)");
        }
        else if (t.HasGap)
        {
            parts.Add("missing " + string.Join(", ", Automation.WorkQueue.Missing(t)));
        }
        else
        {
            parts.Add($"{t.CoveredCriteria}/{r.Criteria.Count} criteria tested");
            parts.Add(IsReviewed(t) ? "reviewed" : "not reviewed");
        }

        return string.Join(" · ", parts);
    }

    public static string Render(NextStep step)
    {
        var sb = new StringBuilder();
        foreach (var f in step.Facts) sb.AppendLine(f);
        sb.AppendLine();
        sb.AppendLine(step.Step > 0 ? $"Step {step.Step} of {NextStep.Steps}: {step.Title}" : step.Title);
        sb.AppendLine(step.Do);
        foreach (var c in step.Commands) sb.AppendLine("  " + c);
        return sb.ToString().TrimEnd();
    }

    // ---------- start ----------

    public static int Start(FlowContext ctx, string storyText)
    {
        var git = ctx.Git;
        var story = Parse(ctx.Tracker, storyText);
        var dirty = git.DirtyFiles();
        if (dirty.Count > 0)
        {
            throw new InvalidOperationException("There are uncommitted changes; commit or stash them before starting a story: " +
                                                string.Join(", ", dirty.Take(5)));
        }

        string? title = null;
        try
        {
            title = ctx.Tracker.Get(story).Title;
        }
        catch (Exception ex) when (ex is StoryException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            ctx.Out.WriteLine($"(Could not read {story.Display} from GitHub: the branch is named after the number only.)");
        }

        var main = ctx.DefaultBranch;
        git.Command("fetch", "--quiet", "origin");
        var existing = git.Command("for-each-ref", "--format=%(refname:short)", $"refs/heads/{BranchPrefix}{Number(story)}-*", $"refs/heads/{BranchPrefix}{Number(story)}",
            $"refs/remotes/origin/{BranchPrefix}{Number(story)}-*").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).FirstOrDefault();

        string branch;
        if (existing != null)
        {
            branch = existing.StartsWith("origin/", StringComparison.Ordinal) ? existing.Substring("origin/".Length) : existing;
            Check(git.Command("switch", branch), "git switch " + branch);
            ctx.Out.WriteLine($"Story {story.Display} already has the branch {branch}: switched to it.");
        }
        else
        {
            branch = BranchName(story, title);
            var from = git.RefExists("origin/" + main) ? "origin/" + main : main;
            Check(git.Command("switch", "-c", branch, from), "git switch -c " + branch);
            ctx.Out.WriteLine($"Created the branch {branch} from {from} for story {story.Display}{(title != null ? $" ({title})" : "")}.");
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine(Render(Next(ctx, null)));
        return 0;
    }

    private static string Number(StoryRef story) => story.Key.Substring(story.Key.LastIndexOf('#') + 1);

    // ---------- approve ----------

    public static int Approve(FlowContext ctx, string? explicitStory, bool force)
    {
        var story = CurrentStory(ctx, explicitStory);
        var model = TraceScanner.Scan(ctx.Config);
        var reqs = RequirementsOf(model, ctx.Tracker, story);
        if (reqs.Count == 0) throw new ArgumentException($"No requirement cites {story.Display} (Source: line). Write the spec first: /mcp__spectrace__story_to_spec {story.Display}");

        var toApprove = reqs.Where(t => !t.Requirement.IsEnforced).Select(t => t.Requirement).ToList();
        if (toApprove.Count == 0)
        {
            ctx.Out.WriteLine($"Every requirement of {story.Display} is already approved: {string.Join(", ", reqs.Select(t => t.Requirement.Id))}.");
            ctx.Out.WriteLine("Next: dotnet spectrace next");
            return 0;
        }

        var questions = toApprove.Select(r => (r, Q: OpenQuestions(ctx.Config, r))).Where(x => x.Q.Count > 0).ToList();
        if (questions.Count > 0 && !force)
        {
            ctx.Out.WriteLine("Not approved: these requirements still have open questions. Answer them, turn the answers into criteria and delete the questions:");
            foreach (var (r, q) in questions)
            {
                ctx.Out.WriteLine($"  {r.Id} {r.Title} ({r.FilePath}:{r.Line + 1})");
                foreach (var item in q) ctx.Out.WriteLine("    - " + item);
            }

            ctx.Out.WriteLine("If a question does not need an answer, delete it. To approve anyway: dotnet spectrace approve --force");
            return 1;
        }

        // From the bottom of each file up, so inserted Status lines do not move the requirements still to edit.
        foreach (var group in toApprove.GroupBy(r => r.FilePath))
        {
            var path = Path.Combine(ctx.Config.Root, group.Key);
            var text = File.ReadAllText(path);
            foreach (var r in group.OrderByDescending(r => r.Line)) text = SpecEdits.Approve(text, r);
            File.WriteAllText(path, text);
        }

        ctx.Out.WriteLine($"Approved {toApprove.Count} requirement(s) of {story.Display}: {string.Join(", ", toApprove.Select(r => r.Id))}.");
        foreach (var issue in SpecLint.Check(TraceScanner.Scan(ctx.Config).Catalog.Requirements.Where(r => toApprove.Any(a => a.Id == r.Id))))
        {
            ctx.Out.WriteLine($"  Note {issue.FilePath}:{issue.Line + 1}: {issue.Message}");
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine(Render(Next(ctx, explicitStory)));
        return 0;
    }

    // ---------- submit ----------

    public static int Submit(FlowContext ctx)
    {
        var git = ctx.Git;
        var branch = git.CurrentBranch() ?? throw new InvalidOperationException("Not on a branch.");
        var story = CurrentStory(ctx, null);
        var model = TraceScanner.Scan(ctx.Config);
        var reqs = RequirementsOf(model, ctx.Tracker, story);
        var proposed = reqs.Where(t => !t.Requirement.IsEnforced).ToList();
        if (proposed.Count > 0)
        {
            throw new InvalidOperationException($"{string.Join(", ", proposed.Select(t => t.Requirement.Id))} of {story.Display} are not approved yet: " +
                                                "dotnet spectrace approve (or Status: deprecated for one the story should not have).");
        }

        var main = ctx.DefaultBranch;
        if (string.Equals(branch, main, StringComparison.Ordinal)) throw new InvalidOperationException($"Submit works on the story's branch, not on {main}.");

        var pr = ctx.Gh.Available ? ctx.Gh.ForBranch(branch) : null;
        if (pr is { IsMerged: true })
        {
            throw new InvalidOperationException($"Pull request #{pr.Number} of {branch} is already merged ({pr.Url}). New work on this story needs a new " +
                                                $"pull request: git checkout {main} && git pull && dotnet spectrace start {story.Display}");
        }

        // Only the specs: code and other files are the agent's (or yours to commit on purpose).
        var specsDir = ctx.Config.Specs;
        Check(git.Command("add", "--", specsDir), "git add " + specsDir);
        var staged = git.Command("diff", "--cached", "--name-only").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ids = string.Join(", ", reqs.Select(t => t.Requirement.Id));
        if (staged.Length > 0)
        {
            Check(git.Command("commit", "-m", $"{story.Display}: specs approved ({ids})"), "git commit");
            ctx.Out.WriteLine($"Committed {staged.Length} spec file(s).");
        }

        var others = git.DirtyFiles();
        if (others.Count > 0) ctx.Out.WriteLine($"Not included (commit them yourself if they belong here): {string.Join(", ", others.Take(8))}");

        Check(git.Command("push", "-u", "origin", branch), "git push");
        ctx.Out.WriteLine($"Pushed {branch}.");

        var gaps = reqs.Where(t => t.Requirement.IsEnforced && t.HasGap).ToList();
        if (!ctx.Gh.Available)
        {
            var repo = GitRemote.OriginRepository(ctx.Config.Root, "github.com");
            ctx.Out.WriteLine();
            ctx.Out.WriteLine("The GitHub CLI (gh) is not installed or not logged in (gh auth login), so finish on github.com:");
            ctx.Out.WriteLine($"  1. Open the pull request: https://github.com/{repo ?? "<owner>/<repo>"}/compare/{main}...{branch}?expand=1");
            if (gaps.Count > 0) ctx.Out.WriteLine($"  2. Add the label {AgentWorkflow.Label}: the agent starts.");
            return 0;
        }

        if (pr == null || !pr.IsOpen)
        {
            string? title = null;
            try { title = ctx.Tracker.Get(story).Title; }
            catch (Exception ex) when (ex is StoryException or InvalidOperationException or System.Net.Http.HttpRequestException) { }

            var body = new StringBuilder();
            body.AppendLine($"Story {story.Display}: spec, implementation and tests reviewed and merged together.");
            body.AppendLine();
            foreach (var t in reqs) body.AppendLine($"- {t.Requirement.Id}: {t.Requirement.Title}");
            body.AppendLine();
            body.AppendLine($"Closes {story.Display}");
            pr = ctx.Gh.Create(branch, main, $"{story.Display}: {title ?? ids}", body.ToString());
            ctx.Out.WriteLine($"Opened pull request #{pr.Number}: {pr.Url}");
        }

        if (gaps.Count == 0)
        {
            ctx.Out.WriteLine("Nothing is waiting for the agent: everything has code and tests. Next: dotnet spectrace next");
            return 0;
        }

        if (!pr.HasLabel(AgentWorkflow.Label)) ctx.Gh.AddLabel(pr.Number, AgentWorkflow.Label);
        ctx.Out.WriteLine($"Label {AgentWorkflow.Label} added: the agent implements {string.Join(", ", gaps.Select(t => t.Requirement.Id))} " +
                          "and comments on the pull request. CI is red until then (ST0003): that is expected.");
        ctx.Out.WriteLine("Next: dotnet spectrace next (it says when the agent has pushed)");
        return 0;
    }

    // ---------- accept ----------

    public static int Accept(FlowContext ctx)
    {
        var git = ctx.Git;
        var branch = git.CurrentBranch() ?? throw new InvalidOperationException("Not on a branch.");
        var story = CurrentStory(ctx, null);
        if (git.Upstream() != null) Check(git.Command("pull", "--ff-only"), "git pull --ff-only");

        var model = TraceScanner.Scan(ctx.Config);
        var reqs = RequirementsOf(model, ctx.Tracker, story);
        var proposed = reqs.Where(t => !t.Requirement.IsEnforced).ToList();
        if (proposed.Count > 0)
        {
            throw new InvalidOperationException($"{string.Join(", ", proposed.Select(t => t.Requirement.Id))} of {story.Display} are not approved: " +
                                                "a story is accepted whole. Approve them (dotnet spectrace approve) or set Status: deprecated.");
        }

        var gaps = reqs.Where(t => t.HasGap).ToList();
        if (gaps.Count > 0)
        {
            ctx.Out.WriteLine("Not accepted: these requirements are missing code or tests (has the agent finished?):");
            foreach (var t in gaps) ctx.Out.WriteLine($"  {t.Requirement.Id}: missing {string.Join(", ", Automation.WorkQueue.Missing(t))}");
            ctx.Out.WriteLine("Next: dotnet spectrace next");
            return 1;
        }

        if (reqs.Count == 0) throw new ArgumentException($"No requirement cites {story.Display}.");

        var merged = model.Lock ?? new SpecLock(Array.Empty<SpecLockEntry>());
        var updated = merged.Resolve(model.Catalog.Requirements);
        foreach (var t in reqs) updated = updated.With(SpecFingerprint.Entry(t.Requirement));
        File.WriteAllText(TraceScanner.LockPath(ctx.Config), updated.Render());

        var ids = string.Join(", ", reqs.Select(t => t.Requirement.Id));
        Check(git.Command("add", "--", SpecLock.FileName), "git add " + SpecLock.FileName);
        if (git.Command("diff", "--cached", "--quiet").Code != 0)
        {
            Check(git.Command("commit", "-m", $"{story.Display}: reviewed ({ids})"), "git commit");
        }

        Check(git.Command("push", "-u", "origin", branch), "git push");
        ctx.Out.WriteLine($"Review recorded for {ids} (spectrace.lock) and pushed.");

        var pr = ctx.Gh.Available ? ctx.Gh.ForBranch(branch) : null;
        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"Step 6 of {NextStep.Steps}: Merge");
        ctx.Out.WriteLine($"Merge the pull request on GitHub when CI is green{(pr != null ? $": {pr.Url}" : "")}. Then:");
        ctx.Out.WriteLine($"  git checkout {ctx.DefaultBranch}");
        ctx.Out.WriteLine("  git pull");
        return 0;
    }

    private static void Check((int Code, string Output) result, string what)
    {
        if (result.Code != 0) throw new InvalidOperationException($"{what} failed: {result.Output.Trim()}");
    }
}
