namespace LMP;

/// <summary>
/// Extension methods for using <see cref="LmpModule"/> as an <see cref="IOptimizationTarget"/>
/// in the unified optimization pipeline.
/// </summary>
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
    /// Creates a <see cref="ModuleTarget"/> wrapping this module for direct use
    /// in an <see cref="OptimizationContext"/>.
    /// </summary>
    public static ModuleTarget AsOptimizationTarget(this LmpModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ModuleTarget.For(module);
    }

    /// <summary>
    /// Creates an <see cref="OptimizationContext"/> bound to this module.
    /// Equivalent to <c>OptimizationContext.For(ModuleTarget.For(module), trainSet, metric)</c>.
    /// </summary>
    public static OptimizationContext AsOptimizationContext(
        this LmpModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        IReadOnlyList<Example>? devSet = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return OptimizationContext.For(ModuleTarget.For(module), trainSet, metric, devSet);
    }
}
