namespace LMP;

/// <summary>
/// Marks a method on an <see cref="LmpModule"/> subclass as a callable tool.
/// The source generator (Pipeline 7) wraps the method as an
/// <see cref="Microsoft.Extensions.AI.AIFunction"/> and registers it as a
/// <see cref="Subset"/> parameter in the module's <see cref="TypedParameterSpace"/>.
/// </summary>
/// <remarks>
/// The attribute enables optimizer-driven tool selection:
/// <code>
/// public partial class MyModule : LmpModule
/// {
///     [Tool]
///     public string Search(string query) => ...;
///
///     [Tool(Name = "calculator", Description = "Evaluates math expressions.")]
///     public double Evaluate(string expr) => ...;
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>
    /// Optional tool name override. When <c>null</c>, the source generator uses the
    /// method name (converted to camelCase by default). Optimizers may evolve the
    /// description but never change the name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional tool description. When <c>null</c>, the source generator uses the
    /// XML doc summary comment on the method. GEPA can evolve this description.
    /// </summary>
    public string? Description { get; init; }
}
