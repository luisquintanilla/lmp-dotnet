namespace LMP;

/// <summary>
/// Factory for creating typed metric functions that bridge to the untyped
/// <c>Func&lt;Example, object, float&gt;</c> or <c>Func&lt;Example, object, Task&lt;float&gt;&gt;</c>
/// signatures used by optimizers and evaluators.
/// </summary>
public static class Metric
{
    // ── Synchronous metrics ─────────────────────────────────

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
    public static Func<Example, object, float> Create<TPredicted, TExpected>(
        Func<TPredicted, TExpected, bool> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TPredicted)output, (TExpected)example.GetLabel()) ? 1f : 0f;
    }

    // ── Asynchronous metrics (LLM-as-judge, SemanticF1, etc.) ──

    /// <summary>
    /// Creates an async metric from a typed async scoring function.
    /// Use for LLM-as-judge metrics that call an LLM during evaluation.
    /// </summary>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="metric">Async scoring function: (predicted, expected) → score in [0, 1].</param>
    /// <returns>An untyped async metric compatible with evaluators.</returns>
    /// <example>
    /// <code>
    /// var metric = Metric.CreateAsync&lt;DraftReply, DraftReply&gt;(async (predicted, expected) =&gt;
    /// {
    ///     var result = await judgeClient.GetResponseAsync&lt;JudgeScore&gt;(
    ///         $"Rate this reply: {predicted.ReplyText}");
    ///     return result.Score / 5f;
    /// });
    /// </code>
    /// </example>
    public static Func<Example, object, Task<float>> CreateAsync<TPredicted, TExpected>(
        Func<TPredicted, TExpected, Task<float>> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return (example, output) => metric((TPredicted)output, (TExpected)example.GetLabel());
    }

    /// <summary>
    /// Creates an async metric from a typed async predicate.
    /// <c>true</c> maps to <c>1.0f</c>, <c>false</c> maps to <c>0.0f</c>.
    /// </summary>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="metric">Async predicate: (predicted, expected) → correct?</param>
    /// <returns>An untyped async metric compatible with evaluators.</returns>
    public static Func<Example, object, Task<float>> CreateAsync<TPredicted, TExpected>(
        Func<TPredicted, TExpected, Task<bool>> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return async (example, output) =>
            await metric((TPredicted)output, (TExpected)example.GetLabel()) ? 1f : 0f;
    }

    // ── IMetric vector factories ────────────────────────────────

    /// <summary>
    /// Creates an <see cref="IMetric"/> from a typed synchronous function that returns
    /// a <see cref="MetricVector"/> (quality + cost dimensions).
    /// The <see cref="MetricContext"/> carries the execution trace, latency, and example.
    /// </summary>
    public static IMetric CreateVector<TPredicted, TExpected>(
        Func<TPredicted, TExpected, MetricContext, MetricVector> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return new DelegateMetric(ctx =>
        {
            var vector = fn(
                (TPredicted)ctx.Output,
                (TExpected)ctx.Example.GetLabel(),
                ctx);
            return ValueTask.FromResult(vector);
        });
    }

    /// <summary>
    /// Creates an <see cref="IMetric"/> from a typed asynchronous function that returns
    /// a <see cref="MetricVector"/>.
    /// </summary>
    public static IMetric CreateVectorAsync<TPredicted, TExpected>(
        Func<TPredicted, TExpected, MetricContext, CancellationToken, ValueTask<MetricVector>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return new DelegateMetric((ctx, ct) =>
            fn((TPredicted)ctx.Output, (TExpected)ctx.Example.GetLabel(), ctx, ct));
    }

    /// <summary>
    /// Wraps a <see cref="Func{Example, Object, Float}"/> as an <see cref="IMetric"/>
    /// that automatically populates cost dimensions from the execution trace.
    /// </summary>
    public static IMetric ToVectorMetric(Func<Example, object, float> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return new DelegateMetric(ctx =>
        {
            float score = metric(ctx.Example, ctx.Output);
            return ValueTask.FromResult(MetricVector.FromTrace(score, ctx.Trace, ctx.ElapsedMs));
        });
    }

    private sealed class DelegateMetric : IMetric
    {
        private readonly Func<MetricContext, CancellationToken, ValueTask<MetricVector>> _fn;

        internal DelegateMetric(Func<MetricContext, ValueTask<MetricVector>> fn)
            => _fn = (ctx, _) => fn(ctx);

        internal DelegateMetric(Func<MetricContext, CancellationToken, ValueTask<MetricVector>> fn)
            => _fn = fn;

        public ValueTask<MetricVector> EvaluateAsync(MetricContext ctx, CancellationToken ct = default)
            => _fn(ctx, ct);
    }
}
