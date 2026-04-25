namespace LMP;

/// <summary>
/// Single-algorithm optimization step in an <c>OptimizationPipeline</c>.
/// Implementations read from and write to <see cref="OptimizationContext"/> fields,
/// mutating <see cref="OptimizationContext.Target"/> in-place.
/// </summary>
public interface IOptimizer
{
    /// <summary>
    /// Runs one optimization pass. Reads from and writes to <paramref name="ctx"/> in-place.
    /// After completion, <see cref="OptimizationContext.Target"/> reflects the best state found.
    /// </summary>
    /// <param name="ctx">The optimization context shared among all pipeline steps.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default);
}

