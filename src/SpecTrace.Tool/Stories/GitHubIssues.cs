using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpecTrace.Tool.Stories;

/// <summary>
/// User stories from GitHub Issues, through the REST API. Read-only. Public repositories work without a token;
/// private ones need GITHUB_TOKEN or GH_TOKEN (or a logged-in GitHub CLI) with read access to issues.
/// </summary>
public sealed class GitHubIssues : IStoryTracker
{
    private const int PageSize = 100;

    private readonly HttpClient _http;
    private readonly string? _defaultRepository;
    private readonly string _webBase;
    private readonly Uri _apiBase;
    private readonly Lazy<string?> _token;

    /// <param name="token">Resolved on the first request, so parsing references never looks for credentials.</param>
    public GitHubIssues(GitHubTrackerConfig config, string? defaultRepository, Func<string?> token, HttpMessageHandler? handler = null)
    {
        _token = new Lazy<string?>(token);
        _defaultRepository = defaultRepository;
        _webBase = config.Url.TrimEnd('/');
        var isGitHubCom = Uri.TryCreate(_webBase, UriKind.Absolute, out var web) && web.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        _apiBase = new Uri((config.ApiUrl ?? (isGitHubCom ? "https://api.github.com" : _webBase + "/api/v3")).TrimEnd('/') + "/");

        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpecTrace", typeof(GitHubIssues).Assembly.GetName().Version?.ToString(3) ?? "0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string Name => "github";

    /// <summary>The repository used for "#12" references, or null when it could not be determined.</summary>
    public string? DefaultRepository => _defaultRepository;

    /// <summary>Builds the tracker from spectrace.json, the environment and the git remote of the repository.</summary>
    public static GitHubIssues FromConfig(SpecTraceConfig config, HttpMessageHandler? handler = null, Func<string?>? tokenSource = null)
    {
        var github = config.Trackers.GitHub;
        var host = Uri.TryCreate(github.Url, UriKind.Absolute, out var web) ? web.Host : "github.com";
        var repository = Blank(github.Repository)
                         ?? Blank(Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"))
                         ?? GitRemote.OriginRepository(config.Root, host);
        return new GitHubIssues(github, repository, tokenSource ?? (() => Token(host)), handler);
    }

    // ---------- References ----------

    private static readonly Regex ShortRef = new(@"^(?:(?<repo>[A-Za-z0-9-_.]+/[A-Za-z0-9-_.]+))?#(?<n>\d+)$|^(?<n>\d+)$", RegexOptions.Compiled);

    public bool TryParse(string text, out StoryRef reference)
    {
        reference = null!;
        text = text.Trim();

        string? repo;
        int number;
        var m = ShortRef.Match(text);
        if (m.Success)
        {
            repo = m.Groups["repo"].Success ? m.Groups["repo"].Value : _defaultRepository;
            number = int.Parse(m.Groups["n"].Value);
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                 _webBase.StartsWith(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            // https://github.com/owner/repo/issues/3 (also .../issues/3#issuecomment-1 or with a trailing slash)
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 4 || !parts[2].Equals("issues", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[3], out number))
            {
                return false;
            }

            repo = parts[0] + "/" + parts[1];
        }
        else
        {
            return false;
        }

        if (repo == null || number <= 0) return false;
        var key = $"{repo}#{number}";
        reference = new StoryRef(Name, key, $"{_webBase}/{repo}/issues/{number}")
        {
            Display = string.Equals(repo, _defaultRepository, StringComparison.OrdinalIgnoreCase) ? $"#{number}" : key,
        };
        return true;
    }

    public StoryRef Parse(string text)
    {
        if (TryParse(text, out var reference)) return reference;
        if (_defaultRepository == null && ShortRef.IsMatch(text.Trim()))
        {
            throw new ArgumentException(
                "Cannot tell which GitHub repository '#N' refers to: set \"trackers\": { \"github\": { \"repository\": \"owner/repo\" } } in spectrace.json, " +
                "or use owner/repo#N.");
        }

        throw new ArgumentException($"'{text}' is not a GitHub issue reference. Use #12, owner/repo#12 or the issue URL.");
    }

    // ---------- Reading ----------

    public Story Get(StoryRef reference)
    {
        var (repo, number) = Split(reference);
        var issue = GetJson($"repos/{repo}/issues/{number}", $"Issue #{number} was not found in {repo}");
        if (issue.TryGetProperty("pull_request", out _))
        {
            throw new StoryException($"#{number} in {repo} is a pull request, not an issue.");
        }

        var comments = new List<StoryComment>();
        if (issue.TryGetProperty("comments", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt32() > 0)
        {
            foreach (var c in GetJson($"repos/{repo}/issues/{number}/comments?per_page={PageSize}", "Comments not found").EnumerateArray())
            {
                comments.Add(new StoryComment(
                    Login(c),
                    c.TryGetProperty("created_at", out var at) && at.TryGetDateTimeOffset(out var when) ? when : default,
                    Text(c, "body")));
            }
        }

        return new Story(reference, Text(issue, "title"), Text(issue, "state"), Labels(issue), Login(issue), Text(issue, "body"), comments);
    }

    public IReadOnlyList<StorySummary> List(StoryQuery query)
    {
        var repo = _defaultRepository ?? throw new ArgumentException(
            "Cannot tell which GitHub repository to list: set \"trackers\": { \"github\": { \"repository\": \"owner/repo\" } } in spectrace.json.");
        var state = query.State is "open" or "closed" or "all" ? query.State : throw new ArgumentException("State must be open, closed or all.");

        var result = new List<StorySummary>();
        for (var page = 1; result.Count < query.Limit && page <= 10; page++)
        {
            var path = $"repos/{repo}/issues?state={state}&per_page={PageSize}&page={page}" +
                       (string.IsNullOrEmpty(query.Label) ? "" : "&labels=" + Uri.EscapeDataString(query.Label!));
            var items = GetJson(path, $"Repository {repo} was not found").EnumerateArray().ToList();
            foreach (var issue in items.Where(i => !i.TryGetProperty("pull_request", out _)))
            {
                var number = issue.GetProperty("number").GetInt32();
                TryParse($"{repo}#{number}", out var reference);
                result.Add(new StorySummary(reference, Text(issue, "title"), Text(issue, "state"), Labels(issue)));
            }

            if (items.Count < PageSize) break;
        }

        return result.Take(query.Limit).ToList();
    }

    private JsonElement GetJson(string path, string notFound)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path));
            if (!string.IsNullOrEmpty(_token.Value)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Value);
            response = _http.SendAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StoryException($"Could not reach GitHub at {_apiBase.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(body).RootElement.Clone();

            var authenticated = !string.IsNullOrEmpty(_token.Value);
            throw new StoryException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => authenticated
                    ? $"{notFound} (or the token cannot read it)."
                    : $"{notFound}. If the repository is private, set GITHUB_TOKEN or GH_TOKEN with read access to issues.",
                HttpStatusCode.Unauthorized => "GitHub rejected the token (401): check GITHUB_TOKEN / GH_TOKEN.",
                HttpStatusCode.Forbidden or (HttpStatusCode)429 when IsRateLimited(response) =>
                    $"GitHub API rate limit reached{ResetTime(response)}." + (authenticated ? "" : " Set GITHUB_TOKEN or GH_TOKEN to get a higher limit."),
                HttpStatusCode.Forbidden => "GitHub denied access (403): the token needs read access to issues of this repository.",
                _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {path}.",
            });
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == (HttpStatusCode)429 ||
        (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0");

    private static string ResetTime(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-reset", out var values) && long.TryParse(values.FirstOrDefault(), out var epoch)
            ? $" (it resets at {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm})"
            : "";

    private static (string Repo, int Number) Split(StoryRef reference)
    {
        var hash = reference.Key.LastIndexOf('#');
        return (reference.Key.Substring(0, hash), int.Parse(reference.Key.Substring(hash + 1)));
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Login(JsonElement e) =>
        e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Text(user, "login") : "";

    private static IReadOnlyList<string> Labels(JsonElement issue) =>
        issue.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString()! : Text(l, "name")).Where(l => l.Length > 0).ToList()
            : Array.Empty<string>();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// GITHUB_TOKEN, GH_TOKEN, or the GitHub CLI's token for <paramref name="host"/> if it is installed and logged in
    /// (never another host's token). Null: anonymous.
    /// </summary>
    public static string? Token(string host)
    {
        var token = Blank(Environment.GetEnvironmentVariable("GITHUB_TOKEN")) ?? Blank(Environment.GetEnvironmentVariable("GH_TOKEN"));
        if (token != null) return token;

        try
        {
            var start = new ProcessStartInfo("gh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("auth");
            start.ArgumentList.Add("token");
            start.ArgumentList.Add("--hostname");
            start.ArgumentList.Add(host);
            using var gh = Process.Start(start);
            if (gh == null) return null;
            var output = gh.StandardOutput.ReadToEnd();
            return gh.WaitForExit(5000) && gh.ExitCode == 0 ? Blank(output) : null;
        }
        catch (Exception)
        {
            return null; // GitHub CLI not installed: anonymous access.
        }
    }
}
