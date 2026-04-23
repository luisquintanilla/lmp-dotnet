using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Extension methods for registering <see cref="AITool"/> pools in optimization contexts.
/// </summary>
/// <remarks>
/// <para>
/// A tool pool is an ordered list of <see cref="AITool"/> candidates. It is registered
/// in <see cref="TypedParameterSpace"/> as a <see cref="Subset"/> parameter, allowing
/// optimizers to select the most effective tool subset for a given task.
/// </para>
/// <para>
/// Pair with <see cref="ChatClientTarget"/> for end-to-end tool selection optimization:
/// <code>
/// var target = ChatClientTarget.For(client, tools: [search, calc, mail]);
/// var ctx = OptimizationContext.For(target, trainSet, metric)
///     .WithToolPool([search, calc, mail], minSize: 1, maxSize: 2);
/// </code>
/// </para>
/// </remarks>
public static class ToolPoolExtensions
{
    /// <summary>
    /// Returns a new <see cref="TypedParameterSpace"/> with a tool pool registered as a
    /// <see cref="Subset"/> parameter under <paramref name="paramName"/>.
    /// </summary>
    /// <param name="space">The parameter space to extend.</param>
    /// <param name="tools">The tool pool. All items are initially in the pool.</param>
    /// <param name="paramName">The parameter name (default: <c>"tools"</c>).</param>
    /// <param name="minSize">Minimum number of tools a valid subset must contain (default: 1).</param>
    /// <param name="maxSize">
    /// Maximum number of tools in a valid subset.
    /// <c>-1</c> (default) means no upper bound — all pool tools may be selected.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="space"/> or <paramref name="tools"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minSize"/> is less than 0 or <paramref name="maxSize"/> is less than -1.
    /// </exception>
    public static TypedParameterSpace AddToolPool(
        this TypedParameterSpace space,
        IReadOnlyList<AITool> tools,
        string paramName = "tools",
        int minSize = 1,
        int maxSize = -1)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfLessThan(minSize, 0);
        if (maxSize != -1)
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 0);

        return space.Add(paramName, new Subset<AITool>(TypedPool: [.. tools], minSize, maxSize));
    }

    /// <summary>
    /// Registers a tool pool in <see cref="OptimizationContext.SearchSpace"/> and returns the context.
    /// </summary>
    /// <param name="ctx">The optimization context to update.</param>
    /// <param name="tools">The tool pool.</param>
    /// <param name="paramName">The parameter name (default: <c>"tools"</c>).</param>
    /// <param name="minSize">Minimum subset size (default: 1).</param>
    /// <param name="maxSize">Maximum subset size, or <c>-1</c> for unbounded (default).</param>
    /// <returns>The same <paramref name="ctx"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="ctx"/> or <paramref name="tools"/> is null.
    /// </exception>
    public static OptimizationContext WithToolPool(
        this OptimizationContext ctx,
        IReadOnlyList<AITool> tools,
        string paramName = "tools",
        int minSize = 1,
        int maxSize = -1)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.SearchSpace = ctx.SearchSpace.AddToolPool(tools, paramName, minSize, maxSize);
        return ctx;
    }
}
