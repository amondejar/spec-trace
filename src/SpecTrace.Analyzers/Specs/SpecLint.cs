using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpecTrace.Specs
{
    public enum SpecQualityKind
    {
        /// <summary>The statement is not a requirement ("shall"): ST0012.</summary>
        NotEars,

        /// <summary>A word that cannot be tested as written ("fast", "user-friendly", "etc."): ST0013.</summary>
        VagueTerm,

        /// <summary>A criterion without an observable outcome ("then ..."): ST0014.</summary>
        NoOutcome,
    }

    /// <summary>Something that makes a requirement hard to implement or to test as written.</summary>
    public sealed class SpecQualityIssue
    {
        public SpecQualityIssue(SpecQualityKind kind, string requirementId, string? criterionId, string term, string message, string filePath, int line)
        {
            Kind = kind;
            RequirementId = requirementId;
            CriterionId = criterionId;
            Term = term;
            Message = message;
            FilePath = filePath;
            Line = line;
        }

        public SpecQualityKind Kind { get; }
        public string RequirementId { get; }
        public string? CriterionId { get; }

        /// <summary>The word that triggered it (vague terms), or empty.</summary>
        public string Term { get; }

        public string Message { get; }
        public string FilePath { get; }

        /// <summary>Zero-based line.</summary>
        public int Line { get; }
    }

    /// <summary>
    /// Checks that need no AI: EARS statements, vague words, criteria with an outcome. They catch what most often
    /// turns into an argument after the code is written. Inconsistencies between the statement and the criteria need a
    /// reader: the review_spec MCP prompt asks an agent for that.
    /// </summary>
    public static class SpecLint
    {
        // English and Spanish. Words that hide a number, a limit or a decision someone has not made yet.
        private static readonly string[] VagueTerms =
        {
            "fast", "quick", "quickly", "slow", "easy", "easily", "simple", "simply", "user-friendly", "intuitive",
            "appropriate", "appropriately", "adequate", "adequately", "reasonable", "reasonably", "as needed", "as appropriate",
            "if possible", "where possible", "when possible", "etc", "and/or", "approximately", "flexible", "efficient",
            "efficiently", "robust", "seamless", "seamlessly", "optimal", "optimally", "normally", "usually", "typically",
            "several", "many", "few", "TBD", "TODO",
            "rápido", "rápida", "rápidamente", "lento", "fácil", "fácilmente", "sencillo", "sencilla", "intuitivo", "intuitiva",
            "adecuado", "adecuada", "adecuadamente", "apropiado", "apropiada", "razonable", "si es posible", "según sea necesario",
            "y/o", "aproximadamente", "eficiente", "robusto", "robusta", "óptimo", "óptima", "normalmente", "habitualmente",
            "varios", "varias", "muchos", "muchas", "pocos", "pocas",
        };

        private static readonly Regex Vague = new Regex(
            @"(?<![\p{L}\p{N}])(?<term>" + string.Join("|", VagueTerms.OrderByDescending(t => t.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // EARS uses "shall"; "must" and the Spanish "deberá/debe" are accepted as the same intent.
        private static readonly Regex Shall = new Regex(@"(?<![\p{L}])(shall|must|deber[áa]|debe)(?![\p{L}])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // An observable outcome: "then ...", "entonces ...", or an arrow.
        private static readonly Regex Outcome = new Regex(@"(?<![\p{L}])(then|entonces)(?![\p{L}])|→|->|=>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IReadOnlyList<SpecQualityIssue> Check(SpecRequirement r)
        {
            var issues = new List<SpecQualityIssue>();
            if (r.IsDeprecated || r.Statement.Length == 0) return issues;

            if (!Shall.IsMatch(r.Statement))
            {
                issues.Add(new SpecQualityIssue(SpecQualityKind.NotEars, r.Id, null, "",
                    $"The statement of '{r.Id}' does not say what the system shall do. Use EARS: When <trigger>, the system shall <response> " +
                    "(also While <state>, If <condition> then, Where <feature>)",
                    r.FilePath, r.Line));
            }

            foreach (var term in Terms(r.Statement))
            {
                issues.Add(new SpecQualityIssue(SpecQualityKind.VagueTerm, r.Id, null, term,
                    $"'{term}' in the statement of '{r.Id}' cannot be tested as written: say what it means (a number, a limit, a condition)",
                    r.FilePath, r.Line));
            }

            foreach (var c in r.Criteria)
            {
                foreach (var term in Terms(c.Text))
                {
                    issues.Add(new SpecQualityIssue(SpecQualityKind.VagueTerm, r.Id, c.Id, term,
                        $"'{term}' in {c.Id} of '{r.Id}' cannot be tested as written: say what it means (a number, a limit, a condition)",
                        r.FilePath, c.Line));
                }

                if (!Outcome.IsMatch(c.Text))
                {
                    issues.Add(new SpecQualityIssue(SpecQualityKind.NoOutcome, r.Id, c.Id, "",
                        $"{c.Id} of '{r.Id}' has no observable outcome. Write it as: Given <context>, when <action>, then <expected result>",
                        r.FilePath, c.Line));
                }
            }

            return issues;
        }

        public static IReadOnlyList<SpecQualityIssue> Check(IEnumerable<SpecRequirement> requirements) =>
            requirements.SelectMany(Check).ToList();

        private static IEnumerable<string> Terms(string text) =>
            Vague.Matches(text).Cast<Match>().Select(m => m.Groups["term"].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
