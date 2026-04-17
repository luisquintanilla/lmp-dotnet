using System.Collections.Concurrent;

namespace LMP.Optimizers;

/// <summary>
/// Runs a teacher module on training data, collects traces from successful examples,
/// and fills the student module's predictors with those traces as few-shot demos.
/// This is the core compile step — the heart of DSPy-style optimization.
/// </summary>
public sealed class BootstrapFewShot : IOptimizer
{
    private readonly int _maxDemos;
    private readonly int _maxRounds;
    private readonly float _metricThreshold;

    /// <summary>
    /// Creates a new BootstrapFewShot optimizer.
    /// </summary>
    /// <param name="maxDemos">Maximum number of demos per predictor. Default is 4.</param>
    /// <param name="maxRounds">Number of bootstrap rounds. Default is 1.</param>
    /// <param name="metricThreshold">Minimum metric score for a trace to be used as a demo. Default is 1.0.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxDemos"/> is less than 1 or <paramref name="maxRounds"/> is less than 1.
    /// </exception>
    public BootstrapFewShot(
        int maxDemos = 4,
        int maxRounds = 1,
        float metricThreshold = 1.0f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);

        _maxDemos = maxDemos;
        _maxRounds = maxRounds;
        _metricThreshold = metricThreshold;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var module = ctx.Target.GetService<LmpModule>()
            ?? throw new NotSupportedException(
                $"{nameof(BootstrapFewShot)} requires an LmpModule target. Use ModuleTarget.For(module).");

        await RunAsync(module, ctx.TrainSet, ctx.Metric, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Optimizes the module by running a teacher copy on the training set,
    /// collecting traces from successful examples, and filling the student's
    /// predictors with those traces as few-shot demos.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The student module whose predictors will be filled with demos.</param>
    /// <param name="trainSet">Training examples to bootstrap from.</param>
    /// <param name="metric">Scoring function: (example, module output) → score in [0, 1].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The student module with predictors filled with demos from successful traces.</returns>
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

        await RunAsync(module, trainSet, metric, cancellationToken).ConfigureAwait(false);

        // Auto-emit .g.cs artifact
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            var evalResult = await Evaluator.EvaluateAsync(
                module, trainSet, metric, cancellationToken: cancellationToken);
            await CSharpArtifactWriter.WriteAsync(
                module, outputDir, evalResult.AverageScore, nameof(BootstrapFewShot),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return module;
    }

    private async Task RunAsync(
        LmpModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken)
    {
        if (trainSet.Count == 0)
            return;

        // Successful traces keyed by predictor name
        var successfulTraces = new ConcurrentDictionary<string, ConcurrentBag<TraceEntry>>();

        // Initialize bags for each predictor in the student
        foreach (var (name, _) in module.GetPredictors())
            successfulTraces[name] = new ConcurrentBag<TraceEntry>();

        for (int round = 0; round < _maxRounds; round++)
        {
            // Clone module to create a teacher for this round.
            // The teacher generates traces; the student receives demos.
            var teacher = module.Clone();

            // If multi-round, teacher inherits demos from previous rounds.
            if (round > 0)
            {
                CopyDemos(module, teacher);
            }

            // Process training examples sequentially to ensure trace isolation.
            // Each ForwardAsync uses teacher.Trace, which is a shared property,
            // so we create a fresh Trace per example.
            foreach (var example in trainSet)
            {
                cancellationToken.ThrowIfCancellationRequested();

                teacher.Trace = new Trace();
                try
                {
                    var output = await teacher.ForwardAsync(example.WithInputs(), cancellationToken);
                    var score = metric(example, output);

                    if (score >= _metricThreshold)
                    {
                        foreach (var entry in teacher.Trace.Entries)
                        {
                            if (successfulTraces.TryGetValue(entry.PredictorName, out var bag))
                            {
                                bag.Add(entry);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Skip failed examples — DSPy does the same.
                    // A few failures in the training set are expected.
                }
            }
        }

        // Fill student predictors with successful demos
        foreach (var (name, predictor) in module.GetPredictors())
        {
            if (successfulTraces.TryGetValue(name, out var traces) && !traces.IsEmpty)
            {
                predictor.Demos.Clear();
                foreach (var entry in traces.Take(_maxDemos))
                {
                    predictor.AddDemo(entry.Input, entry.Output);
                }
            }
        }
    }

    /// <summary>
    /// Copies demos from source module's predictors to target module's predictors.
    /// Used in multi-round bootstrapping so the teacher inherits previous demos.
    /// </summary>
    private static void CopyDemos(LmpModule source, LmpModule target)
    {
        var sourcePredictors = source.GetPredictors();
        var targetPredictors = target.GetPredictors();

        var targetMap = new Dictionary<string, IPredictor>();
        foreach (var (name, predictor) in targetPredictors)
            targetMap[name] = predictor;

        foreach (var (name, sourcePredictor) in sourcePredictors)
        {
            if (targetMap.TryGetValue(name, out var targetPredictor))
            {
                targetPredictor.Demos.Clear();
                foreach (var demo in sourcePredictor.Demos)
                {
                    targetPredictor.Demos.Add(demo);
                }
            }
        }
    }
}
