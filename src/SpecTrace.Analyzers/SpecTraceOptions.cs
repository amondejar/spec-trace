using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SpecTrace.Specs;

namespace SpecTrace.Analyzers
{
    /// <summary>Options read from the MSBuild properties visible to the compiler (see build/SpecTrace.props).</summary>
    internal readonly struct SpecTraceOptions : IEquatable<SpecTraceOptions>
    {
        public SpecTraceOptions(string catalogNamespace, bool isTestProject)
        {
            CatalogNamespace = catalogNamespace;
            IsTestProject = isTestProject;
        }

        public string CatalogNamespace { get; }
        public bool IsTestProject { get; }

        public static SpecTraceOptions From(AnalyzerConfigOptions options)
        {
            options.TryGetValue("build_property.SpecTraceNamespace", out var ns);
            options.TryGetValue("build_property.RootNamespace", out var root);
            options.TryGetValue("build_property.IsTestProject", out var isTest);
            options.TryGetValue("build_property.SpecTraceRole", out var role);

            var catalogNamespace = !string.IsNullOrWhiteSpace(ns) ? ns!.Trim()
                : !string.IsNullOrWhiteSpace(root) ? root!.Trim() + ".Specs"
                : "SpecTrace.Generated";

            var test = string.Equals(role, "Tests", StringComparison.OrdinalIgnoreCase)
                       || (!string.Equals(role, "Implementation", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(isTest, "true", StringComparison.OrdinalIgnoreCase));

            return new SpecTraceOptions(catalogNamespace, test);
        }

        public bool Equals(SpecTraceOptions other) =>
            CatalogNamespace == other.CatalogNamespace && IsTestProject == other.IsTestProject;

        public override bool Equals(object? obj) => obj is SpecTraceOptions o && Equals(o);

        public override int GetHashCode() => (CatalogNamespace?.GetHashCode() ?? 0) * 31 + (IsTestProject ? 1 : 0);
    }

    internal static class SpecFiles
    {
        public static bool IsSpec(AdditionalText text) => SpecParser.IsSpecFile(text.Path);

        public static SpecDocument Parse(AdditionalText text, CancellationToken ct) =>
            SpecParser.Parse(text.Path, text.GetText(ct)?.ToString() ?? string.Empty);

        public static SpecCatalog LoadCatalog(ImmutableArray<AdditionalText> files, CancellationToken ct) =>
            SpecCatalog.Build(files.Where(IsSpec).Select(f => Parse(f, ct)));
    }
}
