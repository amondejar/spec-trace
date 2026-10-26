using System;

namespace SpecTrace
{
    /// <summary>
    /// Declares that the annotated element implements a requirement from the spec.
    /// </summary>
    /// <example><code>[Implements(Requirements.ORD_001)]</code></example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor,
        AllowMultiple = true, Inherited = false)]
    public sealed class ImplementsAttribute : Attribute
    {
        public ImplementsAttribute(string requirementId) => RequirementId = requirementId;

        /// <summary>Requirement ID, for example <c>ORD-001</c>.</summary>
        public string RequirementId { get; }
    }

    /// <summary>
    /// Declares that a test verifies a requirement and, optionally, one of its acceptance criteria.
    /// </summary>
    /// <example><code>[Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)]</code></example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class VerifiesAttribute : Attribute
    {
        public VerifiesAttribute(string requirementId, string? criterion = null)
        {
            RequirementId = requirementId;
            Criterion = criterion;
        }

        /// <summary>Requirement ID, for example <c>ORD-001</c>.</summary>
        public string RequirementId { get; }

        /// <summary>Acceptance criterion ID, for example <c>AC-1</c>. Null means the requirement as a whole.</summary>
        public string? Criterion { get; }
    }

    /// <summary>
    /// Emitted by the SpecTrace generator in each implementation assembly, listing the requirements it implements.
    /// Test projects read it from their references to know what still has to be verified. Do not use it by hand.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SpecTraceImplementedAttribute : Attribute
    {
        public SpecTraceImplementedAttribute(string requirementId) => RequirementId = requirementId;

        public string RequirementId { get; }
    }
}
