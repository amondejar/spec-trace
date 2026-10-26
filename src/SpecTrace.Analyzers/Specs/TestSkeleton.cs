using System.Collections.Generic;
using System.Security;
using System.Text;

namespace SpecTrace.Specs
{
    /// <summary>
    /// Plantilla de tests xUnit para los criterios de un requisito. La comparten la CLI
    /// (<c>dotnet spectrace scaffold-tests</c>) y la refactorización del IDE "Generar tests".
    /// </summary>
    public static class TestSkeleton
    {
        public const string SkipReason = "SpecTrace: pendiente de implementar";

        public static string ClassName(SpecRequirement requirement) =>
            $"{requirement.Identifier}_{SpecNaming.ToPascalSlug(requirement.Title)}Tests";

        public static string FileName(SpecRequirement requirement) => ClassName(requirement) + ".cs";

        /// <param name="rootNamespace">Namespace raíz del proyecto de tests; el catálogo vive en <c>{rootNamespace}.Specs</c>.</param>
        public static string Render(string rootNamespace, SpecRequirement r, IReadOnlyList<SpecCriterion> criteria)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using SpecTrace;");
            sb.AppendLine("using Xunit;");
            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}.Specs;");
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{r.Id}: {Xml(r.Title)}</summary>");
            if (r.Statement.Length > 0) sb.AppendLine($"/// <remarks>{Xml(r.Statement)}</remarks>");
            sb.AppendLine($"public class {ClassName(r)}");
            sb.AppendLine("{");

            for (var i = 0; i < criteria.Count; i++)
            {
                var c = criteria[i];
                var cid = SpecNaming.ToIdentifier(c.Id);
                sb.AppendLine($"    /// <summary>{c.Id}: {Xml(c.Text)}</summary>");
                sb.AppendLine($"    [Fact(Skip = \"{SkipReason}\")]");
                sb.AppendLine($"    [Verifies(Requirements.{r.Identifier}, AcceptanceCriteria.{r.Identifier}.{cid})]");
                sb.AppendLine($"    public void {cid}_{SpecNaming.ToPascalSlug(c.Text, 8)}()");
                sb.AppendLine("    {");
                sb.AppendLine("        // Arrange");
                sb.AppendLine();
                sb.AppendLine("        // Act");
                sb.AppendLine();
                sb.AppendLine("        // Assert");
                sb.AppendLine("        throw new NotImplementedException();");
                sb.AppendLine("    }");
                if (i < criteria.Count - 1) sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Xml(string text) => SecurityElement.Escape(text) ?? string.Empty;
    }
}
