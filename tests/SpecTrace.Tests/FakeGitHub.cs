using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpecTrace.Tests
{
    /// <summary>
    /// GitHub REST API stand-in: answers recorded JSON by path and query, and remembers the requests it received.
    /// The fixtures follow the shapes of GET /repos/{o}/{r}/issues/{n}, .../comments and .../issues?labels=.
    /// </summary>
    public sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, Dictionary<string, string> Headers)> _routes = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public Exception? Failure { get; set; }

        public FakeGitHub Route(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK, Dictionary<string, string>? headers = null)
        {
            _routes[pathAndQuery] = (status, body, headers ?? new Dictionary<string, string>());
            return this;
        }

        public FakeGitHub Issue(string repo, int number, string title, string body, string[]? labels = null, int comments = 0, bool pullRequest = false, string state = "open")
        {
            var issue = new JsonObject
            {
                ["number"] = number,
                ["title"] = title,
                ["body"] = body,
                ["state"] = state,
                ["html_url"] = $"https://github.com/{repo}/issues/{number}",
                ["user"] = new JsonObject { ["login"] = "amondejar" },
                ["labels"] = new JsonArray((labels ?? Array.Empty<string>()).Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()),
                ["comments"] = comments,
            };
            if (pullRequest) issue["pull_request"] = new JsonObject { ["url"] = "https://api.github.com/x" };
            return Route($"/repos/{repo}/issues/{number}", issue.ToJsonString());
        }

        public static string Comments(params (string Author, string Body)[] comments) =>
            new JsonArray(comments.Select((c, i) => (JsonNode)new JsonObject
            {
                ["user"] = new JsonObject { ["login"] = c.Author },
                ["created_at"] = $"2026-10-2{i}T10:00:00Z",
                ["body"] = c.Body,
            }).ToArray()).ToJsonString();

        public static string IssueList(params (int Number, string Title, bool PullRequest)[] items) =>
            new JsonArray(items.Select(i =>
            {
                var o = new JsonObject
                {
                    ["number"] = i.Number,
                    ["title"] = i.Title,
                    ["state"] = "open",
                    ["labels"] = new JsonArray((JsonNode)new JsonObject { ["name"] = "user-story" }),
                };
                if (i.PullRequest) o["pull_request"] = new JsonObject();
                return (JsonNode)o;
            }).ToArray()).ToJsonString();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure != null) throw Failure;

            var key = request.RequestUri!.PathAndQuery;
            if (key.StartsWith("/api/v3/", StringComparison.Ordinal)) key = key.Substring("/api/v3".Length); // GitHub Enterprise Server
            if (!_routes.TryGetValue(key, out var route))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json"),
                });
            }

            var response = new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
            foreach (var h in route.Headers) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return Task.FromResult(response);
        }
    }
}
