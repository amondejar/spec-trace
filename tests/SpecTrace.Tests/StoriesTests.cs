using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using SpecTrace.Tool;
using SpecTrace.Tool.Stories;
using Xunit;

namespace SpecTrace.Tests
{
    /// <summary>v0.5: user stories from GitHub Issues.</summary>
    public class StoriesTests
    {
        internal const string FormBody = @"### As a

organizer

### I want

to move one of my bookings to another time on the same day

### So that

I keep the booking

### Acceptance criteria

- Given my booking from 10:00 to 11:00, when I move it to 12:00 and the room is free, then it is moved.
- If the room is taken at the new time I get an error.
- Only I can move my bookings.

### Notes

_No response_";

        private static GitHubIssues Tracker(FakeGitHub fake, string? repository = "acme/shop", string? token = null, GitHubTrackerConfig? config = null) =>
            new(config ?? new GitHubTrackerConfig(), repository, () => token, fake);

        // ---------- Story text ----------

        [Fact]
        public void Issue_form_sections_become_a_user_story_and_criteria()
        {
            var story = new Story(new StoryRef("github", "acme/shop#3", "u"), "Move a booking", "open", Array.Empty<string>(), "ana", FormBody, Array.Empty<StoryComment>());

            Assert.Equal("As an organizer, I want to move one of my bookings to another time on the same day, so that I keep the booking.", story.Narrative);
            Assert.Equal(3, story.AcceptanceCriteria.Count);
            Assert.StartsWith("Given my booking from 10:00", story.AcceptanceCriteria[0]);
            Assert.Equal("", story.Section("notes"));
        }

        [Theory]
        [InlineData("1. First\n2. Second", "First|Second")]
        [InlineData("- [ ] First\n- [x] Second", "First|Second")]
        [InlineData("First line\n\nSecond line", "First line|Second line")]
        [InlineData("_No response_", "")]
        public void Criteria_are_read_from_lists_or_lines(string section, string expected)
        {
            var story = new Story(new StoryRef("github", "k", "u"), "t", "open", Array.Empty<string>(), null,
                "### Acceptance criteria\n\n" + section + "\n\n### Notes\n\nx", Array.Empty<StoryComment>());

            Assert.Equal(expected, string.Join("|", story.AcceptanceCriteria));
        }

        [Fact]
        public void A_free_form_issue_has_no_sections_and_no_narrative()
        {
            var story = new Story(new StoryRef("github", "k", "u"), "t", "open", Array.Empty<string>(), null,
                "Booking the big room should be fairer.\n\n```\n### not a heading\n```", Array.Empty<StoryComment>());

            Assert.Empty(story.Sections);
            Assert.Null(story.Narrative);
            Assert.Empty(story.AcceptanceCriteria);
        }

        // ---------- References ----------

        [Theory]
        [InlineData("#3", "acme/shop#3", "#3")]
        [InlineData("3", "acme/shop#3", "#3")]
        [InlineData("other/repo#7", "other/repo#7", "other/repo#7")]
        [InlineData("https://github.com/acme/shop/issues/3", "acme/shop#3", "#3")]
        [InlineData("https://github.com/acme/shop/issues/3/", "acme/shop#3", "#3")]
        [InlineData("https://github.com/Other/Repo/issues/12#issuecomment-99", "Other/Repo#12", "Other/Repo#12")]
        public void References_are_recognized_without_calling_the_network(string text, string key, string display)
        {
            var fake = new FakeGitHub();

            Assert.True(Tracker(fake).TryParse(text, out var reference));
            Assert.Equal(key, reference.Key);
            Assert.Equal(display, reference.Display);
            Assert.Equal($"https://github.com/{key.Replace("#", "/issues/")}", reference.Url);
            Assert.Empty(fake.Requests);
        }

        [Theory]
        [InlineData("PROJ-3")]
        [InlineData("https://github.com/acme/shop/pull/3")]
        [InlineData("https://gitlab.com/acme/shop/issues/3")]
        [InlineData("#0")]
        [InlineData("issue three")]
        public void Other_text_is_not_a_github_reference(string text)
        {
            Assert.False(Tracker(new FakeGitHub()).TryParse(text, out _));
        }

        [Fact]
        public void Short_references_need_a_repository()
        {
            var tracker = Tracker(new FakeGitHub(), repository: null);

            Assert.False(tracker.TryParse("#3", out _));
            Assert.True(tracker.TryParse("acme/shop#3", out _));
            var error = Assert.Throws<ArgumentException>(() => tracker.Parse("#3"));
            Assert.Contains("spectrace.json", error.Message);
        }

        [Fact]
        public void GitHub_Enterprise_uses_its_own_web_and_api_addresses()
        {
            var fake = new FakeGitHub().Issue("acme/shop", 3, "Move", FormBody);
            var tracker = Tracker(fake, config: new GitHubTrackerConfig { Url = "https://github.example.com" });

            Assert.True(tracker.TryParse("https://github.example.com/acme/shop/issues/3", out var reference));
            Assert.False(tracker.TryParse("https://github.com/acme/shop/issues/3", out _));
            tracker.Get(reference);
            Assert.Equal("https://github.example.com/api/v3/repos/acme/shop/issues/3", fake.Requests.Single().RequestUri!.ToString());
        }

        // ---------- Reading ----------

        [Fact]
        public void Get_reads_the_issue_and_its_comments()
        {
            var fake = new FakeGitHub()
                .Issue("acme/shop", 3, "Move a booking", FormBody, new[] { "user-story" }, comments: 1)
                .Route("/repos/acme/shop/issues/3/comments?per_page=100", FakeGitHub.Comments(("luis", "Only on the same day, please.")));
            var tracker = Tracker(fake, token: "secret");
            tracker.TryParse("#3", out var reference);

            var story = tracker.Get(reference);

            Assert.Equal("Move a booking", story.Title);
            Assert.Equal(new[] { "user-story" }, story.Labels.ToArray());
            Assert.Equal("Only on the same day, please.", story.Comments.Single().Body);
            Assert.All(fake.Requests, r => Assert.Equal("Bearer secret", r.Headers.Authorization!.ToString()));
            Assert.All(fake.Requests, r => Assert.True(r.Headers.Contains("X-GitHub-Api-Version")));
        }

        [Fact]
        public void Without_a_token_requests_are_anonymous()
        {
            var fake = new FakeGitHub().Issue("acme/shop", 3, "Move", FormBody);
            var tracker = Tracker(fake);
            tracker.TryParse("#3", out var reference);

            tracker.Get(reference);

            Assert.Null(fake.Requests.Single().Headers.Authorization);
        }

        [Fact]
        public void A_pull_request_is_not_a_story()
        {
            var tracker = Tracker(new FakeGitHub().Issue("acme/shop", 2, "Add skill", "x", pullRequest: true));
            tracker.TryParse("#2", out var reference);

            var error = Assert.Throws<StoryException>(() => tracker.Get(reference));
            Assert.Contains("pull request", error.Message);
        }

        [Fact]
        public void A_missing_issue_explains_how_to_reach_private_repositories()
        {
            var tracker = Tracker(new FakeGitHub());
            tracker.TryParse("#99", out var reference);

            var error = Assert.Throws<StoryException>(() => tracker.Get(reference));
            Assert.Contains("Issue #99 was not found in acme/shop", error.Message);
            Assert.Contains("GITHUB_TOKEN", error.Message);
        }

        [Fact]
        public void Rate_limits_are_reported_with_what_to_do()
        {
            var fake = new FakeGitHub().Route("/repos/acme/shop/issues/3", "{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden,
                new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = "1893456000" });
            var tracker = Tracker(fake);
            tracker.TryParse("#3", out var reference);

            var error = Assert.Throws<StoryException>(() => tracker.Get(reference));
            Assert.Contains("rate limit", error.Message);
            Assert.Contains("GITHUB_TOKEN", error.Message);
        }

        [Fact]
        public void Network_failures_become_story_errors()
        {
            var tracker = Tracker(new FakeGitHub { Failure = new HttpRequestException("No such host is known.") });
            tracker.TryParse("#3", out var reference);

            var error = Assert.Throws<StoryException>(() => tracker.Get(reference));
            Assert.Contains("Could not reach GitHub", error.Message);
        }

        [Fact]
        public void List_filters_by_label_skips_pull_requests_and_pages()
        {
            var firstPage = Enumerable.Range(1, 100).Select(n => (n, $"Story {n}", n == 50)).ToArray();
            var fake = new FakeGitHub()
                .Route("/repos/acme/shop/issues?state=open&per_page=100&page=1&labels=user-story", FakeGitHub.IssueList(firstPage))
                .Route("/repos/acme/shop/issues?state=open&per_page=100&page=2&labels=user-story", FakeGitHub.IssueList((101, "Story 101", false)));

            var stories = Tracker(fake).List(new StoryQuery("user-story", Limit: 500));

            Assert.Equal(100, stories.Count);
            Assert.DoesNotContain(stories, s => s.Ref.Key == "acme/shop#50");
            Assert.Equal("#101", stories.Last().Ref.Display);
        }

        // ---------- Repository from the git remote ----------

        [Theory]
        [InlineData("https://github.com/amondejar/room-booking.git", "amondejar/room-booking")]
        [InlineData("git@github.com:amondejar/room-booking.git", "amondejar/room-booking")]
        [InlineData("ssh://git@github.com/amondejar/room-booking", "amondejar/room-booking")]
        [InlineData("https://gitlab.com/amondejar/room-booking.git", null)]
        public void The_repository_comes_from_the_origin_remote(string url, string? expected)
        {
            var root = Path.Combine(Path.GetTempPath(), "spectrace-git-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                Directory.CreateDirectory(Path.Combine(root, "src"));
                File.WriteAllText(Path.Combine(root, ".git", "config"),
                    "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/someone/else.git\n" +
                    $"[remote \"origin\"]\n\turl = {url}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");

                Assert.Equal(expected, GitRemote.OriginRepository(Path.Combine(root, "src"), "github.com"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void A_worktree_follows_its_gitdir_pointer()
        {
            var root = Path.Combine(Path.GetTempPath(), "spectrace-wt-" + Guid.NewGuid().ToString("N"));
            try
            {
                var main = Path.Combine(root, "main", ".git");
                var worktreeGit = Path.Combine(main, "worktrees", "feature");
                Directory.CreateDirectory(worktreeGit);
                Directory.CreateDirectory(Path.Combine(root, "feature"));
                File.WriteAllText(Path.Combine(main, "config"), "[remote \"origin\"]\n\turl = https://github.com/acme/shop.git\n");
                File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..\n");
                File.WriteAllText(Path.Combine(root, "feature", ".git"), "gitdir: " + worktreeGit + "\n");

                Assert.Equal("acme/shop", GitRemote.OriginRepository(Path.Combine(root, "feature"), "github.com"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
