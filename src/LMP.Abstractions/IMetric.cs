namespace LMP;

/// <summary>
/// Multi-objective metric that evaluates quality <b>and</b> cost in a single pass.
/// Returns a <see cref="MetricVector"/> instead of a scalar so optimizers can apply
/// Pareto-based selection across accuracy, token cost, latency, and turn count.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="IMetric"/> when you need multi-objective optimization, LLM-as-judge
/// evaluation that returns rationale alongside a score, or access to the execution
/// <see cref="Trace"/> (token counts, API calls) during scoring.
/// </para>
/// <para>
/// For backward compatibility, the existing <c>Func&lt;Example, object, float&gt;</c>
/// pattern in <see cref="OptimizationContext.Metric"/> is unchanged.
/// Optimizers that support <see cref="IMetric"/> check for
/// <see cref="OptimizationContext.VectorMetric"/> first, then fall back to
/// <see cref="OptimizationContext.Metric"/>.
/// </para>
/// </remarks>
public interface IMetric
{
    /// <summary>
    /// Evaluates a prediction against the ground truth, returning a
    /// <see cref="MetricVector"/> with both quality score and cost dimensions.
    /// </summary>
    ValueTask<MetricVector> EvaluateAsync(MetricContext ctx, CancellationToken ct = default);
}
