using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SpecTrace.Specs;

namespace SpecTrace.Analyzers
{
    /// <summary>
    /// ST0015: in the namespaces an architecture rule covers, every name the code uses (namespaces in <c>using</c>,
    /// types, members) is resolved and compared with what the rule forbids. One diagnostic per rule and expression:
    /// <c>DateTime.Now.Date</c> is reported once.
    /// </summary>
    internal static class ArchitectureCheck
    {
        public static void Analyze(SemanticModelAnalysisContext ctx, ImmutableArray<ArchitectureRule> rules)
        {
            var model = ctx.SemanticModel;
            var root = model.SyntaxTree.GetRoot(ctx.CancellationToken);
            var fileNamespace = FileNamespace(root);
            var reported = new HashSet<(string Rule, int Chain)>();
            var found = new List<Diagnostic>();

            // From the end, so that in A.B.C the most specific name is the one reported (Microsoft.AspNetCore.Http rather
            // than Microsoft.AspNetCore).
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>().Reverse())
            {
                var codeNamespace = NamespaceOf(name) ?? fileNamespace;
                if (codeNamespace == null) continue;

                var applicable = rules.Where(r => r.AppliesTo(codeNamespace)).ToList();
                if (applicable.Count == 0) continue;

                // The namespace declaration itself is not a use.
                if (name.Ancestors().Any(a => a is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(name.Span))) continue;

                var symbol = Resolve(model, name, ctx.CancellationToken);
                var qualified = symbol == null ? null : QualifiedName(symbol);
                if (qualified == null) continue;

                foreach (var rule in applicable)
                {
                    var forbidden = rule.Forbids(qualified);
                    if (forbidden == null) continue;
                    var chain = ChainOf(name);
                    if (!reported.Add((rule.Id, chain.SpanStart))) continue;

                    found.Add(Diagnostic.Create(Diagnostics.ArchitectureViolation, chain.GetLocation(),
                        rule.Id, rule.Title, codeNamespace, qualified == forbidden ? forbidden : $"{qualified} ({forbidden})",
                        rule.UseInstead != null ? $". Use instead: {rule.UseInstead}" : ""));
                }
            }

            foreach (var d in found.OrderBy(d => d.Location.SourceSpan.Start)) ctx.ReportDiagnostic(d);
        }

        private static ISymbol? Resolve(SemanticModel model, SimpleNameSyntax name, System.Threading.CancellationToken ct)
        {
            var info = model.GetSymbolInfo(name, ct);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;
            return symbol?.OriginalDefinition;
        }

        /// <summary>"System.DateTime.Now", "Microsoft.AspNetCore.Http", or null for locals, parameters and the like.</summary>
        internal static string? QualifiedName(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamespaceSymbol ns:
                    return ns.IsGlobalNamespace ? null : ns.ToDisplayString();
                case INamedTypeSymbol type:
                    var container = type.ContainingType != null
                        ? QualifiedName(type.ContainingType)
                        : type.ContainingNamespace is { IsGlobalNamespace: false } ns2 ? ns2.ToDisplayString() : null;
                    return container == null ? type.Name : container + "." + type.Name;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor:
                    return QualifiedName(ctor.ContainingType);
                case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol when symbol.ContainingType != null:
                    return QualifiedName(symbol.ContainingType) + "." + symbol.Name;
                default:
                    return null;
            }
        }

        /// <summary>The outermost <c>A.B.C</c> the name is part of, to report a chain once.</summary>
        private static SyntaxNode ChainOf(SyntaxNode node)
        {
            while (node.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax or AliasQualifiedNameSyntax or
                   MemberBindingExpressionSyntax or ConditionalAccessExpressionSyntax)
            {
                node = node.Parent;
            }

            return node;
        }

        private static string? NamespaceOf(SyntaxNode node)
        {
            var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse().ToList();
            return parts.Count == 0 ? null : string.Join(".", parts);
        }

        /// <summary>For code outside any namespace block (usings at the top): the file's only namespace, if there is one.</summary>
        private static string? FileNamespace(SyntaxNode root)
        {
            var namespaces = root.DescendantNodes(n => n is CompilationUnitSyntax || n is BaseNamespaceDeclarationSyntax)
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Where(n => !n.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any())
                .ToList();
            return namespaces.Count == 1 ? namespaces[0].Name.ToString() : null;
        }
    }
}
