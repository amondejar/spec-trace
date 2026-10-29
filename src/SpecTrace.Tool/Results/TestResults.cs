using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SpecTrace.Tool.Results;

/// <summary>Outcome of a test or of an acceptance criterion, from the worst to the best.</summary>
public enum TestOutcome
{
    /// <summary>At least one run failed (also errors, timeouts and aborted runs).</summary>
    Failed,

    /// <summary>It has tests, but none of them ran (not in the results, skipped, inconclusive).</summary>
    NotRun,

    Passed,
}

/// <summary>One test from a results file: the method and how its runs ended (theories run once per data row).</summary>
public sealed record TestRun(string ClassName, string Method, TestOutcome Outcome, int Runs, string? Message);

/// <summary>
/// Results of <c>dotnet test --logger trx</c> (Visual Studio test results, also written by xUnit, NUnit and MSTest
/// adapters). Only reads files: SpecTrace never runs the tests.
/// </summary>
public sealed class TestResults
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private readonly Dictionary<string, List<TestRun>> _byMethod;

    private TestResults(IReadOnlyList<TestRun> tests, IReadOnlyList<string> files)
    {
        Tests = tests;
        Files = files;
        _byMethod = tests.GroupBy(t => t.Method, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public IReadOnlyList<TestRun> Tests { get; }

    /// <summary>The .trx files that were read, as given.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// Loads every .trx in <paramref name="paths"/>: files, or folders searched recursively (dotnet test writes them to
    /// TestResults/). Results of the same test in several files are combined: one failure is a failure.
    /// </summary>
    public static TestResults Load(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*.trx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(path)) files.Add(path);
            else throw new ArgumentException($"No test results at '{path}'. Run the tests with: dotnet test --logger trx");
        }

        if (files.Count == 0) throw new ArgumentException($"No .trx files in {string.Join(", ", paths)}. Run the tests with: dotnet test --logger trx");

        var runs = new List<TestRun>();
        foreach (var file in files)
        {
            try
            {
                runs.AddRange(Parse(XDocument.Load(file)));
            }
            catch (XmlException ex)
            {
                throw new ArgumentException($"'{file}' is not a valid .trx file: {ex.Message}");
            }
        }

        var combined = runs
            .GroupBy(r => (r.ClassName, r.Method))
            .Select(g => new TestRun(g.Key.ClassName, g.Key.Method, g.Min(r => r.Outcome), g.Sum(r => r.Runs),
                g.Where(r => r.Outcome == TestOutcome.Failed).Select(r => r.Message).FirstOrDefault(m => m != null)))
            .ToList();
        return new TestResults(combined, files);
    }

    public static IReadOnlyList<TestRun> Parse(XDocument trx)
    {
        // <TestDefinitions><UnitTest id="..."><TestMethod className="Ns.Class" name="Method" /></UnitTest>
        var methods = trx.Descendants(Ns + "UnitTest")
            .Select(u => (Id: (string?)u.Attribute("id"), Method: u.Element(Ns + "TestMethod")))
            .Where(u => u.Id != null && u.Method != null)
            .ToDictionary(u => u.Id!, u => (ClassName: (string?)u.Method!.Attribute("className") ?? "", Name: (string?)u.Method.Attribute("name") ?? ""),
                StringComparer.OrdinalIgnoreCase);

        // <Results><UnitTestResult testId="..." outcome="Passed|Failed|NotExecuted|..." /></Results>
        return trx.Descendants(Ns + "UnitTestResult")
            .Select(r =>
            {
                var id = (string?)r.Attribute("testId");
                if (id == null || !methods.TryGetValue(id, out var m)) return null;
                var message = r.Descendants(Ns + "Message").Select(x => x.Value.Trim()).FirstOrDefault(v => v.Length > 0);
                // Theories: the adapter can put the data row in the name ("Method(x: 1)"); the method is what counts.
                var name = m.Name.Contains('(') ? m.Name.Substring(0, m.Name.IndexOf('(')) : m.Name;
                return new TestRun(StripArity(m.ClassName), name, Outcome((string?)r.Attribute("outcome")), 1, message);
            })
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    /// <summary>
    /// The outcome of the test behind a [Verifies] mark ("Class.Method"): the worst of its runs, or NotRun if it is not
    /// in the results. When several classes share the name, the one in <paramref name="nameSpace"/> wins.
    /// </summary>
    public TestRun? Find(string symbol, string? nameSpace = null)
    {
        // [Verifies] on a test class: every test of the class.
        var dot = symbol.LastIndexOf('.');
        var type = dot <= 0 ? symbol : symbol.Substring(0, dot);
        var method = dot <= 0 ? null : symbol.Substring(dot + 1);
        var candidates = method == null ? Tests.ToList() : _byMethod.TryGetValue(method, out var byName) ? byName : new List<TestRun>();

        var matches = candidates.Where(t => SimpleName(t.ClassName) == type).ToList();
        if (matches.Count > 1 && nameSpace != null)
        {
            var exact = matches.Where(t => t.ClassName == nameSpace + "." + type).ToList();
            if (exact.Count > 0) matches = exact;
        }

        return matches.Count == 0 ? null : new TestRun(matches[0].ClassName, method ?? "*", matches.Min(t => t.Outcome), matches.Sum(t => t.Runs),
            matches.Select(t => t.Message).FirstOrDefault(m => m != null));
    }

    public TestOutcome OutcomeOf(string symbol, string? nameSpace = null) => Find(symbol, nameSpace)?.Outcome ?? TestOutcome.NotRun;

    private static TestOutcome Outcome(string? value) => value switch
    {
        "Passed" or "PassedButRunAborted" or "Warning" => TestOutcome.Passed,
        "Failed" or "Error" or "Timeout" or "Aborted" => TestOutcome.Failed,
        _ => TestOutcome.NotRun, // NotExecuted (skipped), Inconclusive, Pending, Disconnected...
    };

    // "Ns.Outer+Inner" (nested class) → "Inner"; "Ns.Class`1" (generic) → "Class".
    private static string SimpleName(string className) => className.Split('.', '+').Last();

    private static string StripArity(string className)
    {
        var tick = className.IndexOf('`');
        return tick < 0 ? className : className.Substring(0, tick);
    }
}
