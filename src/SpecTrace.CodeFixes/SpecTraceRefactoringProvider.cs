using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpecTrace.Specs;

namespace SpecTrace.CodeFixes
{
    /// <summary>
    /// Refactorings on [Implements] / [Verifies]:
    /// - "Use SpecTrace catalog constants": "ORD-001" → Requirements.ORD_001 (and "AC-1" → AcceptanceCriteria.ORD_001.AC_1).
    /// - "Generate tests for ORD-001 in X.Tests": creates Specs/ORD_001_...Tests.cs in each test project that references
    ///   this project, with one skipped test per criterion that has no [Verifies] yet.
    /// </summary>
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(SpecTraceRefactoringProvider)), Shared]
    public sealed class SpecTraceRefactoringProvider : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var attribute = root?.FindNode(context.Span).FirstAncestorOrSelf<AttributeSyntax>();
            if (attribute?.ArgumentList is not { Arguments.Count: > 0 } args) return;

            var kind = AttributeKind(attribute.Name);
            if (kind == null) return;

            var model = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (model == null) return;

            var requirementId = model.GetConstantValue(args.Arguments[0].Expression, context.CancellationToken).Value as string;
            if (requirementId == null) return;

            await OfferUseConstantsAsync(context, model, attribute, args, requirementId).ConfigureAwait(false);

            if (kind == "Implements")
            {
                await OfferGenerateTestsAsync(context, requirementId).ConfigureAwait(false);
            }
        }

        private static string? AttributeKind(NameSyntax name)
        {
            var simple = name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
                SimpleNameSyntax s => s.Identifier.Text,
                _ => string.Empty,
            };
            return simple switch
            {
                "Implements" or "ImplementsAttribute" => "Implements",
                "Verifies" or "VerifiesAttribute" => "Verifies",
                _ => null,
            };
        }

        // ---------- Literal → constant ----------

        private static Task OfferUseConstantsAsync(CodeRefactoringContext context, SemanticModel model,
            AttributeSyntax attribute, AttributeArgumentListSyntax args, string requirementId)
        {
            var catalog = FindCatalog(model.Compilation);
            if (catalog == null) return Task.CompletedTask;

            var reqField = catalog.Requirements.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.IsConst && Equals(f.ConstantValue, requirementId));
            if (reqField == null) return Task.CompletedTask;

            var replacements = new Dictionary<ExpressionSyntax, ExpressionSyntax>();
            var first = args.Arguments[0].Expression;
            if (first.IsKind(SyntaxKind.StringLiteralExpression))
            {
                replacements[first] = Access("Requirements", reqField.Name).WithTriviaFrom(first);
            }

            if (args.Arguments.Count > 1 && args.Arguments[1].Expression.IsKind(SyntaxKind.StringLiteralExpression)
                && catalog.Criteria?.GetTypeMembers(reqField.Name).FirstOrDefault() is { } group)
            {
                var literal = args.Arguments[1].Expression;
                var value = model.GetConstantValue(literal).Value as string;
                var critField = group.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.IsConst && string.Equals(f.ConstantValue as string, value, StringComparison.OrdinalIgnoreCase));
                if (critField != null)
                {
                    replacements[literal] = Access("AcceptanceCriteria", reqField.Name, critField.Name).WithTriviaFrom(literal);
                }
            }

            if (replacements.Count == 0) return Task.CompletedTask;

            var ns = catalog.Requirements.ContainingNamespace.ToDisplayString();
            context.RegisterRefactoring(CodeAction.Create(
                "Use SpecTrace catalog constants",
                async ct =>
                {
                    var root = (CompilationUnitSyntax)(await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false))!;
                    var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
                    newRoot = AddUsing(newRoot, ns, attribute);
                    return context.Document.WithSyntaxRoot(newRoot);
                },
                equivalenceKey: "SpecTrace.UseConstants"));
            return Task.CompletedTask;
        }

        private static ExpressionSyntax Access(params string[] parts)
        {
            ExpressionSyntax expr = SyntaxFactory.IdentifierName(parts[0]);
            foreach (var part in parts.Skip(1))
            {
                expr = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expr, SyntaxFactory.IdentifierName(part));
            }

            return expr;
        }

        private static CompilationUnitSyntax AddUsing(CompilationUnitSyntax root, string ns, AttributeSyntax original)
        {
            if (root.Usings.Any(u => u.Name?.ToString() == ns)) return root;

            // No using is needed when the code is already inside that namespace (or a child one).
            var containing = original.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
            if (containing.Any(n => n == ns || n.StartsWith(ns + ".", StringComparison.Ordinal))) return root;

            var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            var usings = root.Usings.Add(directive)
                .OrderBy(u => u.Name?.ToString(), StringComparer.Ordinal);
            return root.WithUsings(SyntaxFactory.List(usings));
        }

        private sealed class Catalog
        {
            public Catalog(INamedTypeSymbol requirements, INamedTypeSymbol? criteria)
            {
                Requirements = requirements;
                Criteria = criteria;
            }

            public INamedTypeSymbol Requirements { get; }
            public INamedTypeSymbol? Criteria { get; }
        }

        /// <summary>Finds the Requirements / AcceptanceCriteria classes generated by SpecTrace in this project.</summary>
        private static Catalog? FindCatalog(Compilation compilation)
        {
            var requirements = compilation.GetSymbolsWithName("Requirements", SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(IsSpecTraceGenerated);
            if (requirements == null) return null;

            var criteria = compilation.GetSymbolsWithName("AcceptanceCriteria", SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => IsSpecTraceGenerated(t) &&
                                     SymbolEqualityComparer.Default.Equals(t.ContainingNamespace, requirements.ContainingNamespace));
            return new Catalog(requirements, criteria);
        }

        private static bool IsSpecTraceGenerated(INamedTypeSymbol type) =>
            type.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "GeneratedCodeAttribute" &&
                a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value as string == "SpecTrace");

        // ---------- Generate tests ----------

        private static async Task OfferGenerateTestsAsync(CodeRefactoringContext context, string requirementId)
        {
            var project = context.Document.Project;
            var catalog = await LoadSpecsAsync(project, context.CancellationToken).ConfigureAwait(false);
            if (!catalog.TryGet(requirementId, out var requirement) || requirement.Criteria.Count == 0) return;

            var testProjects = project.Solution.Projects
                .Where(p => p.ProjectReferences.Any(r => r.ProjectId == project.Id) && IsTestProject(p))
                .ToList();

            foreach (var testProject in testProjects)
            {
                var fileName = TestSkeleton.FileName(requirement);
                if (testProject.Documents.Any(d => string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase))) continue;

                var covered = await CoveredCriteriaAsync(testProject, requirement.Id, context.CancellationToken).ConfigureAwait(false);
                var pending = requirement.Criteria.Where(c => !covered.Contains(c.Id)).ToList();
                if (pending.Count == 0) continue;

                var rootNs = testProject.DefaultNamespace ?? testProject.AssemblyName;
                var text = TestSkeleton.Render(rootNs, requirement, pending);
                var title = $"Generate tests for {requirement.Id} in {testProject.Name} ({pending.Count} criteri{(pending.Count == 1 ? "on" : "a")})";
                var testProjectId = testProject.Id;

                context.RegisterRefactoring(CodeAction.Create(
                    title,
                    ct =>
                    {
                        var target = context.Document.Project.Solution.GetProject(testProjectId)!;
                        var folder = target.FilePath != null ? System.IO.Path.GetDirectoryName(target.FilePath) : null;
                        var path = folder != null ? System.IO.Path.Combine(folder, "Specs", fileName) : null;
                        var doc = target.AddDocument(fileName, text, folders: new[] { "Specs" }, filePath: path);
                        return Task.FromResult(doc.Project.Solution);
                    },
                    equivalenceKey: "SpecTrace.GenerateTests:" + testProject.Name));
            }
        }

        private static bool IsTestProject(Project project)
        {
            var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
            if (options.TryGetValue("build_property.SpecTraceRole", out var role) && !string.IsNullOrEmpty(role))
            {
                return string.Equals(role, "Tests", StringComparison.OrdinalIgnoreCase);
            }

            return options.TryGetValue("build_property.IsTestProject", out var isTest)
                ? string.Equals(isTest, "true", StringComparison.OrdinalIgnoreCase)
                : project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<SpecCatalog> LoadSpecsAsync(Project project, CancellationToken ct)
        {
            var docs = new List<SpecDocument>();
            foreach (var additional in project.AdditionalDocuments)
            {
                var path = additional.FilePath ?? additional.Name;
                if (!SpecParser.IsSpecFile(path)) continue;
                var text = await additional.GetTextAsync(ct).ConfigureAwait(false);
                docs.Add(SpecParser.Parse(path, text.ToString()));
            }

            return SpecCatalog.Build(docs);
        }

        /// <summary>Criteria of the requirement that already have a non-skipped [Verifies] in the test project.</summary>
        private static async Task<HashSet<string>> CoveredCriteriaAsync(Project testProject, string requirementId, CancellationToken ct)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var compilation = await testProject.GetCompilationAsync(ct).ConfigureAwait(false);
            var verifies = compilation?.GetTypeByMetadataName("SpecTrace.VerifiesAttribute");
            if (compilation == null || verifies == null) return covered;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var method in (await tree.GetRootAsync(ct).ConfigureAwait(false)).DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(method, ct) is not { } symbol) continue;
                    var attributes = symbol.GetAttributes();
                    if (attributes.Any(a => a.NamedArguments.Any(n => n.Key == "Skip" && n.Value.Value is string s && s.Length > 0))) continue;

                    foreach (var a in attributes.Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, verifies)))
                    {
                        if (a.ConstructorArguments.Length > 1 &&
                            string.Equals(a.ConstructorArguments[0].Value as string, requirementId, StringComparison.OrdinalIgnoreCase) &&
                            a.ConstructorArguments[1].Value is string criterion)
                        {
                            covered.Add(criterion);
                        }
                    }
                }
            }

            return covered;
        }
    }
}
