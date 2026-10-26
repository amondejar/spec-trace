using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SpecTrace.Analyzers
{
    /// <summary>Claves de <see cref="Diagnostic.Properties"/> que leen los code fixes.</summary>
    public static class DiagnosticProperties
    {
        /// <summary>Valores sugeridos separados por ';' (IDs parecidos en ST0001, criterios válidos en ST0002).</summary>
        public const string Suggestions = "SpecTrace.Suggestions";
    }

    /// <summary>IDs de diagnóstico públicos para los code fixes y las herramientas.</summary>
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
            "Requisito desconocido",
            "El requisito '{0}' no existe en ninguna spec",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnknownCriterion = Rule("ST0002",
            "Criterio de aceptación desconocido",
            "El requisito '{0}' no tiene el criterio '{1}'. Criterios definidos: {2}",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor NotImplemented = Rule("ST0003",
            "Requisito sin implementar",
            "El requisito '{0}' ({1}) está {2} pero ningún tipo o miembro tiene [Implements(\"{0}\")]",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor NotVerified = Rule("ST0004",
            "Requisito implementado sin test",
            "El requisito '{0}' ({1}) está implementado en {2} pero ningún test tiene [Verifies(\"{0}\")]",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor CriterionNotVerified = Rule("ST0005",
            "Criterio de aceptación sin test",
            "El criterio {1} del requisito '{0}' no tiene ningún test: {2}",
            DiagnosticSeverity.Info, compilationEnd: true);

        public static readonly DiagnosticDescriptor DeprecatedRequirement = Rule("ST0006",
            "Requisito obsoleto",
            "El requisito '{0}' está marcado como deprecated en la spec; retira la referencia o reactívalo",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor DuplicateInSpec = Rule("ST0007",
            "ID duplicado en las specs",
            "{0}",
            DiagnosticSeverity.Error, compilationEnd: true);

        public static readonly DiagnosticDescriptor SpecFormat = Rule("ST0008",
            "Problema de formato en la spec",
            "{0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor StatementChanged = Rule("ST0010",
            "El enunciado cambió después de revisarse",
            "El enunciado de '{0}' cambió después de la última revisión (spectrace.lock). Revisa {1} y, cuando esté al día, ejecuta: dotnet spectrace lock --requirement {0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor CriterionChanged = Rule("ST0011",
            "El criterio cambió después de revisarse",
            "El criterio {1} de '{0}' cambió después de la última revisión (spectrace.lock). Revisa {2} y, cuando esté al día, ejecuta: dotnet spectrace lock --requirement {0}",
            DiagnosticSeverity.Warning, compilationEnd: true);

        public static readonly DiagnosticDescriptor NotConstant = Rule("ST0009",
            "ID de requisito no constante",
            "El ID del requisito debe ser una constante (usa Requirements.XXX o un literal)",
            DiagnosticSeverity.Warning);
    }
}
