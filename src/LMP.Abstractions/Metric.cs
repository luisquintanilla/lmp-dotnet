namespace LMP;

/// <summary>
/// Factory for creating typed metric functions that bridge to the untyped
/// <c>Func&lt;Example, object, float&gt;</c> signature used by optimizers and evaluators.
/// </summary>
public static class Metric
{
    /// <summary>
    /// Creates a metric from a typed scoring function that returns a float in [0, 1].
    /// The predicted and expected types may differ (e.g., module output with extra fields
    /// like confidence/reasoning vs. simple ground truth labels).
    /// When types match, the compiler infers both type parameters automatically.
    /// </summary>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="metric">Scoring function: (predicted, expected) → score in [0, 1].</param>
    /// <returns>An untyped metric compatible with <see cref="IOptimizer"/> and evaluators.</returns>
    /// <example>
    /// <code>
    /// // Same types — compiler infers both type params:
    /// var m1 = Metric.Create((DraftReply predicted, DraftReply expected) =&gt;
    ///     matchedKeywords / totalKeywords);
    ///
    /// // Different types — explicit type args:
    /// var m2 = Metric.Create&lt;AnswerWithConfidence, SimpleAnswer&gt;(
    ///     (predicted, expected) =&gt; predicted.Answer == expected.Answer ? 1f : 0f);
    /// </code>
    /// </example>
    public static Func<Example, object, float> Create<TPredicted, TExpected>(
        Func<TPredicted, TExpected, float> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TPredicted)output, (TExpected)example.GetLabel());
    }

    /// <summary>
    /// Creates a metric from a typed predicate where <c>true</c> maps to <c>1.0f</c>
    /// and <c>false</c> maps to <c>0.0f</c>.
    /// The predicted and expected types may differ.
    /// </summary>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="metric">Predicate: (predicted, expected) → correct?</param>
    /// <returns>An untyped metric compatible with <see cref="IOptimizer"/> and evaluators.</returns>
    /// <example>
    /// <code>
    /// var metric = Metric.Create((ClassifyTicket predicted, ClassifyTicket expected) =&gt;
    ///     predicted.Category == expected.Category);
    /// </code>
    /// </example>
    public static Func<Example, object, float> Create<TPredicted, TExpected>(
        Func<TPredicted, TExpected, bool> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TPredicted)output, (TExpected)example.GetLabel()) ? 1f : 0f;
    }
}
