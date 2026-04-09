namespace LMP;

/// <summary>
/// Compiles (optimizes) a module by running it against training data,
/// scoring with a metric, and filling in learnable parameters (demos, instructions).
/// </summary>
public interface IOptimizer
{
    /// <summary>
    /// Optimizes the module's learnable parameters using the provided
    /// training set and metric function. Returns the same module with
    /// parameters filled in.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to optimize.</param>
    /// <param name="trainSet">Training examples.</param>
    /// <param name="metric">Scoring function: (example, actual output) → score in [0, 1].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The optimized module with learnable parameters filled in.</returns>
    Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
