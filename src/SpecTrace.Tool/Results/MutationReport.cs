using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpecTrace.Tool.Results;

public enum MutantStatus
{
    Killed,
    Survived,
    NoCoverage,
    Timeout,
    CompileError,
    RuntimeError,
    Ignored,
    Pending,
}

/// <summary>A change made to the code by the mutation tool, and whether any test noticed it.</summary>
/// <param name="File">File as written in the report (relative, or absolute in some tools).</param>
/// <param name="StartLine">1-based.</param>
/// <param name="EndLine">1-based.</param>
/// <param name="KilledBy">Names of the tests that failed with this mutant (when the tool reports them).</param>
/// <param name="CoveredBy">Names of the tests that ran the mutated code (when the tool reports them).</param>
public sealed record Mutant(string Id, string File, int StartLine, int EndLine, MutantStatus Status, string MutatorName, string? Replacement,
    IReadOnlyList<string> KilledBy, IReadOnlyList<string> CoveredBy)
{
    /// <summary>Counts for the score: killed or timed out (detected), survived or not covered (not detected).</summary>
    public bool Detected => Status is MutantStatus.Killed or MutantStatus.Timeout;

    public bool Undetected => Status is MutantStatus.Survived or MutantStatus.NoCoverage;
}

/// <summary>
/// A mutation testing report in the common format of Stryker (Stryker.NET: <c>dotnet stryker --reporter json</c>,
/// written to StrykerOutput/&lt;date&gt;/reports/mutation-report.json). Only read: SpecTrace never mutates or runs code.
/// </summary>
public sealed class MutationReport
{
    public const string FileName = "mutation-report.json";

    private MutationReport(string path, string? projectRoot, IReadOnlyList<Mutant> mutants)
    {
        Path = path;
        ProjectRoot = projectRoot;
        Mutants = mutants;
    }

    /// <summary>The report file that was read.</summary>
    public string Path { get; }

    public string? ProjectRoot { get; }

    public IReadOnlyList<Mutant> Mutants { get; }

    /// <summary>
    /// Every test that detects a mutant is listed, not only the first one. Stryker stops a mutant's run at the first
    /// failing test unless it runs with --disable-bail; a mutant killed by two or more tests shows that it did not stop.
    /// </summary>
    public bool ListsEveryKiller => Mutants.Any(m => m.KilledBy.Count > 1);

    /// <summary>A report file, or the newest mutation-report.json under a folder (StrykerOutput keeps one per run).</summary>
    public static MutationReport Load(string path)
    {
        var file = File.Exists(path) ? path : Newest(path)
            ?? throw new ArgumentException($"No {FileName} at '{path}'. Run Stryker.NET with the json reporter: dotnet stryker --reporter json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return Parse(doc.RootElement, file);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"'{file}' is not a valid mutation report: {ex.Message}");
        }
    }

    /// <summary>The newest report under <paramref name="folder"/>, or null.</summary>
    public static string? Newest(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, FileName, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    public static MutationReport Parse(JsonElement root, string path = "")
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"'{path}' is not a mutation testing report (no \"files\").");
        }

        // Test ids → names, from "testFiles" (by file path or class name). Without it, ids are used as names.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("testFiles", out var testFiles) && testFiles.ValueKind == JsonValueKind.Object)
        {
            foreach (var testFile in testFiles.EnumerateObject())
            {
                if (!testFile.Value.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
                foreach (var test in tests.EnumerateArray())
                {
                    var id = Text(test, "id");
                    var name = Text(test, "name");
                    if (id != null && name != null) names[id] = name;
                }
            }
        }

        IReadOnlyList<string> Tests(JsonElement mutant, string property) =>
            mutant.TryGetProperty(property, out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(i => i.GetString()).Where(i => i != null).Select(i => names.TryGetValue(i!, out var n) ? n : i!).ToList()
                : Array.Empty<string>();

        var mutants = new List<Mutant>();
        foreach (var file in files.EnumerateObject())
        {
            if (!file.Value.TryGetProperty("mutants", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var m in list.EnumerateArray())
            {
                if (!m.TryGetProperty("location", out var location) ||
                    !Enum.TryParse<MutantStatus>(Text(m, "status") ?? "", ignoreCase: true, out var status))
                {
                    continue;
                }

                var start = Line(location, "start");
                var end = Line(location, "end") ?? start;
                if (start == null) continue;

                mutants.Add(new Mutant(Text(m, "id") ?? "", file.Name, start.Value, end!.Value, status, Text(m, "mutatorName") ?? "?",
                    Text(m, "replacement"), Tests(m, "killedBy"), Tests(m, "coveredBy")));
            }
        }

        return new MutationReport(path, Text(root, "projectRoot"), mutants);
    }

    /// <summary>
    /// The report's file that is <paramref name="relativeFile"/> (relative to the SpecTrace root): equal after normalizing
    /// separators, or the longer path ends with the shorter one at a folder boundary.
    /// </summary>
    public static bool SameFile(string reportFile, string relativeFile)
    {
        var a = Normalize(reportFile);
        var b = Normalize(relativeFile);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var (longer, shorter) = a.Length >= b.Length ? (a, b) : (b, a);
        return longer.EndsWith("/" + shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A test name from the report ("Ns.Class.Method(x: 1)") is the test behind a [Verifies] mark ("Class.Method").</summary>
    public static bool SameTest(string reportName, string symbol)
    {
        var name = reportName.Contains('(') ? reportName.Substring(0, reportName.IndexOf('(')) : reportName;
        name = name.Trim().Replace('+', '.');
        if (string.Equals(name, symbol, StringComparison.Ordinal) || name.EndsWith("." + symbol, StringComparison.Ordinal)) return true;
        // [Verifies] on a test class: every test of the class.
        return !symbol.Contains('.') && (name.Contains("." + symbol + ".", StringComparison.Ordinal) || name.StartsWith(symbol + ".", StringComparison.Ordinal));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Line(JsonElement location, string edge) =>
        location.TryGetProperty(edge, out var p) && p.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : null;
}
