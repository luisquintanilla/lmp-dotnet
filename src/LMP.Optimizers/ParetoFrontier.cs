namespace LMP.Optimizers;

/// <summary>
/// Tracks a Pareto frontier of non-dominated candidates for multi-objective optimization.
/// A candidate survives if no other candidate dominates it on ALL per-example scores.
/// This is the core selection mechanism for GEPA's evolutionary search.
/// </summary>
/// <typeparam name="TModule">The LMP module type being optimized.</typeparam>
internal sealed class ParetoFrontier<TModule> where TModule : LmpModule
{
    private readonly List<(TModule Candidate, IReadOnlyList<ExampleResult> Scores)> _frontier = [];

    /// <summary>Number of candidates on the frontier.</summary>
    public int Count => _frontier.Count;

    /// <summary>All non-dominated candidates.</summary>
    public IReadOnlyList<TModule> Frontier => _frontier.Select(f => f.Candidate).ToList();

    /// <summary>
    /// The candidate with the highest average score across all examples.
    /// </summary>
    public TModule Best
    {
        get
        {
            if (_frontier.Count == 0)
                throw new InvalidOperationException("Frontier is empty.");
            return _frontier.MaxBy(f => f.Scores.Average(s => s.Score))!.Candidate;
        }
    }

    /// <summary>
    /// Adds a candidate to the frontier. Removes any candidates it dominates.
    /// If the new candidate is dominated by an existing one, it is not added.
    /// </summary>
    public void Add(TModule candidate, IReadOnlyList<ExampleResult> scores)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(scores);

        // Check if new candidate is dominated by any existing frontier member
        for (int i = _frontier.Count - 1; i >= 0; i--)
        {
            var existing = _frontier[i].Scores;
            if (Dominates(existing, scores))
                return; // new candidate is dominated → don't add

            if (Dominates(scores, existing))
                _frontier.RemoveAt(i); // new dominates existing → remove existing
        }

        _frontier.Add((candidate, scores));
    }

    /// <summary>
    /// Selects two parents from the frontier for the merge (crossover) operation.
    /// Prefers diverse parents: picks one at random, then picks the most different one.
    /// </summary>
    public (TModule Parent1, TModule Parent2) SelectParents(Random rng)
    {
        if (_frontier.Count < 2)
            throw new InvalidOperationException("Need at least 2 candidates for parent selection.");

        int idx1 = rng.Next(_frontier.Count);

        // Pick the most "different" parent (largest score disagreement)
        int idx2 = 0;
        double maxDiff = -1;
        var scores1 = _frontier[idx1].Scores;
        for (int i = 0; i < _frontier.Count; i++)
        {
            if (i == idx1) continue;
            var scores2 = _frontier[i].Scores;
            double diff = ComputeDisagreement(scores1, scores2);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                idx2 = i;
            }
        }

        return (_frontier[idx1].Candidate, _frontier[idx2].Candidate);
    }

    /// <summary>
    /// Returns true if <paramref name="a"/> dominates <paramref name="b"/>:
    /// a[i] >= b[i] for all i, and a[j] > b[j] for at least one j.
    /// </summary>
    private static bool Dominates(IReadOnlyList<ExampleResult> a, IReadOnlyList<ExampleResult> b)
    {
        bool strictlyBetter = false;
        int count = Math.Min(a.Count, b.Count);
        for (int i = 0; i < count; i++)
        {
            if (a[i].Score < b[i].Score)
                return false; // a is worse on at least one → not dominated
            if (a[i].Score > b[i].Score)
                strictlyBetter = true;
        }
        return strictlyBetter;
    }

    /// <summary>
    /// Measures how different two candidates' score profiles are.
    /// </summary>
    private static double ComputeDisagreement(IReadOnlyList<ExampleResult> a, IReadOnlyList<ExampleResult> b)
    {
        double sum = 0;
        int count = Math.Min(a.Count, b.Count);
        for (int i = 0; i < count; i++)
            sum += Math.Abs(a[i].Score - b[i].Score);
        return count > 0 ? sum / count : 0;
    }
}
