namespace LMP;

/// <summary>
/// Proposes and updates categorical hyperparameter configurations for Bayesian
/// optimization. Implementations maintain trial history and use it to guide
/// subsequent proposals toward higher-scoring regions of the search space.
/// Mirrors ML.NET AutoML's <c>ITuner</c> pattern: <see cref="Propose"/> + <see cref="Update"/>.
/// </summary>
/// <remarks>
/// Each parameter is a named categorical variable with a fixed number of choices
/// (e.g., <c>{ "classify_instr" → 5, "classify_demos" → 4 }</c>). The sampler
/// proposes a configuration by selecting one choice index per parameter, then
/// receives feedback via <see cref="Update"/> after evaluation.
/// </remarks>
public interface ISampler
{
    /// <summary>
    /// Number of completed trials recorded so far.
    /// </summary>
    int TrialCount { get; }

    /// <summary>
    /// Proposes a new configuration to evaluate.
    /// </summary>
    /// <returns>
    /// A dictionary mapping parameter name → selected category index.
    /// </returns>
    Dictionary<string, int> Propose();

    /// <summary>
    /// Reports the result of evaluating a proposed configuration.
    /// </summary>
    /// <param name="config">The configuration that was evaluated.</param>
    /// <param name="score">The evaluation score (higher is better).</param>
    void Update(Dictionary<string, int> config, float score);

    /// <summary>
    /// Reports the result of evaluating a proposed configuration, including cost data.
    /// Cost-aware samplers override this to incorporate cost into their acquisition function.
    /// The default implementation delegates to <see cref="Update(Dictionary{string, int}, float)"/>,
    /// so existing samplers remain backward-compatible without changes.
    /// </summary>
    /// <param name="config">The configuration that was evaluated.</param>
    /// <param name="score">The evaluation score (higher is better).</param>
    /// <param name="cost">The cost measurement for this trial.</param>
    void Update(Dictionary<string, int> config, float score, TrialCost cost)
        => Update(config, score);
}
