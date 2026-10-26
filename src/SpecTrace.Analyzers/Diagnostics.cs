using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SpecTrace.Analyzers
{
    /// <summary>Keys of <see cref="Diagnostic.Properties"/> read by the code fixes.</summary>
    public static class DiagnosticProperties
    {
        /// <summary>Suggested values separated by ';' (similar IDs in ST0001, valid criteria in ST0002).</summary>
        public const string Suggestions = "SpecTrace.Suggestions";
    }

    /// <summary>Public diagnostic IDs for the code fixes and tools.</summary>
    public static class DiagnosticIds
    {
        public const string UnknownRequirement = "ST0001";
        public const string UnknownCriterion = "ST0002";
        public const string NotImplemented = "ST0003";
        public const string NotVerified = "ST0004";
        public const string CriterionNotVerified = "ST0005";
        public const string DeprecatedRequirement = "ST0006";
        public const string DuplicateInSpec = "ST0007";
        public const string SpecFormat = "ST0008";
        public const string NotConstant = "ST0009";
        public const string StatementChanged = "ST0010";
        public const string CriterionChanged = "ST0011";
    }

    internal static class Diagnostics
    {
        private const string Category = "SpecTrace";
        private const string HelpBase = "https://github.com/amondejar/spec-trace/blob/main/docs/rules/";

        private static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity,
            bool compilationEnd = false) =>
            new DiagnosticDescriptor(id, title, message, Category, severity, isEnabledByDefault: true,
                helpLinkUri: HelpBase + id + ".md",
                customTags: compilationEnd ? new[] { WellKnownDiagnosticTags.CompilationEnd } : new string[0]);

        public static readonly DiagnosticDescriptor UnknownRequirement = Rule("ST0001",
            "Unknown requirement",
            "Requirement '{0}' does not exist in any spec",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnknownCriterion = Rule("ST0002",
            "Unknown acceptance criterion",
            "Requirement '{0}' has no criterion '{1}'. Defined criteria: {2}",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor NotImplemented = Rule("ST0003",
            "Requirement not implemented",
            "Requirement '{0}' ({1}) is {2} but no type or member has [Implements(\"{0}\")]",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor NotVerified = Rule("ST0004",
            "Implemented requirement without tests",
            "Requirement '{0}' ({1}) is implemented in {2} but no test has [Verifies(\"{0}\")]",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor CriterionNotVerified = Rule("ST0005",
            "Acceptance criterion without a test",
            "Criterion {1} of requirement '{0}' has no test: {2}",
            DiagnosticSeverity.Info, compilationEnd: true);

        public static readonly DiagnosticDescriptor DeprecatedRequirement = Rule("ST0006",
            "Deprecated requirement",
            "Requirement '{0}' is deprecated in the spec; remove the reference or reactivate it",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor DuplicateInSpec = Rule("ST0007",
            "Duplicate ID in the specs",
            "{0}",
            DiagnosticSeverity.Error, compilationEnd: true);

        public static readonly DiagnosticDescriptor SpecFormat = Rule("ST0008",
            "Spec format issue",
            "{0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor StatementChanged = Rule("ST0010",
            "Statement changed after review",
            "The statement of '{0}' changed after the last review (spectrace.lock). Review {1} and, once it is up to date, run: dotnet spectrace lock --requirement {0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor CriterionChanged = Rule("ST0011",
            "Criterion changed after review",
            "Criterion {1} of '{0}' changed after the last review (spectrace.lock). Review {2} and, once it is up to date, run: dotnet spectrace lock --requirement {0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor NotConstant = Rule("ST0009",
            "Requirement ID is not a constant",
            "The requirement ID must be a constant (use Requirements.XXX or a string literal)",
            DiagnosticSeverity.Warning);
    }
}
