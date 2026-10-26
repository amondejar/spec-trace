using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SpecTrace.Analyzers;
using SpecTrace.CodeFixes;

namespace SpecTrace.Tests
{
    /// <summary>
    /// In-memory solution (AdhocWorkspace) with an implementation project and, optionally, a test project,
    /// as the IDE would see it: specs as AdditionalDocuments, the generated catalog and a .globalconfig with the
    /// MSBuild properties.
    /// </summary>
    internal sealed class WorkspaceHarness : IDisposable
    {
        public const string SpecPath = "/repo/specs/orders.spec.md";

        private readonly AdhocWorkspace _workspace = new();

        public WorkspaceHarness(string spec, string appSource, string? testSource = null)
        {
            var solution = _workspace.CurrentSolution;
            AppId = ProjectId.CreateNewId("App");
            solution = AddProject(solution, AppId, "App", "/repo/src/App", spec, appSource, isTest: false);

            if (testSource != null)
            {
                TestsId = ProjectId.CreateNewId("App.Tests");
                solution = AddProject(solution, TestsId, "App.Tests", "/repo/tests/App.Tests", spec, testSource, isTest: true)
                    .AddProjectReference(TestsId, new ProjectReference(AppId));
            }

            if (!_workspace.TryApplyChanges(solution)) throw new InvalidOperationException("Could not create the solution.");
        }

        public ProjectId AppId { get; }
        public ProjectId? TestsId { get; }
        public Solution Solution => _workspace.CurrentSolution;

        public Document Code(ProjectId project) =>
            Solution.GetProject(project)!.Documents.Single(d => d.Name == "Code.cs");

        public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Project project)
        {
            var compilation = (await project.GetCompilationAsync())!;
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SpecTraceAnalyzer()), project.AnalyzerOptions)
                .GetAnalyzerDiagnosticsAsync();
        }

        public static async Task<IReadOnlyList<CodeAction>> FixesAsync(Document document, Diagnostic diagnostic)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
            await new SpecTraceCodeFixProvider().RegisterCodeFixesAsync(context);
            return actions;
        }

        public static async Task<IReadOnlyList<CodeAction>> RefactoringsAsync(Document document, string atText)
        {
            var text = (await document.GetTextAsync()).ToString();
            var index = text.IndexOf(atText, StringComparison.Ordinal);
            if (index < 0) throw new ArgumentException($"'{atText}' does not appear in the document");

            var actions = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, new TextSpan(index, 0), a => actions.Add(a), CancellationToken.None);
            await new SpecTraceRefactoringProvider().ComputeRefactoringsAsync(context);
            return actions;
        }

        public static async Task<Solution> ApplyAsync(CodeAction action)
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        }

        public static async Task<string> TextAsync(Solution solution, DocumentId id) =>
            (await solution.GetDocument(id)!.GetTextAsync()).ToString();

        public void Dispose() => _workspace.Dispose();

        private static Solution AddProject(Solution solution, ProjectId id, string name, string dir, string spec, string source, bool isTest)
        {
            // The catalog and assembly attributes come from the real generator, as in a build.
            var generated = TestHarness.Run(source, spec, isTestProject: isTest, assemblyName: name, rootNamespace: name).Generated;

            var info = ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                    filePath: $"{dir}/{name}.csproj",
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                    parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                    metadataReferences: TestHarness.References)
                .WithDefaultNamespace(name);

            solution = solution
                .AddProject(info)
                .AddDocument(DocumentId.CreateNewId(id), "Code.cs", SourceText.From(source), filePath: $"{dir}/Code.cs");

            foreach (var g in generated)
            {
                solution = solution.AddDocument(DocumentId.CreateNewId(id), g.HintName, g.SourceText, filePath: $"{dir}/obj/{g.HintName}");
            }

            return solution
                .AddAdditionalDocument(DocumentId.CreateNewId(id), "orders.spec.md", SourceText.From(spec), filePath: SpecPath)
                .AddAnalyzerConfigDocument(DocumentId.CreateNewId(id), "msbuild.globalconfig",
                    SourceText.From($"is_global = true\nbuild_property.RootNamespace = {name}\nbuild_property.IsTestProject = {(isTest ? "true" : "false")}\n"),
                    filePath: $"{dir}/obj/msbuild.globalconfig");
        }
    }
}
