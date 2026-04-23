namespace LMP;

/// <summary>
/// Extension methods for using <see cref="LmpModule"/> in the unified optimization pipeline.
/// </summary>
/// <remarks>
/// Since T1, <see cref="LmpModule"/> implements <see cref="IOptimizationTarget"/> directly —
/// adapter wrappers are no longer required. Pass a module wherever an
/// <see cref="IOptimizationTarget"/> is expected.
/// </remarks>
public static class LmpModuleOptimizationExtensions
{
    /// <summary>
    /// Creates an <see cref="OptimizationPipeline"/> for this module.
    /// Use <c>.Use(new BootstrapFewShot())</c> etc. to add optimization steps.
    /// </summary>
    /// <example>
    /// <code>
    /// var result = await module
    ///     .AsOptimizationPipeline()
    ///     .Use(new BootstrapFewShot(maxDemos: 4))
    ///     .OptimizeAsync(trainSet, devSet, metric, ct);
    /// </code>
    /// </example>
    public static OptimizationPipeline AsOptimizationPipeline(this LmpModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return OptimizationPipeline.For(module);
    }

    /// <summary>
    /// Creates an <see cref="OptimizationContext"/> bound to this module.
    /// </summary>
    public static OptimizationContext AsOptimizationContext(
        this LmpModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        IReadOnlyList<Example>? devSet = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return OptimizationContext.For(module, trainSet, metric, devSet);
    }
}
