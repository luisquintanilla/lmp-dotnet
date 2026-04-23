namespace LMP.Optimizers;

/// <summary>
/// Runs a teacher target on training data, collects traces from successful examples,
/// and fills the student's <c>demos</c> slots with those traces as few-shot demos.
/// This is the core compile step — the heart of DSPy-style optimization.
/// </summary>
/// <remarks>
/// <para>
/// BFS discovers its work through <see cref="IOptimizationTarget.GetParameterSpace"/>:
/// any parameter keyed <c>"demos"</c> (root) or <c>"{predictorName}.demos"</c> (module
/// leaf) with kind <see cref="Subset"/> becomes a demo slot. Demos are applied via
/// <see cref="IOptimizationTarget.WithParameters"/> using cumulative
/// <see cref="ParameterAssignment"/> values across rounds.
/// </para>
/// <para>
/// Supported target shapes:
/// <list type="bullet">
/// <item><description>
/// <b>Standalone <see cref="Predictor{TInput,TOutput}"/></b> — emits a single root <c>"demos"</c> slot.
/// </description></item>
/// <item><description>
/// <b><see cref="LmpModule"/> fractal tree</b> — emits <c>"{predictorName}.demos"</c> slots, one per
/// <see cref="IOptimizationTarget"/>-implementing child predictor.
/// </description></item>
/// <item><description>
/// <b><c>ChainTarget</c> / <c>Pipeline</c> of <see cref="LmpModule"/> children</b> — emits
/// <c>"child_{i}.{predictorName}.demos"</c> slots. Trace entries carry the symmetric
/// <c>"child_{i}.{predictorName}"</c> prefix (see <see cref="IOptimizationTarget.ExecuteAsync"/>),
/// so demos are routed to the correct stage. Nesting composes (e.g.,
/// <c>"child_0.child_1.{predictorName}.demos"</c>).
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Explicitly NOT supported: <see cref="Predictor{TInput,TOutput}"/> inside a bare
/// <c>ChainTarget</c>/<c>Pipeline</c> (without an enclosing <see cref="LmpModule"/>). A bare
/// <see cref="Predictor{TInput,TOutput}"/> exposes a root <c>"demos"</c> slot, so composite
/// prefixing produces <c>"child_{i}.demos"</c> slot keys that do not collide across children
/// but are not routed back into the predictor's root slot. Tracked as future work (T2d.6).
/// </para>
/// </remarks>
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
        await RunAsync(ctx.Target, ctx.TrainSet, ctx.Metric, ct).ConfigureAwait(false);
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
    /// <param name="options">Optional compile options controlling artifact emission.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same <paramref name="module"/> instance with predictors filled with demos.</returns>
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
        IOptimizationTarget target,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        if (trainSet.Count == 0) return;

        // 1. Discover demo slots from the parameter space.
        //    Root "demos" and per-predictor "{prefix}.demos" are both supported.
        var space = target.GetParameterSpace();
        var demoSlots = new List<string>();
        foreach (var (key, kind) in space.Parameters)
        {
            if (kind is not Subset) continue;
            if (key == "demos" || key.EndsWith(".demos", StringComparison.Ordinal))
                demoSlots.Add(key);
        }
        if (demoSlots.Count == 0) return;

        // 2. Initialize one bucket per slot, keyed by the slot key itself.
        var buckets = new Dictionary<string, List<TraceEntry>>();
        foreach (var slotKey in demoSlots)
            buckets[slotKey] = new List<TraceEntry>();

        // 3. Round loop with cumulative assignment.
        var cumulative = ParameterAssignment.Empty;
        for (int round = 0; round < _maxRounds; round++)
        {
            var teacher = target.WithParameters(cumulative);

            foreach (var example in trainSet)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (output, trace) = await teacher.ExecuteAsync(example.WithInputs(), ct)
                        .ConfigureAwait(false);
                    var score = metric(example, output);
                    if (score >= _metricThreshold)
                    {
                        foreach (var entry in trace.Entries)
                        {
                            if (buckets.TryGetValue("demos", out var rootBucket))
                                rootBucket.Add(entry);
                            if (buckets.TryGetValue($"{entry.PredictorName}.demos", out var namedBucket))
                                namedBucket.Add(entry);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Skip failed examples — DSPy does the same.
                }
            }

            // 4. Rebuild cumulative assignment from current buckets.
            cumulative = ParameterAssignment.Empty;
            foreach (var slotKey in demoSlots)
            {
                var bucket = buckets[slotKey];
                if (bucket.Count == 0) continue;
                var demos = bucket.Take(_maxDemos)
                    .Select(e => (object)(e.Input, e.Output))
                    .ToList();
                cumulative = cumulative.With(slotKey, (IReadOnlyList<object>)demos);
            }
        }

        // 5. Apply final assignment back to the original target via state round-trip.
        var optimized = target.WithParameters(cumulative);
        target.ApplyState(optimized.GetState());
    }
}
