using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Sequential refinement: predict, critique, re-predict with critique feedback.
/// Runs for a configurable number of iterations. Each refinement iteration is a
/// separate <see cref="Predictor{TInput, TOutput}.PredictAsync"/> call recorded in
/// the trace, allowing the optimizer to see the full refinement chain.
/// </summary>
/// <typeparam name="TInput">The input type for the predictor.</typeparam>
/// <typeparam name="TOutput">The output type for the predictor. Must be a reference type.</typeparam>
/// <remarks>
/// The initial prediction uses the base predictor (inheriting instructions, demos, config).
/// Each subsequent iteration uses an internal refiner predictor that receives the original
/// input and the previous output, producing an improved version. The refiner has its own
/// instructions telling the LM to critique and improve.
/// </remarks>
public class Refine<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly Predictor<RefineCritiqueInput<TOutput>, TOutput> _refiner;
    private readonly int _maxIterations;

    /// <summary>
    /// Creates a refinement predictor that iteratively improves predictions via LM self-critique.
    /// </summary>
    /// <param name="client">The chat client to use for LM calls.</param>
    /// <param name="maxIterations">
    /// Maximum predict-critique cycles after the initial prediction (default: 2).
    /// Must be at least 1.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxIterations"/> is less than 1.</exception>
    public Refine(IChatClient client, int maxIterations = 2)
        : base(client)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        _maxIterations = maxIterations;
        _refiner = new Predictor<RefineCritiqueInput<TOutput>, TOutput>(client)
        {
            Instructions = "Given the original input, a previous attempt, " +
                           "and a critique, produce an improved output."
        };
    }

    /// <summary>
    /// Gets the maximum number of refinement iterations this instance performs.
    /// </summary>
    public int MaxIterations => _maxIterations;

    /// <summary>
    /// Gets the internal refiner predictor used for critique-and-improve iterations.
    /// Exposed for optimizer access to learnable state (instructions, demos).
    /// </summary>
    internal Predictor<RefineCritiqueInput<TOutput>, TOutput> Refiner => _refiner;

    /// <summary>
    /// Executes an iterative refinement: initial predict, then for each iteration
    /// sends the original input and previous output to the refiner for improvement.
    /// Each step is recorded in the trace. Validation (if provided) is applied only
    /// to the final result after all iterations complete.
    /// </summary>
    /// <param name="input">The input to predict from.</param>
    /// <param name="trace">Optional trace for recording all invocations (initial + refinements).</param>
    /// <param name="validate">
    /// Optional validation delegate. Applied to the final refined result.
    /// Should use <see cref="LmpAssert.That{T}"/> to throw <see cref="LmpAssertionException"/>
    /// on validation failure.
    /// </param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure (default 3). Not used for individual iterations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final refined output after all iterations.</returns>
    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        // Initial prediction using the base predictor
        var current = await base.PredictAsync(input, trace, cancellationToken: cancellationToken);

        // Iterative refinement: critique previous output and produce improved version
        for (int i = 0; i < _maxIterations; i++)
        {
            var critiqueInput = new RefineCritiqueInput<TOutput>(
                OriginalInput: input!,
                PreviousOutput: current);

            current = await _refiner.PredictAsync(
                critiqueInput, trace, cancellationToken: cancellationToken);
        }

        // Apply optional validation to the final refined result
        validate?.Invoke(current);

        return current;
    }
}
