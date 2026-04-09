namespace LMP;

/// <summary>
/// Factory for creating typed metric functions that bridge to the untyped
/// <c>Func&lt;Example, object, float&gt;</c> signature used by optimizers and evaluators.
/// </summary>
public static class Metric
{
    /// <summary>
    /// Creates a metric from a typed scoring function that returns a float in [0, 1].
    /// Use this for partial-credit metrics (keyword overlap, F1, etc.).
    /// </summary>
    /// <typeparam name="TOutput">The module's output type (also the label type).</typeparam>
    /// <param name="metric">Scoring function: (predicted, expected) → score in [0, 1].</param>
    /// <returns>An untyped metric compatible with <see cref="IOptimizer"/> and evaluators.</returns>
    /// <example>
    /// <code>
    /// var metric = Metric.Create&lt;DraftReply&gt;((predicted, expected) =&gt;
    ///     matchedKeywords / totalKeywords);
    /// </code>
    /// </example>
    public static Func<Example, object, float> Create<TOutput>(Func<TOutput, TOutput, float> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TOutput)output, (TOutput)example.GetLabel());
    }

    /// <summary>
    /// Creates a metric from a typed predicate that returns true for correct predictions.
    /// <c>true</c> maps to <c>1.0f</c>, <c>false</c> maps to <c>0.0f</c>.
    /// Use this for exact-match or pass/fail metrics.
    /// </summary>
    /// <typeparam name="TOutput">The module's output type (also the label type).</typeparam>
    /// <param name="metric">Predicate: (predicted, expected) → correct?</param>
    /// <returns>An untyped metric compatible with <see cref="IOptimizer"/> and evaluators.</returns>
    /// <example>
    /// <code>
    /// var metric = Metric.Create&lt;ClassifyTicket&gt;((predicted, expected) =&gt;
    ///     predicted.Category == expected.Category);
    /// </code>
    /// </example>
    public static Func<Example, object, float> Create<TOutput>(Func<TOutput, TOutput, bool> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TOutput)output, (TOutput)example.GetLabel()) ? 1f : 0f;
    }
}
