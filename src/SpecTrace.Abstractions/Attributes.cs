using System;

namespace SpecTrace
{
    /// <summary>
    /// Declara que el elemento anotado implementa un requisito de la spec.
    /// </summary>
    /// <example><code>[Implements(Requirements.ORD_001)]</code></example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor,
        AllowMultiple = true, Inherited = false)]
    public sealed class ImplementsAttribute : Attribute
    {
        public ImplementsAttribute(string requirementId) => RequirementId = requirementId;

        /// <summary>ID del requisito, por ejemplo <c>ORD-001</c>.</summary>
        public string RequirementId { get; }
    }

    /// <summary>
    /// Declara que un test verifica un requisito y, opcionalmente, uno de sus criterios de aceptación.
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

        /// <summary>ID del requisito, por ejemplo <c>ORD-001</c>.</summary>
        public string RequirementId { get; }

        /// <summary>ID del criterio de aceptación, por ejemplo <c>AC-1</c>. Nulo = el requisito en conjunto.</summary>
        public string? Criterion { get; }
    }

    /// <summary>
    /// Lo emite el generador de SpecTrace en cada ensamblado de implementación con los requisitos que implementa.
    /// Los proyectos de test lo leen de sus referencias para saber qué falta verificar. No lo uses a mano.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SpecTraceImplementedAttribute : Attribute
    {
        public SpecTraceImplementedAttribute(string requirementId) => RequirementId = requirementId;

        public string RequirementId { get; }
    }
}
