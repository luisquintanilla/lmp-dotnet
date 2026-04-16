using System.Numerics.Tensors;

namespace LMP.Optimizers;

/// <summary>
/// Tracks a Pareto frontier of candidates using per-instance best tracking.
/// A candidate is on the frontier if it achieves the best score on at least one
/// validation example. This matches the DSPy GEPA Pareto frontier design.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant:</b> All candidates must be evaluated on the same set of examples
/// in the same order. The scores at position <c>i</c> must correspond to the same
/// validation example for every candidate. GEPA enforces this by evaluating all
/// candidates on the full training set.
/// </para>
/// </remarks>
/// <typeparam name="TModule">The LMP module type being optimized.</typeparam>
internal sealed class ParetoFrontier<TModule> where TModule : LmpModule
{
    private readonly List<(TModule Candidate, IReadOnlyList<ExampleResult> Scores)> _candidates = [];

    // Per-instance tracking: best score and which candidates achieved it
    private readonly List<float> _bestPerInstance = [];
    private readonly List<HashSet<int>> _bestCandidatesPerInstance = [];

    // Frontier = candidate indices that are best on at least one instance
    private HashSet<int> _frontierIndices = [];

    /// <summary>Number of candidates on the frontier.</summary>
    public int Count => _frontierIndices.Count;

    /// <summary>Total number of candidates tracked (including non-frontier).</summary>
    public int TotalCandidates => _candidates.Count;

    /// <summary>All frontier candidates (those that are best on at least one instance).</summary>
    public IReadOnlyList<TModule> Frontier => _frontierIndices.Order().Select(i => _candidates[i].Candidate).ToList();

    /// <summary>
    /// The candidate with the highest average score across all examples.
    /// Considers all tracked candidates, not just frontier members.
    /// </summary>
    public TModule Best
    {
        get
        {
            if (_candidates.Count == 0)
                throw new InvalidOperationException("Frontier is empty.");
            return _candidates.MaxBy(c => c.Scores.Average(s => s.Score))!.Candidate;
        }
    }

    /// <summary>
    /// The Pareto front score: for each instance, take the best score achieved by any
    /// candidate, then average across all instances. This represents the theoretical
    /// best achievable by an ensemble of the frontier candidates.
    /// </summary>
    public float ParetoFrontScore => _bestPerInstance.Count > 0
        ? _bestPerInstance.Average(s => (double)s) is double avg ? (float)avg : 0f
        : 0f;

    /// <summary>
    /// Adds a candidate with its per-instance scores. The scores must be evaluated
    /// on the same examples as all other candidates, in the same order.
    /// </summary>
    public void Add(TModule candidate, IReadOnlyList<ExampleResult> scores)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(scores);

        int candidateIdx = _candidates.Count;
        _candidates.Add((candidate, scores));

        // Expand per-instance tracking if needed (first candidate sets the size)
        while (_bestPerInstance.Count < scores.Count)
        {
            _bestPerInstance.Add(float.NegativeInfinity);
            _bestCandidatesPerInstance.Add([]);
        }

        // Update per-instance bests
        for (int i = 0; i < scores.Count; i++)
        {
            float score = scores[i].Score;
            if (score > _bestPerInstance[i])
            {
                _bestPerInstance[i] = score;
                _bestCandidatesPerInstance[i] = [candidateIdx];
            }
            else if (Math.Abs(score - _bestPerInstance[i]) < 1e-6f)
            {
                _bestCandidatesPerInstance[i].Add(candidateIdx);
            }
        }

        // Recompute frontier: candidates that appear in at least one per-instance best set
        _frontierIndices = [];
        foreach (var set in _bestCandidatesPerInstance)
        {
            foreach (int idx in set)
                _frontierIndices.Add(idx);
        }
    }

    /// <summary>
    /// Selects two parents from the frontier for the merge (crossover) operation.
    /// Prefers diverse parents: picks one at random, then picks the most different one.
    /// </summary>
    public (TModule Parent1, TModule Parent2) SelectParents(Random rng)
    {
        if (_frontierIndices.Count < 2)
            throw new InvalidOperationException("Need at least 2 candidates for parent selection.");

        var frontierList = _frontierIndices.ToList();
        int idx1 = frontierList[rng.Next(frontierList.Count)];

        // Pick the most "different" parent (largest score disagreement)
        int idx2 = frontierList[0] == idx1 ? frontierList[1] : frontierList[0];
        double maxDiff = -1;
        var scores1 = _candidates[idx1].Scores;
        foreach (int i in frontierList)
        {
            if (i == idx1) continue;
            var scores2 = _candidates[i].Scores;
            double diff = ComputeDisagreement(scores1, scores2);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                idx2 = i;
            }
        }

        return (_candidates[idx1].Candidate, _candidates[idx2].Candidate);
    }

    /// <summary>
    /// Measures how different two candidates' score profiles are.
    /// </summary>
    private static double ComputeDisagreement(IReadOnlyList<ExampleResult> a, IReadOnlyList<ExampleResult> b)
    {
        int count = Math.Min(a.Count, b.Count);
        if (count == 0) return 0;

        var scoresA = new float[count];
        var scoresB = new float[count];
        for (int i = 0; i < count; i++)
        {
            scoresA[i] = a[i].Score;
            scoresB[i] = b[i].Score;
        }

        Span<float> diff = stackalloc float[count];
        TensorPrimitives.Subtract<float>(scoresA, scoresB, diff);
        TensorPrimitives.Abs(diff, diff);
        return TensorPrimitives.Sum<float>(diff) / count;
    }
}
