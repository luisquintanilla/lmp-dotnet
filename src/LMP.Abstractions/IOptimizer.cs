namespace LMP;

/// <summary>
/// Compiles (optimizes) a module by running it against training data,
/// scoring with a metric, and filling in learnable parameters (demos, instructions).
/// By default, compilation also writes a typed C# artifact (<c>.g.cs</c>) to the
/// output directory — just like <c>dotnet build</c> produces a <c>.dll</c>.
/// </summary>
public interface IOptimizer
{
    /// <summary>
    /// Optimizes the module's learnable parameters using the provided
    /// training set and metric function. Returns the optimized module.
    /// By default, writes a <c>.g.cs</c> artifact to <c>Generated/</c>.
    /// Pass <see cref="CompileOptions.RuntimeOnly"/> to suppress file output.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to optimize.</param>
    /// <param name="trainSet">Training examples.</param>
    /// <param name="metric">Scoring function: (example, actual output) → score in [0, 1].</param>
    /// <param name="options">
    /// Compilation options. <c>null</c> uses defaults (emit to <c>Generated/</c>).
    /// Use <see cref="CompileOptions.RuntimeOnly"/> to suppress artifact generation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The optimized module with learnable parameters filled in.</returns>
    Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CompileOptions? options = null,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
