namespace LMP;

/// <summary>
/// Scores a complete <see cref="Trajectory"/>. Supports async evaluation from the start
/// to accommodate LLM-as-judge trajectory metrics without a later breaking change.
/// </summary>
public interface ITrajectoryMetric
{
    /// <summary>
    /// Computes a score for the given trajectory. Conventionally in [0, 1],
    /// though implementations may return values outside that range.
    /// </summary>
    /// <param name="trajectory">The trajectory to score. Cannot be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A score, conventionally in [0, 1].</returns>
    ValueTask<float> ScoreAsync(Trajectory trajectory, CancellationToken ct = default);
}
