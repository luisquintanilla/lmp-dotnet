namespace LMP;

/// <summary>
/// Marks a method on an <see cref="LmpModule"/> subclass as a named skill.
/// The source generator (Pipeline 8) emits a <see cref="SkillManifest"/> entry
/// in <see cref="LmpModule.GetSkills()"/> for each <c>[Skill]</c>-annotated method.
/// </summary>
/// <remarks>
/// Skills are optimizable capabilities that can be selected, routed, and combined
/// by the <c>ContextualBandit</c> optimizer and constrained via the Z3 compatibility graph.
/// <code>
/// public partial class AssistantModule : LmpModule
/// {
///     [Skill(Description = "Searches the web for current information.")]
///     public Task&lt;string&gt; WebSearchAsync(string query) => ...;
///
///     [Skill(Name = "math", Description = "Evaluates arithmetic expressions.")]
///     public Task&lt;double&gt; EvaluateAsync(string expression) => ...;
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SkillAttribute : Attribute
{
    /// <summary>
    /// Optional skill name override. When <c>null</c>, the source generator uses the method name.
    /// The name is stable across optimizations — optimizers may evolve descriptions but never names.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional skill description. When <c>null</c>, the source generator uses the XML doc
    /// summary comment on the method. GEPA can evolve this description during optimization.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional tags for grouping and compatibility graph construction.
    /// Used by the Z3 compatibility graph to express skill compatibility constraints.
    /// </summary>
    public string[]? Tags { get; init; }
}
