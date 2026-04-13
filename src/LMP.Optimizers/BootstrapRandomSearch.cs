namespace LMP.Optimizers;

/// <summary>
/// Runs <see cref="BootstrapFewShot"/> N times with different random training-set shuffles,
/// evaluates each candidate on a held-out validation split, and returns the best by average score.
/// </summary>
public sealed class BootstrapRandomSearch : IOptimizer
{
    private readonly int _numTrials;
    private readonly int _maxDemos;
    private readonly float _metricThreshold;
    private readonly int? _seed;

    /// <summary>
    /// Creates a new BootstrapRandomSearch optimizer.
    /// </summary>
    /// <param name="numTrials">Number of bootstrap trials to run. Default is 8.</param>
    /// <param name="maxDemos">Maximum number of demos per predictor in each trial. Default is 4.</param>
    /// <param name="metricThreshold">Minimum metric score for a trace to be used as a demo. Default is 1.0.</param>
    /// <param name="seed">Optional random seed for deterministic splitting and shuffling.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="numTrials"/> is less than 1 or <paramref name="maxDemos"/> is less than 1.
    /// </exception>
    public BootstrapRandomSearch(
        int numTrials = 8,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);

        _numTrials = numTrials;
        _maxDemos = maxDemos;
        _metricThreshold = metricThreshold;
        _seed = seed;
    }

    /// <summary>
    /// Optimizes the module by running <see cref="BootstrapFewShot"/> N times with
    /// different random training-set orderings, evaluating each candidate on a held-out
    /// validation split, and returning the candidate with the highest average score.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to optimize. Cloned for each trial.</param>
    /// <param name="trainSet">Training examples — split 80/20 into train/validation internally.</param>
    /// <param name="metric">Scoring function: (example, module output) → score in [0, 1].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The best-performing candidate module after N trials.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/>, <paramref name="trainSet"/>, or <paramref name="metric"/> is null.
    /// </exception>
    public async Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CompileOptions? options = null,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        if (trainSet.Count == 0)
            return module;

        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        var (trainSplit, valSplit) = SplitDataset(trainSet, trainFraction: 0.8, rng);

        // If validation split is empty (very small dataset), use trainSplit for both
        if (valSplit.Count == 0)
            valSplit = trainSplit;

        // Bootstrap N candidates with different random training-set orderings
        var candidates = new List<TModule>(_numTrials);
        for (int i = 0; i < _numTrials; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var shuffled = trainSplit.OrderBy(_ => rng.Next()).ToList();
            var bootstrap = new BootstrapFewShot(_maxDemos, metricThreshold: _metricThreshold);
            var candidate = await bootstrap.CompileAsync(
                module.Clone<TModule>(), shuffled, metric, CompileOptions.RuntimeOnly, cancellationToken);
            candidates.Add(candidate);
        }

        // Evaluate all candidates on validation set in parallel
        var evaluationTasks = candidates.Select(candidate =>
            Evaluator.EvaluateAsync(candidate, valSplit, metric,
                cancellationToken: cancellationToken));
        var results = await Task.WhenAll(evaluationTasks);

        // Return best-performing candidate
        var bestIndex = 0;
        for (int i = 1; i < results.Length; i++)
        {
            if (results[i].AverageScore > results[bestIndex].AverageScore)
                bestIndex = i;
        }

        var best = candidates[bestIndex];

        // Auto-emit .g.cs artifact
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            await CSharpArtifactWriter.WriteAsync(
                best, outputDir, results[bestIndex].AverageScore, nameof(BootstrapRandomSearch),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return best;
    }

    /// <summary>
    /// Splits a dataset into train and validation subsets using Fisher-Yates shuffle.
    /// </summary>
    internal static (List<Example> Train, List<Example> Validation) SplitDataset(
        IReadOnlyList<Example> data,
        double trainFraction,
        Random rng)
    {
        // Shuffle indices
        var indices = Enumerable.Range(0, data.Count).ToArray();
        for (int i = indices.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        int trainCount = Math.Max(1, (int)(data.Count * trainFraction));
        var train = new List<Example>(trainCount);
        var val = new List<Example>(data.Count - trainCount);

        for (int i = 0; i < indices.Length; i++)
        {
            if (i < trainCount)
                train.Add(data[indices[i]]);
            else
                val.Add(data[indices[i]]);
        }

        return (train, val);
    }
}
