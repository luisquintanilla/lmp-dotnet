namespace LMP;

/// <summary>
/// Tracks a multi-objective Pareto frontier of optimization candidates.
/// Uses <see cref="MetricVector"/> to represent the quality/cost trade-off for each candidate.
/// </summary>
/// <remarks>
/// <para>
/// A candidate entry is on the Pareto frontier if no other entry strictly
/// dominates it: i.e., no other entry is at least as good on <em>all</em> objectives
/// and strictly better on <em>at least one</em>.
/// </para>
/// <para>
/// Objectives and dominance direction:
/// <list type="bullet">
/// <item><description><b>Score</b>: higher is better</description></item>
/// <item><description><b>Tokens</b>: lower is better</description></item>
/// <item><description><b>Turns</b>: lower is better</description></item>
/// </list>
/// LatencyMs is excluded from dominance as it is a noisy measurement.
/// </para>
/// </remarks>
public sealed class ParetoFrontier
{
    private readonly object _lock = new();
    private readonly List<(TargetState State, MetricVector Vector)> _entries = [];

    /// <summary>All non-dominated entries on the frontier, in insertion order.</summary>
    public IReadOnlyList<(TargetState State, MetricVector Vector)> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    /// <summary>Number of entries currently on the frontier.</summary>
    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>
    /// Adds a candidate if it is not dominated by any existing entry.
    /// Removes existing entries that are dominated by the new candidate.
    /// </summary>
    /// <returns><c>true</c> if the candidate was added; <c>false</c> if it was dominated.</returns>
    public bool Add(TargetState state, MetricVector vector)
    {
        lock (_lock)
        {
            // Check if dominated by any existing entry
            foreach (var (_, existingVector) in _entries)
            {
                if (existingVector.Dominates(vector))
                    return false; // New entry is dominated — don't add
            }

            // Remove entries dominated by the new candidate
            _entries.RemoveAll(e => vector.Dominates(e.Vector));

            // Add the new non-dominated entry
            _entries.Add((state, vector));
            return true;
        }
    }

    /// <summary>
    /// The entry with the highest <see cref="MetricVector.Score"/>.
    /// Returns <c>null</c> if the frontier is empty.
    /// </summary>
    public (TargetState State, MetricVector Vector)? BestByScore
    {
        get
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return null;
                return _entries.MaxBy(e => e.Vector.Score);
            }
        }
    }

    /// <summary>
    /// The entry with the fewest <see cref="MetricVector.Tokens"/>.
    /// Returns <c>null</c> if the frontier is empty.
    /// </summary>
    public (TargetState State, MetricVector Vector)? BestByTokens
    {
        get
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return null;
                return _entries.MinBy(e => e.Vector.Tokens);
            }
        }
    }

    /// <summary>
    /// Clears all entries from the frontier.
    /// </summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
