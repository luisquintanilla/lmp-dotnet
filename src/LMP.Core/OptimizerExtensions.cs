namespace LMP;

/// <summary>
/// Backward-compatibility bridge: routes the old <c>CompileAsync&lt;TModule&gt;</c> call pattern
/// through the new unified <see cref="IOptimizer"/> interface.
/// </summary>
/// <remarks>
/// All existing optimizer call sites continue to compile and run unchanged.
/// Migrate to <see cref="OptimizationPipeline"/> at your own pace.
/// </remarks>
public static class OptimizerExtensions
{
    /// <summary>
    /// Runs the optimizer on the given module using the unified pipeline under the hood.
    /// Preserves the original return-module-with-params-filled semantics.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="optimizer">The optimizer to run.</param>
    /// <param name="module">The module to optimize (mutated in-place).</param>
    /// <param name="trainSet">Training examples.</param>
    /// <param name="metric">Scoring function: (example, output) → score in [0, 1].</param>
    /// <param name="options">
    /// Compilation options. Pass <see cref="CompileOptions.RuntimeOnly"/> to suppress artifact
    /// generation. When <c>null</c>, no artifact is written.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The same module instance with its predictors updated.</returns>
    [Obsolete("Use OptimizeAsync(OptimizationContext) via OptimizationPipeline. " +
              "This overload is preserved for backward compatibility.")]
    public static async Task<TModule> CompileAsync<TModule>(
        this IOptimizer optimizer,
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CompileOptions? options = null,
        CancellationToken ct = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(optimizer);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        var ctx = new OptimizationContext
        {
            Target = module,
            TrainSet = trainSet,
            Metric = metric,
        };

        await optimizer.OptimizeAsync(ctx, ct).ConfigureAwait(false);

        if (options?.OutputDir is not null)
        {
            var baseline = ctx.Diagnostics.BaselineScore ?? 0f;
            await CSharpArtifactWriter.WriteAsync(
                module,
                options.OutputDir,
                ctx.TrialHistory.BestScore,
                optimizer.GetType().Name,
                options.TrainDataPath,
                baseline,
                ct).ConfigureAwait(false);
        }

        return module;
    }
}
