using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Runs N parallel predictions and returns the one that scores highest on a reward function.
/// Inherits from <see cref="Predictor{TInput, TOutput}"/> so it can be used anywhere a
/// predictor is expected (e.g., in <see cref="LmpModule.GetPredictors"/>).
/// </summary>
/// <typeparam name="TInput">The input type for the predictor.</typeparam>
/// <typeparam name="TOutput">The output type for the predictor. Must be a reference type.</typeparam>
/// <remarks>
/// All N predictions share the same learnable state (Instructions, Demos, Config)
/// inherited from the base <see cref="Predictor{TInput, TOutput}"/>. The reward function
/// scores each candidate and the highest-scoring result is returned. All candidates are
/// recorded in the trace for optimizer consumption.
/// </remarks>
public class BestOfN<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly int _n;
    private readonly Func<TInput, TOutput, float> _reward;

    /// <summary>
    /// Creates a best-of-N predictor that fires N parallel predictions and selects the best.
    /// </summary>
    /// <param name="client">The chat client to use for LM calls.</param>
    /// <param name="n">The number of parallel predictions to make. Must be at least 1.</param>
    /// <param name="reward">
    /// A scoring function that takes the input and a candidate output, returning a float score.
    /// Higher scores indicate better results.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> or <paramref name="reward"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="n"/> is less than 1.</exception>
    public BestOfN(IChatClient client, int n, Func<TInput, TOutput, float> reward)
        : base(client)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentNullException.ThrowIfNull(reward);
        _n = n;
        _reward = reward;
    }

    /// <summary>
    /// Gets the number of parallel predictions this instance fires.
    /// </summary>
    public int N => _n;

    /// <summary>
    /// Fires N concurrent predictions via <see cref="Task.WhenAll"/>, scores each candidate
    /// with the reward function, and returns the highest-scoring result.
    /// All candidates are recorded in the trace (if provided).
    /// </summary>
    /// <param name="input">The input to predict from.</param>
    /// <param name="trace">Optional trace for recording all N invocations.</param>
    /// <param name="validate">
    /// Optional validation delegate. Applied to the best-scoring result after selection.
    /// Should use <see cref="LmpAssert.That{T}"/> to throw <see cref="LmpAssertionException"/>
    /// on validation failure.
    /// </param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure (default 3). Not used for individual predictions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The highest-scoring predicted output.</returns>
    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        // True parallelism — no GIL. Each prediction runs concurrently.
        var tasks = Enumerable.Range(0, _n)
            .Select(_ => base.PredictAsync(input, trace, cancellationToken: cancellationToken));

        var candidates = await Task.WhenAll(tasks);

        // Score each candidate and return the best
        var best = candidates
            .OrderByDescending(c => _reward(input, c))
            .First();

        // Apply optional validation to the selected result
        validate?.Invoke(best);

        return best;
    }
}
