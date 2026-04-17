namespace LMP;

/// <summary>
/// Input context passed to <see cref="IMetric.EvaluateAsync"/>.
/// Carries all information needed to compute a <see cref="MetricVector"/>,
/// including the ground-truth example, module output, execution trace, and elapsed time.
/// </summary>
/// <param name="Example">The training/evaluation example with ground-truth label.</param>
/// <param name="Output">The module's actual output (untyped).</param>
/// <param name="Trace">Execution trace from <see cref="IOptimizationTarget.ExecuteAsync"/>.</param>
/// <param name="ElapsedMs">Wall-clock time for the execution in milliseconds.</param>
public sealed record MetricContext(
    Example Example,
    object Output,
    Trace Trace,
    double ElapsedMs)
{
    /// <summary>
    /// Creates a <see cref="MetricContext"/> from the output of
    /// <see cref="IOptimizationTarget.ExecuteAsync"/> and an elapsed time measurement.
    /// </summary>
    public static MetricContext From(
        Example example,
        (object Output, Trace Trace) executeResult,
        double elapsedMs = 0)
        => new(example, executeResult.Output, executeResult.Trace, elapsedMs);
}
