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
    /// <typeparam name="TInput">The input type for training examples.</typeparam>
    /// <typeparam name="TLabel">The label type for training examples.</typeparam>
    /// <param name="module">The module to optimize.</param>
    /// <param name="trainSet">Training examples.</param>
    /// <param name="metric">Scoring function: (expected label, actual output) → score.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The optimized module with learnable parameters filled in.</returns>
    Task<TModule> CompileAsync<TModule, TInput, TLabel>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
