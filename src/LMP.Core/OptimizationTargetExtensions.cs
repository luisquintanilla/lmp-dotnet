namespace LMP;

/// <summary>
/// Extension methods for composing <see cref="IOptimizationTarget"/> instances.
/// </summary>
public static class OptimizationTargetExtensions
{
    /// <summary>
    /// Returns a new <see cref="IOptimizationTarget"/> that executes <paramref name="first"/>
    /// followed by <paramref name="next"/>, piping the output of the former as input to the latter.
    /// </summary>
    /// <remarks>
    /// If <paramref name="first"/> is already a <see cref="ChainTarget"/>, <paramref name="next"/>
    /// is appended to the existing chain (flattened) rather than nested. This keeps
    /// child-parameter prefixes (<c>child_0.</c>, <c>child_1.</c>, …) sequential.
    /// </remarks>
    /// <param name="first">The target whose output is piped forward. Must not be null.</param>
    /// <param name="next">The target that consumes <paramref name="first"/>'s output. Must not be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="first"/> or <paramref name="next"/> is null.
    /// </exception>
    public static IOptimizationTarget Then(this IOptimizationTarget first, IOptimizationTarget next)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(next);

        if (first is ChainTarget existing)
        {
            var combined = new IOptimizationTarget[existing.Targets.Count + 1];
            for (int i = 0; i < existing.Targets.Count; i++)
                combined[i] = existing.Targets[i];
            combined[^1] = next;
            return new ChainTarget(combined);
        }

        return new ChainTarget([first, next]);
    }
}
