using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SpecTrace.Analyzers;

namespace SpecTrace.Tests
{
    /// <summary>
    /// Compila código en memoria, ejecuta el generador y el analizador de SpecTrace y devuelve el resultado.
    /// </summary>
    internal static class TestHarness
    {
        private static readonly Lazy<MetadataReference[]> FrameworkReferences = new(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) ||
                        Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

        public static MetadataReference Abstractions { get; } =
            MetadataReference.CreateFromFile(typeof(ImplementsAttribute).Assembly.Location);

        /// <summary>Framework + SpecTrace.Abstractions.</summary>
        public static IEnumerable<MetadataReference> References => FrameworkReferences.Value.Append(Abstractions);

        public sealed class Result
        {
            public Result(Compilation compilation, ImmutableArray<Diagnostic> compilerDiagnostics,
                ImmutableArray<Diagnostic> analyzerDiagnostics, ImmutableArray<GeneratedSourceResult> generated)
            {
                Compilation = compilation;
                CompilerDiagnostics = compilerDiagnostics;
                AnalyzerDiagnostics = analyzerDiagnostics;
                Generated = generated;
            }

            public Compilation Compilation { get; }
            public ImmutableArray<Diagnostic> CompilerDiagnostics { get; }
            public ImmutableArray<Diagnostic> AnalyzerDiagnostics { get; }
            public ImmutableArray<GeneratedSourceResult> Generated { get; }

            public IEnumerable<Diagnostic> SpecTrace(string? id = null) =>
                AnalyzerDiagnostics.Where(d => d.Id.StartsWith("ST", StringComparison.Ordinal) && (id == null || d.Id == id));

            public string GeneratedText(string hintName) =>
                Generated.Single(g => g.HintName == hintName).SourceText.ToString();

            /// <summary>Emite el ensamblado para usarlo como referencia de otro proyecto (p. ej. el de tests).</summary>
            public MetadataReference ToReference()
            {
                using var ms = new MemoryStream();
                var emit = Compilation.Emit(ms);
                if (!emit.Success)
                {
                    throw new InvalidOperationException("La compilación falló: " +
                        string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                }

                return MetadataReference.CreateFromImage(ms.ToArray());
            }
        }

        public static Result Run(
            string source,
            string spec,
            bool isTestProject = false,
            string assemblyName = "App",
            string rootNamespace = "App",
            params MetadataReference[] extraReferences) =>
            RunCore(source, spec, null, isTestProject, assemblyName, rootNamespace, extraReferences);

        /// <summary>Como <see cref="Run"/>, con un spectrace.lock junto a las specs.</summary>
        public static Result RunWithLock(
            string source,
            string spec,
            string lockText,
            bool isTestProject = false,
            string assemblyName = "App",
            string rootNamespace = "App",
            params MetadataReference[] extraReferences) =>
            RunCore(source, spec, lockText, isTestProject, assemblyName, rootNamespace, extraReferences);

        private static Result RunCore(string source, string spec, string? lockText, bool isTestProject,
            string assemblyName, string rootNamespace, MetadataReference[] extraReferences)
        {
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Code.cs");
            var compilation = CSharpCompilation.Create(assemblyName, new[] { tree },
                FrameworkReferences.Value.Append(Abstractions).Concat(extraReferences),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            var additional = lockText == null
                ? ImmutableArray.Create<AdditionalText>(new InMemoryText("/repo/specs/test.spec.md", spec))
                : ImmutableArray.Create<AdditionalText>(new InMemoryText("/repo/specs/test.spec.md", spec), new InMemoryText("/repo/spectrace.lock", lockText));
            var options = new OptionsProvider(new Dictionary<string, string>
            {
                ["build_property.RootNamespace"] = rootNamespace,
                ["build_property.IsTestProject"] = isTestProject ? "true" : "false",
            });

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new RequirementsGenerator().AsSourceGenerator() },
                additional, (CSharpParseOptions)tree.Options, options);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
            var generated = driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();

            var withAnalyzers = updated.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new SpecTraceAnalyzer()),
                new AnalyzerOptions(additional, options));
            var analyzerDiagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();

            return new Result(updated, updated.GetDiagnostics(), analyzerDiagnostics, generated);
        }

        private sealed class InMemoryText : AdditionalText
        {
            private readonly SourceText _text;

            public InMemoryText(string path, string text)
            {
                Path = path;
                _text = SourceText.From(text);
            }

            public override string Path { get; }

            public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
        }

        private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly Options _global;

            public OptionsProvider(Dictionary<string, string> values) => _global = new Options(values);

            public override AnalyzerConfigOptions GlobalOptions => _global;

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

            private sealed class Options : AnalyzerConfigOptions
            {
                public static readonly Options Empty = new(new Dictionary<string, string>());
                private readonly Dictionary<string, string> _values;

                public Options(Dictionary<string, string> values) => _values = values;

                public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
            }
        }
    }
}
