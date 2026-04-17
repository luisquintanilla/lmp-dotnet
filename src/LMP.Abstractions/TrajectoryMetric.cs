namespace LMP;

/// <summary>
/// Factory for creating <see cref="ITrajectoryMetric"/> implementations.
/// </summary>
public static class TrajectoryMetric
{
    /// <summary>
    /// Creates a trajectory metric from a synchronous scoring function.
    /// </summary>
    /// <param name="fn">Scoring function: trajectory → score in [0, 1].</param>
    public static ITrajectoryMetric Create(Func<Trajectory, float> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return new DelegateTrajectoryMetric((t, _) => ValueTask.FromResult(fn(t)));
    }

    /// <summary>
    /// Creates a trajectory metric from an asynchronous scoring function.
    /// Use for LLM-as-judge metrics that call an LLM during trajectory evaluation.
    /// </summary>
    /// <param name="fn">Async scoring function: (trajectory, ct) → score in [0, 1].</param>
    public static ITrajectoryMetric Create(Func<Trajectory, CancellationToken, ValueTask<float>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return new DelegateTrajectoryMetric(fn);
    }

    /// <summary>
    /// Creates a trajectory metric that scores using the average of all non-null per-step
    /// <see cref="Turn.Reward"/> values. Returns 0 when no turns have rewards.
    /// </summary>
    public static ITrajectoryMetric AverageReward() => Create(t => t.AverageReward);

    /// <summary>
    /// Creates a trajectory metric that scores using the sum of all per-step
    /// <see cref="Turn.Reward"/> values.
    /// </summary>
    public static ITrajectoryMetric TotalReward() => Create(t => t.TotalReward);

    /// <summary>
    /// Wraps a standard single-turn metric as a trajectory metric by scoring the last turn's
    /// output against the trajectory's source example.
    /// </summary>
    /// <remarks>
    /// Returns 0 when the trajectory is empty, has no source example, or the last turn
    /// has no output. This makes the adapter fail-safe: trajectories without enough context
    /// score zero rather than throwing.
    /// </remarks>
    /// <param name="metric">Standard metric: (example, output) → score in [0, 1].</param>
    public static ITrajectoryMetric WrapMetric(Func<Example, object, float> metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return Create(t =>
        {
            if (t.LastTurn?.Output is not { } output) return 0f;
            if (t.Source is not { } source) return 0f;
            return metric(source, output);
        });
    }

    private sealed class DelegateTrajectoryMetric(
        Func<Trajectory, CancellationToken, ValueTask<float>> fn) : ITrajectoryMetric
    {
        public ValueTask<float> ScoreAsync(Trajectory trajectory, CancellationToken ct = default)
            => fn(trajectory, ct);
    }
}
