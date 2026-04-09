namespace LMP.Optimizers;

/// <summary>
/// Minimal Tree-structured Parzen Estimator (TPE) for categorical-only search spaces.
/// Maintains frequency-based surrogate models for "good" and "bad" trials,
/// then proposes configurations that maximize the ratio l(x) / g(x).
/// </summary>
public sealed class CategoricalTpeSampler : ISampler
{
    private readonly Dictionary<string, int> _parameterCardinalities;
    private readonly double _gamma;
    private readonly Random _rng;
    private readonly List<(Dictionary<string, int> Config, float Score)> _history = [];

    /// <summary>
    /// Creates a new TPE sampler for a categorical search space.
    /// </summary>
    /// <param name="parameterCardinalities">
    /// Maps parameter name → number of distinct category values.
    /// For example, <c>{ "classify_instruction" → 5, "classify_demos" → 4 }</c>.
    /// </param>
    /// <param name="gamma">
    /// Quantile threshold (0,1) for splitting trials into good vs bad.
    /// Default is 0.25 (top 25% are "good"), matching DSPy's default.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="gamma"/> is not in (0, 1).
    /// </exception>
    public CategoricalTpeSampler(
        Dictionary<string, int> parameterCardinalities,
        double gamma = 0.25,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(parameterCardinalities);
        if (gamma is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be in (0, 1).");

        _parameterCardinalities = new Dictionary<string, int>(parameterCardinalities);
        _gamma = gamma;
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    /// <summary>
    /// Number of completed trials recorded so far.
    /// </summary>
    public int TrialCount => _history.Count;

    /// <summary>
    /// Proposes a new configuration. For the first few trials (before enough
    /// history for TPE), uses uniform random sampling. Once enough trials exist,
    /// uses the TPE acquisition function: maximize l(x)/g(x).
    /// </summary>
    /// <returns>
    /// A dictionary mapping parameter name → selected category index.
    /// </returns>
    public Dictionary<string, int> Propose()
    {
        // Need at least 2 trials to split into good/bad
        int minTrialsForTpe = Math.Max(2, (int)Math.Ceiling(1.0 / _gamma));
        if (_history.Count < minTrialsForTpe)
            return ProposeUniform();

        return ProposeTpe();
    }

    /// <summary>
    /// Reports the result of a trial to the sampler.
    /// </summary>
    /// <param name="config">The configuration that was evaluated.</param>
    /// <param name="score">The evaluation score (higher is better).</param>
    public void Update(Dictionary<string, int> config, float score)
    {
        ArgumentNullException.ThrowIfNull(config);
        _history.Add((new Dictionary<string, int>(config), score));
    }

    private Dictionary<string, int> ProposeUniform()
    {
        var config = new Dictionary<string, int>(_parameterCardinalities.Count);
        foreach (var (name, cardinality) in _parameterCardinalities)
        {
            config[name] = _rng.Next(cardinality);
        }
        return config;
    }

    private Dictionary<string, int> ProposeTpe()
    {
        // Sort trials by score descending and split at the gamma quantile
        var sorted = _history.OrderByDescending(t => t.Score).ToList();
        int numGood = Math.Max(1, (int)(_history.Count * _gamma));

        var good = sorted.Take(numGood).ToList();
        var bad = sorted.Skip(numGood).ToList();

        // If bad is empty (all trials equally good), fall back to uniform
        if (bad.Count == 0)
            return ProposeUniform();

        var config = new Dictionary<string, int>(_parameterCardinalities.Count);

        foreach (var (name, cardinality) in _parameterCardinalities)
        {
            config[name] = SampleByAcquisition(name, cardinality, good, bad);
        }

        return config;
    }

    /// <summary>
    /// For a single categorical parameter, computes l(x)/g(x) for each category
    /// and samples proportionally. Uses Laplace smoothing (add-1) to avoid zero probabilities.
    /// </summary>
    private int SampleByAcquisition(
        string paramName,
        int cardinality,
        List<(Dictionary<string, int> Config, float Score)> good,
        List<(Dictionary<string, int> Config, float Score)> bad)
    {
        // Count frequencies with Laplace smoothing
        var goodCounts = new double[cardinality];
        var badCounts = new double[cardinality];

        // Laplace smoothing: start each count at 1
        for (int i = 0; i < cardinality; i++)
        {
            goodCounts[i] = 1.0;
            badCounts[i] = 1.0;
        }

        foreach (var (cfg, _) in good)
        {
            if (cfg.TryGetValue(paramName, out var val))
                goodCounts[val] += 1.0;
        }

        foreach (var (cfg, _) in bad)
        {
            if (cfg.TryGetValue(paramName, out var val))
                badCounts[val] += 1.0;
        }

        // Normalize to probabilities
        double goodTotal = goodCounts.Sum();
        double badTotal = badCounts.Sum();

        // Compute acquisition values: l(x) / g(x)
        var acquisitionValues = new double[cardinality];
        for (int i = 0; i < cardinality; i++)
        {
            double lx = goodCounts[i] / goodTotal;
            double gx = badCounts[i] / badTotal;
            acquisitionValues[i] = lx / gx;
        }

        // Sample proportional to acquisition values
        double totalAcq = acquisitionValues.Sum();
        double roll = _rng.NextDouble() * totalAcq;
        double cumulative = 0;
        for (int i = 0; i < cardinality; i++)
        {
            cumulative += acquisitionValues[i];
            if (roll <= cumulative)
                return i;
        }

        // Fallback (shouldn't reach here due to floating point)
        return cardinality - 1;
    }
}
