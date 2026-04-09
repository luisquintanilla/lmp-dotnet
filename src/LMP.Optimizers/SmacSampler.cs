namespace LMP.Optimizers;

/// <summary>
/// SMAC (Sequential Model-based Algorithm Configuration) sampler for categorical search spaces.
/// Uses a random forest surrogate model to predict scores and their uncertainty,
/// then selects configurations that maximize Expected Improvement (EI).
/// Algorithm ported from ML.NET AutoML's SmacTuner, adapted for categorical-only spaces.
/// </summary>
public sealed class SmacSampler : ISampler
{
    private readonly Dictionary<string, int> _parameterCardinalities;
    private readonly int _numTrees;
    private readonly int _numInitialTrials;
    private readonly int _numRandomEISearch;
    private readonly int _localSearchParentCount;
    private readonly Random _rng;
    private readonly List<(Dictionary<string, int> Config, float Score)> _history = [];

    /// <summary>
    /// Creates a new SMAC sampler for a categorical search space.
    /// </summary>
    /// <param name="parameterCardinalities">
    /// Maps parameter name → number of distinct category values.
    /// </param>
    /// <param name="numTrees">Number of trees in the random forest surrogate. Default is 10.</param>
    /// <param name="numInitialTrials">
    /// Number of uniform random trials before the surrogate model activates.
    /// If null, defaults to max(2 × numParams, 6).
    /// </param>
    /// <param name="numRandomEISearch">
    /// Number of random configurations to evaluate EI for during candidate generation. Default is 100.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    public SmacSampler(
        Dictionary<string, int> parameterCardinalities,
        int numTrees = 10,
        int? numInitialTrials = null,
        int numRandomEISearch = 100,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(parameterCardinalities);

        _parameterCardinalities = new Dictionary<string, int>(parameterCardinalities);
        _numTrees = numTrees;
        _numInitialTrials = numInitialTrials ?? Math.Max(2 * parameterCardinalities.Count, 6);
        _numRandomEISearch = numRandomEISearch;
        _localSearchParentCount = Math.Min(5, Math.Max(1, parameterCardinalities.Count));
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    /// <inheritdoc />
    public int TrialCount => _history.Count;

    /// <inheritdoc />
    public Dictionary<string, int> Propose()
    {
        if (_history.Count < _numInitialTrials)
            return ProposeUniform();

        return ProposeSmac();
    }

    /// <inheritdoc />
    public void Update(Dictionary<string, int> config, float score)
    {
        ArgumentNullException.ThrowIfNull(config);
        _history.Add((new Dictionary<string, int>(config), score));
    }

    private Dictionary<string, int> ProposeUniform()
    {
        var config = new Dictionary<string, int>(_parameterCardinalities.Count);
        foreach (var (name, cardinality) in _parameterCardinalities)
            config[name] = _rng.Next(cardinality);
        return config;
    }

    private Dictionary<string, int> ProposeSmac()
    {
        var featureNames = _parameterCardinalities.Keys.ToList();

        // Fit the random forest surrogate
        var forest = new CategoricalRandomForest(_numTrees, minSamplesPerLeaf: 2, seed: _rng.Next());
        forest.Fit(_history, featureNames);

        float bestScore = _history.Max(t => t.Score);

        // Generate candidates via local search + random EI search
        var candidates = new List<(double EI, Dictionary<string, int> Config)>();

        // Local search around top-k best configurations
        var topK = _history
            .OrderByDescending(t => t.Score)
            .Take(_localSearchParentCount)
            .Select(t => t.Config)
            .ToList();

        foreach (var parent in topK)
        {
            var (ei, config) = LocalSearch(parent, forest, featureNames, bestScore);
            candidates.Add((ei, config));
        }

        // Random EI search
        for (int i = 0; i < _numRandomEISearch; i++)
        {
            var randomConfig = ProposeUniform();
            double ei = ComputeEI(forest, featureNames, randomConfig, bestScore);
            candidates.Add((ei, randomConfig));
        }

        // Return the candidate with highest EI
        var best = candidates.OrderByDescending(c => c.EI).First();
        return best.Config;
    }

    /// <summary>
    /// Local search: start from a config, iteratively move to the neighbor with highest EI.
    /// For categoricals, a neighbor is produced by cycling one parameter to the next category.
    /// </summary>
    private (double EI, Dictionary<string, int> Config) LocalSearch(
        Dictionary<string, int> start,
        CategoricalRandomForest forest,
        List<string> featureNames,
        float bestScore)
    {
        var current = new Dictionary<string, int>(start);
        double currentEI = ComputeEI(forest, featureNames, current, bestScore);

        for (int iter = 0; iter < 20; iter++)
        {
            var neighbors = GetOneMutationNeighborhood(current);
            double bestNeighborEI = double.MinValue;
            Dictionary<string, int>? bestNeighbor = null;

            foreach (var neighbor in neighbors)
            {
                double ei = ComputeEI(forest, featureNames, neighbor, bestScore);
                if (ei > bestNeighborEI)
                {
                    bestNeighborEI = ei;
                    bestNeighbor = neighbor;
                }
            }

            if (bestNeighbor is null || bestNeighborEI - currentEI < 1e-8)
                break;

            current = bestNeighbor;
            currentEI = bestNeighborEI;
        }

        return (currentEI, current);
    }

    /// <summary>
    /// One-mutation neighborhood: for each parameter, cycle to the next category value.
    /// </summary>
    private List<Dictionary<string, int>> GetOneMutationNeighborhood(Dictionary<string, int> config)
    {
        var neighbors = new List<Dictionary<string, int>>();

        foreach (var (name, cardinality) in _parameterCardinalities)
        {
            if (cardinality <= 1)
                continue;

            int currentVal = config.GetValueOrDefault(name, 0);
            int nextVal = (currentVal + 1) % cardinality;

            var neighbor = new Dictionary<string, int>(config)
            {
                [name] = nextVal
            };
            neighbors.Add(neighbor);
        }

        return neighbors;
    }

    /// <summary>
    /// Computes the Expected Improvement acquisition function.
    /// EI = (bestScore - mean) × Φ(z) + σ × φ(z)
    /// where z = (bestScore - mean) / σ, adapted for maximization (higher is better).
    /// </summary>
    private static double ComputeEI(
        CategoricalRandomForest forest,
        List<string> featureNames,
        Dictionary<string, int> config,
        float bestScore)
    {
        var (mean, stdDev) = forest.Predict(config, featureNames);

        if (stdDev < 1e-10)
            return mean - bestScore;

        double z = (mean - bestScore) / stdDev;
        double ei = (mean - bestScore) * NormalCdf(z) + stdDev * NormalPdf(z);
        return ei;
    }

    /// <summary>Standard normal probability density function.</summary>
    private static double NormalPdf(double z)
        => Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);

    /// <summary>
    /// Standard normal cumulative distribution function (rational approximation).
    /// Abramowitz and Stegun formula 7.1.26, accurate to 1.5×10⁻⁷.
    /// </summary>
    private static double NormalCdf(double z)
    {
        if (z < -8) return 0;
        if (z > 8) return 1;

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        double absZ = Math.Abs(z);
        double t = 1.0 / (1.0 + p * absZ);
        double y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * NormalPdf(absZ);

        return z < 0 ? 1.0 - y : y;
    }
}
