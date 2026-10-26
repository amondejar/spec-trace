using System.Linq;
using SpecTrace.Specs;
using Xunit;

namespace SpecTrace.Tests
{
    public class SpecParserTests
    {
        [Fact]
        public void Parses_front_matter_requirements_statement_and_criteria()
        {
            const string text = @"---
spec: PAY
title: Pagos
---
# Pagos

## PAY-001: Pay by card
**Status:** approved

**EARS:** When the customer confirms the payment,
the system shall charge the card once.

- AC-1: Given a valid card, then the charge succeeds.
- [ ] AC-2: Given a declined card, then the order stays unpaid.

### Notas
Text that is not part of the requirement.
";
            var doc = SpecParser.Parse("pay.spec.md", text);

            Assert.Equal("PAY", doc.SpecId);
            Assert.Equal("Pagos", doc.Title);
            var req = Assert.Single(doc.Requirements);
            Assert.Equal("PAY-001", req.Id);
            Assert.Equal("Pay by card", req.Title);
            Assert.Equal(SpecStatus.Approved, req.Status);
            Assert.Equal("When the customer confirms the payment, the system shall charge the card once.", req.Statement);
            Assert.Equal(new[] { "AC-1", "AC-2" }, req.Criteria.Select(c => c.Id).ToArray());
            Assert.Equal(6, req.Line);
            Assert.Empty(doc.Issues);
        }

        [Fact]
        public void Requirement_ends_at_next_heading_of_same_or_higher_level()
        {
            const string text = "## A-1: One\nThe system shall one.\n\n- AC-1: x\n\n## Another section\n- AC-2: does not belong\n";

            var req = Assert.Single(SpecParser.Parse("a.spec.md", text).Requirements);

            Assert.Single(req.Criteria);
        }

        [Fact]
        public void Ignores_headings_inside_code_fences()
        {
            const string text = "```markdown\n## FAKE-1: No es real\n```\n## REAL-1: Real\nThe system shall work.\n\n- AC-1: ok\n";

            var req = Assert.Single(SpecParser.Parse("a.spec.md", text).Requirements);

            Assert.Equal("REAL-1", req.Id);
        }

        [Fact]
        public void Blockquotes_are_notes_not_statement()
        {
            const string text = "## A-1: One\nStatus: proposed\n\n> Check with the business.\n\nThe system shall one.\n\n- AC-1: x\n";

            var req = Assert.Single(SpecParser.Parse("a.spec.md", text).Requirements);

            Assert.Equal("The system shall one.", req.Statement);
        }

        [Fact]
        public void Reports_unknown_status_and_missing_criteria()
        {
            const string text = "## A-1: One\nStatus: wip\nThe system shall one.\n";

            var doc = SpecParser.Parse("a.spec.md", text);

            Assert.Contains(doc.Issues, i => i.Kind == SpecIssueKind.UnknownStatus);
            Assert.Contains(doc.Issues, i => i.Kind == SpecIssueKind.MissingCriteria);
        }

        [Fact]
        public void Catalog_detects_duplicates_across_files()
        {
            var a = SpecParser.Parse("a.spec.md", "## A-1: One\nThe system shall one.\n\n- AC-1: x\n");
            var b = SpecParser.Parse("b.spec.md", "## A-1: Otro\nThe system shall two.\n\n- AC-1: y\n");

            var catalog = SpecCatalog.Build(new[] { a, b });

            Assert.Single(catalog.Requirements);
            Assert.Contains(catalog.Issues, i => i.Kind == SpecIssueKind.DuplicateRequirement && i.FilePath == "b.spec.md");
        }

        [Theory]
        [InlineData("ORD-001", "ORD_001")]
        [InlineData("PAY-API-12", "PAY_API_12")]
        [InlineData("AC-1", "AC_1")]
        public void Ids_map_to_csharp_identifiers(string id, string expected)
        {
            Assert.Equal(expected, SpecNaming.ToIdentifier(id));
        }

        [Fact]
        public void Pascal_slug_removes_accents_and_punctuation()
        {
            Assert.Equal("AplicarCuponDeDescuento", SpecNaming.ToPascalSlug("Aplicar cupón de descuento"));
        }
    }
}
