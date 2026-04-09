namespace LMP.Optimizers;

/// <summary>
/// A simple random forest for categorical-only feature spaces.
/// Used internally by <see cref="SmacSampler"/> as the surrogate model.
/// Each tree is trained on a bootstrap sample with a random feature subset,
/// using variance reduction for splits. Ensemble predictions provide
/// both mean (prediction) and standard deviation (uncertainty).
/// </summary>
internal sealed class CategoricalRandomForest
{
    private readonly int _numTrees;
    private readonly int _minSamplesPerLeaf;
    private readonly Random _rng;
    private DecisionTree[]? _trees;

    public CategoricalRandomForest(int numTrees = 10, int minSamplesPerLeaf = 2, int? seed = null)
    {
        _numTrees = numTrees;
        _minSamplesPerLeaf = minSamplesPerLeaf;
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    /// <summary>
    /// Fits the forest on observed (config, score) trials.
    /// </summary>
    public void Fit(
        IReadOnlyList<(Dictionary<string, int> Config, float Score)> trials,
        IReadOnlyList<string> featureNames)
    {
        _trees = new DecisionTree[_numTrees];
        int n = trials.Count;

        for (int t = 0; t < _numTrees; t++)
        {
            // Bootstrap sample (sample with replacement)
            var bootstrap = new List<int>(n);
            for (int i = 0; i < n; i++)
                bootstrap.Add(_rng.Next(n));

            // Random feature subset: sqrt(numFeatures) features per tree
            int numFeaturesToUse = Math.Max(1, (int)Math.Sqrt(featureNames.Count));
            var featureIndices = Enumerable.Range(0, featureNames.Count)
                .OrderBy(_ => _rng.Next())
                .Take(numFeaturesToUse)
                .ToArray();

            _trees[t] = BuildTree(trials, featureNames, bootstrap, featureIndices);
        }
    }

    /// <summary>
    /// Predicts the mean and standard deviation for a given configuration.
    /// </summary>
    public (double Mean, double StdDev) Predict(Dictionary<string, int> config, IReadOnlyList<string> featureNames)
    {
        if (_trees is null)
            throw new InvalidOperationException("Forest has not been fitted.");

        var predictions = new double[_trees.Length];
        for (int t = 0; t < _trees.Length; t++)
            predictions[t] = _trees[t].Predict(config, featureNames);

        double mean = predictions.Average();
        double variance = predictions.Select(p => (p - mean) * (p - mean)).Average();
        return (mean, Math.Sqrt(variance));
    }

    private DecisionTree BuildTree(
        IReadOnlyList<(Dictionary<string, int> Config, float Score)> trials,
        IReadOnlyList<string> featureNames,
        List<int> sampleIndices,
        int[] featureIndices)
    {
        return BuildNode(trials, featureNames, sampleIndices, featureIndices, depth: 0);
    }

    private DecisionTree BuildNode(
        IReadOnlyList<(Dictionary<string, int> Config, float Score)> trials,
        IReadOnlyList<string> featureNames,
        List<int> sampleIndices,
        int[] featureIndices,
        int depth)
    {
        double leafValue = sampleIndices.Average(i => trials[i].Score);

        // Stop conditions: too few samples, max depth, or no variance
        if (sampleIndices.Count < _minSamplesPerLeaf * 2 || depth >= 10)
            return new DecisionTree(leafValue);

        double totalVariance = ComputeVariance(trials, sampleIndices);
        if (totalVariance < 1e-10)
            return new DecisionTree(leafValue);

        // Try splits on each candidate feature
        int bestFeatureIdx = -1;
        int bestSplitValue = -1;
        double bestReduction = 0;
        List<int>? bestLeft = null;
        List<int>? bestRight = null;

        foreach (int fi in featureIndices)
        {
            string featureName = featureNames[fi];

            // Collect distinct values for this feature
            var values = sampleIndices
                .Select(i => trials[i].Config.GetValueOrDefault(featureName, 0))
                .Distinct()
                .ToArray();

            if (values.Length <= 1)
                continue;

            // Binary split: feature == value vs feature != value
            foreach (int splitVal in values)
            {
                var left = sampleIndices.Where(i =>
                    trials[i].Config.GetValueOrDefault(featureName, 0) == splitVal).ToList();
                var right = sampleIndices.Where(i =>
                    trials[i].Config.GetValueOrDefault(featureName, 0) != splitVal).ToList();

                if (left.Count < _minSamplesPerLeaf || right.Count < _minSamplesPerLeaf)
                    continue;

                double leftVar = ComputeVariance(trials, left);
                double rightVar = ComputeVariance(trials, right);
                double weightedVar = (left.Count * leftVar + right.Count * rightVar) / sampleIndices.Count;
                double reduction = totalVariance - weightedVar;

                if (reduction > bestReduction)
                {
                    bestReduction = reduction;
                    bestFeatureIdx = fi;
                    bestSplitValue = splitVal;
                    bestLeft = left;
                    bestRight = right;
                }
            }
        }

        // No useful split found
        if (bestFeatureIdx < 0 || bestLeft is null || bestRight is null)
            return new DecisionTree(leafValue);

        var leftChild = BuildNode(trials, featureNames, bestLeft, featureIndices, depth + 1);
        var rightChild = BuildNode(trials, featureNames, bestRight, featureIndices, depth + 1);

        return new DecisionTree(leafValue, featureNames[bestFeatureIdx], bestSplitValue, leftChild, rightChild);
    }

    private static double ComputeVariance(
        IReadOnlyList<(Dictionary<string, int> Config, float Score)> trials,
        List<int> indices)
    {
        if (indices.Count <= 1)
            return 0;

        double mean = indices.Average(i => trials[i].Score);
        return indices.Average(i =>
        {
            double diff = trials[i].Score - mean;
            return diff * diff;
        });
    }

    /// <summary>
    /// A single decision tree node. Internal nodes split on feature == value;
    /// leaves store the mean score prediction.
    /// </summary>
    internal sealed class DecisionTree
    {
        private readonly double _leafValue;
        private readonly string? _splitFeature;
        private readonly int _splitValue;
        private readonly DecisionTree? _left;   // feature == splitValue
        private readonly DecisionTree? _right;  // feature != splitValue

        public DecisionTree(double leafValue)
        {
            _leafValue = leafValue;
        }

        public DecisionTree(double leafValue, string splitFeature, int splitValue,
            DecisionTree left, DecisionTree right)
        {
            _leafValue = leafValue;
            _splitFeature = splitFeature;
            _splitValue = splitValue;
            _left = left;
            _right = right;
        }

        public double Predict(Dictionary<string, int> config, IReadOnlyList<string> featureNames)
        {
            if (_splitFeature is null)
                return _leafValue;

            int featureValue = config.GetValueOrDefault(_splitFeature, 0);
            return featureValue == _splitValue
                ? _left!.Predict(config, featureNames)
                : _right!.Predict(config, featureNames);
        }
    }
}
