namespace LMP.Optimizers;

/// <summary>
/// Runs <see cref="BootstrapFewShot"/> N times with different random training-set shuffles,
/// evaluates each candidate on a held-out validation split, and returns the best by average score.
/// </summary>
/// <remarks>
/// <para>
/// Operates on any <see cref="IOptimizationTarget"/> — standalone <see cref="Predictor{TInput,TOutput}"/>,
/// <see cref="LmpModule"/>, <c>ChainTarget</c>, or <c>Pipeline</c>. Composes
/// <see cref="BootstrapFewShot"/> internally per trial.
/// </para>
/// <para>
/// BRS isolates predictor state across trials by cloning the target via
/// <c>WithParameters(ParameterAssignment.Empty)</c>; modules with non-predictor mutable
/// instance state are the user's responsibility (see the <see cref="IOptimizationTarget.WithParameters"/>
/// contract).
/// </para>
/// </remarks>
public sealed class BootstrapRandomSearch : IOptimizer
{
    private readonly int _numTrials;
    private readonly int _maxDemos;
    private readonly float _metricThreshold;
    private readonly int? _seed;
    private readonly int _maxConcurrency;

    /// <summary>
    /// Creates a new BootstrapRandomSearch optimizer.
    /// </summary>
    /// <param name="numTrials">Number of bootstrap trials to run. Default is 8.</param>
    /// <param name="maxDemos">Maximum number of demos per predictor in each trial. Default is 4.</param>
    /// <param name="metricThreshold">Minimum metric score for a trace to be used as a demo. Default is 1.0.</param>
    /// <param name="seed">Optional random seed for deterministic splitting and shuffling.</param>
    /// <param name="maxConcurrency">
    /// Maximum number of examples evaluated concurrently when scoring each trial candidate.
    /// Lower values reduce API rate-limit pressure. Default is 4.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="numTrials"/> is less than 1 or <paramref name="maxDemos"/> is less than 1.
    /// </exception>
    public BootstrapRandomSearch(
        int numTrials = 8,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        int? seed = null,
        int maxConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _numTrials = numTrials;
        _maxDemos = maxDemos;
        _metricThreshold = metricThreshold;
        _seed = seed;
        _maxConcurrency = maxConcurrency;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var bestState = await RunAsync(ctx.Target, ctx.TrainSet, ctx.Metric, ct).ConfigureAwait(false);
        if (bestState is not null)
            ctx.Target.ApplyState(bestState);
    }

    private async Task<TargetState?> RunAsync(
        IOptimizationTarget target,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        if (trainSet.Count == 0) return null;

        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        var (trainSplit, valSplit) = SplitDataset(trainSet, trainFraction: 0.8, rng);

        // If validation split is empty (very small dataset), use trainSplit for both
        if (valSplit.Count == 0)
            valSplit = trainSplit;

        // Bootstrap N candidates with different random training-set orderings
        var candidates = new List<IOptimizationTarget>(_numTrials);
        for (int i = 0; i < _numTrials; i++)
        {
            ct.ThrowIfCancellationRequested();

            var shuffled = trainSplit.OrderBy(_ => rng.Next()).ToList();
            // Empty-assignment clone idiom (per IOT XML doc).
            var candidate = target.WithParameters(ParameterAssignment.Empty);
            var subCtx = OptimizationContext.For(candidate, shuffled, metric);
            var bfs = new BootstrapFewShot(_maxDemos, metricThreshold: _metricThreshold);
            await bfs.OptimizeAsync(subCtx, ct).ConfigureAwait(false);
            candidates.Add(candidate);
        }

        // Evaluate all candidates on validation set in parallel via the IOT overload.
        var evaluationTasks = candidates.Select(candidate =>
            Evaluator.EvaluateAsync(candidate, valSplit, metric,
                maxConcurrency: _maxConcurrency,
                cancellationToken: ct));
        var results = await Task.WhenAll(evaluationTasks);

        // Pick best-performing candidate
        var bestIndex = 0;
        for (int i = 1; i < results.Length; i++)
        {
            if (results[i].AverageScore > results[bestIndex].AverageScore)
                bestIndex = i;
        }

        return candidates[bestIndex].GetState();
    }

    /// <summary>
    /// Optimizes the module by running <see cref="BootstrapFewShot"/> N times with
    /// different random training-set orderings, evaluating each candidate on a held-out
    /// validation split, and returning the candidate with the highest average score.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to optimize. Cloned for each trial; the input is never mutated.</param>
    /// <param name="trainSet">Training examples — split 80/20 into train/validation internally.</param>
    /// <param name="metric">Scoring function: (example, module output) → score in [0, 1].</param>
    /// <param name="options">Optional compile options controlling artifact emission.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The best-performing candidate module after N trials. Always a clone of the input.</returns>
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

        // Clone first so input module is never mutated (back-compat invariant).
        var clone = module.Clone<TModule>();
        var bestState = await RunAsync(clone, trainSet, metric, cancellationToken).ConfigureAwait(false);
        if (bestState is not null)
            clone.ApplyState(bestState);

        // Auto-emit .g.cs artifact
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            var evalResult = await Evaluator.EvaluateAsync(
                clone, trainSet, metric, cancellationToken: cancellationToken);
            await CSharpArtifactWriter.WriteAsync(
                clone, outputDir, evalResult.AverageScore, nameof(BootstrapRandomSearch),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return clone;
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
