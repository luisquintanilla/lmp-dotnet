namespace LMP;

/// <summary>
/// Accumulates trial results during an optimization run.
/// Shared across all steps in an <c>OptimizationPipeline</c>.
/// </summary>
public sealed class TrialHistory
{
    private readonly List<Trial> _trials = [];

    /// <summary>All recorded trials in chronological order.</summary>
    public IReadOnlyList<Trial> Trials => _trials;

    /// <summary>Number of trials recorded.</summary>
    public int Count => _trials.Count;

    /// <summary>Records a new trial.</summary>
    public void Add(Trial trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        _trials.Add(trial);
    }

    /// <summary>Records multiple trials.</summary>
    public void AddRange(IEnumerable<Trial> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        _trials.AddRange(trials);
    }

    /// <summary>Returns the best score recorded so far, or 0 if no trials have been recorded.</summary>
    public float BestScore => _trials.Count == 0 ? 0f : _trials.Max(t => t.Score);

    /// <summary>Total tokens consumed across all trials.</summary>
    public long TotalTokens => _trials.Sum(t => t.Cost.TotalTokens);

    /// <summary>Total API calls across all trials.</summary>
    public int TotalApiCalls => _trials.Sum(t => t.Cost.ApiCalls);

    /// <summary>Total elapsed milliseconds accumulated across all trials.</summary>
    public long TotalElapsedMs => _trials.Sum(t => t.Cost.ElapsedMilliseconds);
}
