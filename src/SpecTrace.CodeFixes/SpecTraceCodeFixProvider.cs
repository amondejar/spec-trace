using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpecTrace.Analyzers;

namespace SpecTrace.CodeFixes
{
    /// <summary>
    /// ST0001: cambiar a un ID existente parecido. ST0002: cambiar a un criterio válido.
    /// ST0006: quitar la referencia al requisito obsoleto.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SpecTraceCodeFixProvider)), Shared]
    public sealed class SpecTraceCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
            DiagnosticIds.UnknownRequirement,
            DiagnosticIds.UnknownCriterion,
            DiagnosticIds.DeprecatedRequirement);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                if (diagnostic.Id == DiagnosticIds.DeprecatedRequirement)
                {
                    var attribute = node.FirstAncestorOrSelf<AttributeSyntax>();
                    if (attribute == null) continue;
                    context.RegisterCodeFix(CodeAction.Create(
                        "Quitar la referencia al requisito obsoleto",
                        ct => RemoveAttributeAsync(context.Document, attribute, ct),
                        equivalenceKey: DiagnosticIds.DeprecatedRequirement),
                        diagnostic);
                    continue;
                }

                var argument = node.FirstAncestorOrSelf<AttributeArgumentSyntax>();
                if (argument == null || !argument.Expression.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                if (!diagnostic.Properties.TryGetValue(DiagnosticProperties.Suggestions, out var raw) || string.IsNullOrEmpty(raw)) continue;

                foreach (var suggestion in raw!.Split(';').Where(s => s.Length > 0))
                {
                    context.RegisterCodeFix(CodeAction.Create(
                        $"Cambiar a '{suggestion}'",
                        ct => ReplaceLiteralAsync(context.Document, argument.Expression, suggestion, ct),
                        equivalenceKey: diagnostic.Id + ":" + suggestion),
                        diagnostic);
                }
            }
        }

        private static async Task<Document> ReplaceLiteralAsync(Document document, ExpressionSyntax literal, string value, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
                .WithTriviaFrom(literal);
            return document.WithSyntaxRoot(root!.ReplaceNode(literal, replacement));
        }

        private static async Task<Document> RemoveAttributeAsync(Document document, AttributeSyntax attribute, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var list = (AttributeListSyntax)attribute.Parent!;
            if (list.Attributes.Count > 1)
            {
                return document.WithSyntaxRoot(root!.RemoveNode(attribute, SyntaxRemoveOptions.KeepNoTrivia)!);
            }

            // Se quita la lista entera; si era lo primero del miembro, su trivia inicial
            // (comentarios XML, indentación) pasa a lo que venga después.
            var parent = list.Parent!;
            var wasFirst = parent.GetFirstToken() == list.GetFirstToken();
            var newParent = parent.RemoveNode(list, SyntaxRemoveOptions.KeepNoTrivia)!;
            if (wasFirst) newParent = newParent.WithLeadingTrivia(parent.GetLeadingTrivia());
            return document.WithSyntaxRoot(root!.ReplaceNode(parent, newParent));
        }
    }
}
