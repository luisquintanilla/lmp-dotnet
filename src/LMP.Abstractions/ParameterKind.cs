namespace LMP;

/// <summary>
/// Discriminated union describing the type of a single optimization parameter.
/// </summary>
/// <remarks>
/// The six kinds map to the five axes of optimization:
/// <list type="table">
/// <listheader><term>Kind</term><description>Typical use</description></listheader>
/// <item><term><see cref="Categorical"/></term><description>Demo-set index, instruction index, model tier</description></item>
/// <item><term><see cref="Integer"/></term><description>Max-demos count, beam width</description></item>
/// <item><term><see cref="Continuous"/></term><description>Temperature, top-p, cost weight</description></item>
/// <item><term><see cref="StringValued"/></term><description>Instruction text (evolved by GEPA/SIMBA)</description></item>
/// <item><term><see cref="Subset"/></term><description>Tool pool, model list, skill manifest</description></item>
/// <item><term><see cref="Composite"/></term><description>Grouped sub-spaces per predictor</description></item>
/// </list>
/// </remarks>
public abstract record ParameterKind;

/// <summary>
/// A categorical parameter with a fixed number of discrete choices.
/// </summary>
/// <param name="Count">Number of distinct category values (indices 0 … Count−1).</param>
public sealed record Categorical(int Count) : ParameterKind;

/// <summary>
/// An integer-valued parameter in a closed range.
/// </summary>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
public sealed record Integer(int Min, int Max) : ParameterKind;

/// <summary>
/// A real-valued parameter in a closed range, optionally on a log scale.
/// </summary>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Scale">
/// Search scale. Use <see cref="Scale.Log"/> for parameters spanning orders of magnitude
/// (e.g. learning rate, temperature).
/// </param>
public sealed record Continuous(double Min, double Max, Scale Scale = Scale.Linear) : ParameterKind;

/// <summary>
/// A free-form string parameter. Used for instruction text evolved by GEPA and SIMBA.
/// </summary>
/// <param name="InitialValue">
/// Optional initial instruction text. When set, optimizers use this as the seed value
/// rather than an empty string.
/// </param>
public sealed record StringValued(string? InitialValue = null) : ParameterKind;

/// <summary>
/// A subset selection from a finite pool of objects.
/// Used for tool pools (<see cref="Microsoft.Extensions.AI.AITool"/> lists),
/// model name lists, and skill manifests.
/// </summary>
/// <param name="Pool">
/// All candidate objects. Pool items are typically
/// <see cref="Microsoft.Extensions.AI.AITool"/>, <c>string</c> (model names),
/// <see cref="Example"/> (demonstrations), or any arbitrary object.
/// </param>
/// <param name="MinSize">Minimum number of items in a valid subset. Default is 1.</param>
/// <param name="MaxSize">
/// Maximum number of items in a valid subset.
/// −1 (default) means no upper bound (all pool items are selected).
/// </param>
public sealed record Subset(
    IReadOnlyList<object> Pool,
    int MinSize = 1,
    int MaxSize = -1) : ParameterKind;

/// <summary>
/// A composite parameter that groups a nested <see cref="TypedParameterSpace"/>.
/// Allows representing per-predictor sub-spaces.
/// </summary>
/// <param name="Members">The nested parameter space.</param>
public sealed record Composite(TypedParameterSpace Members) : ParameterKind;

/// <summary>
/// Search scale for <see cref="Continuous"/> parameters.
/// </summary>
public enum Scale
{
    /// <summary>Values are sampled uniformly on a linear scale.</summary>
    Linear,

    /// <summary>
    /// Values are sampled uniformly on a log scale (i.e., log-uniform).
    /// Suitable for parameters that span orders of magnitude such as temperature,
    /// learning rate, or regularization coefficients.
    /// </summary>
    Log
}
