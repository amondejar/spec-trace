using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpecTrace.Specs;

namespace SpecTrace.Tool;

/// <summary>Generates xUnit test skeletons for acceptance criteria that have no test.</summary>
public static class Scaffolder
{
    public static IReadOnlyList<string> ScaffoldTests(TraceModel model, string testProjectDir, string? requirementId, string? ns)
    {
        var project = Directory.EnumerateFiles(testProjectDir, "*.csproj").FirstOrDefault()
            ?? throw new ArgumentException($"There is no .csproj in {testProjectDir}");
        var rootNs = ns ?? Path.GetFileNameWithoutExtension(project);
        var outDir = Path.Combine(testProjectDir, "Specs");
        var written = new List<string>();

        var targets = model.Requirements
            .Where(t => t.Requirement.IsEnforced)
            .Where(t => requirementId == null || string.Equals(t.Requirement.Id, requirementId, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.UncoveredCriteria.Any())
            .ToList();

        foreach (var t in targets)
        {
            var file = Path.Combine(outDir, TestSkeleton.FileName(t.Requirement));
            if (File.Exists(file)) continue;

            Directory.CreateDirectory(outDir);
            File.WriteAllText(file, TestSkeleton.Render(rootNs, t.Requirement, t.UncoveredCriteria.ToList()));
            written.Add(file);
        }

        return written;
    }
}
