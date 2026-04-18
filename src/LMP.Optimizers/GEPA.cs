using Microsoft.Extensions.AI;

namespace LMP.Optimizers;

/// <summary>
/// GEPA (Genetic-Pareto Evolutionary Algorithm) — reflection-driven instruction optimization.
/// Unlike Bayesian optimizers (MIPROv2) that search based on scores alone, GEPA captures
/// execution traces, uses an LLM to diagnose failures, and proposes targeted instruction fixes.
/// </summary>
/// <remarks>
/// <para>
/// GEPA evolves <b>instructions only</b> (not demos). It maintains a Pareto frontier
/// of candidates tracked per-instance on a stable validation set, and uses two operations:
/// </para>
/// <list type="bullet">
/// <item><description><b>Reflective Mutation:</b> Run candidate on mini-batch → capture traces →
/// LLM analyzes failures (including expected labels) → proposes improved instructions.
/// Uses round-robin component selection (one predictor per iteration).</description></item>
/// <item><description><b>Merge:</b> Combine instructions from two Pareto-optimal parents,
/// taking each predictor's instruction from whichever parent scored better.</description></item>
/// </list>
/// <para>
/// Inspired by <see href="https://github.com/gepa-ai/gepa">gepa-ai/gepa</see>,
/// now integrated into DSPy as <c>dspy.GEPA</c>.
/// </para>
/// </remarks>
public sealed class GEPA : IOptimizer
{
    private readonly IChatClient _reflectionClient;
    private readonly int _maxIterations;
    private readonly int _miniBatchSize;
    private readonly int _mergeEvery;
    private readonly int _maxConcurrency;
    private readonly int? _seed;
    private readonly IProgress<GEPAProgressReport>? _progress;
    // Per-run external observations from ctx.ReflectionLog, threaded through the call chain
    private IReadOnlyList<ReflectionEntry> _externalObservations = [];

    /// <summary>
    /// Creates a new GEPA optimizer.
    /// </summary>
    /// <param name="reflectionClient">
    /// LLM used for failure diagnosis and instruction generation. Can be the same model
    /// as the module's client, or a cheaper/faster model for cost efficiency.
    /// </param>
    /// <param name="maxIterations">Maximum evolutionary iterations. Default is 50.</param>
    /// <param name="miniBatchSize">Examples per mini-batch for reflection. Default is 5.</param>
    /// <param name="mergeEvery">Perform a merge operation every N iterations. Default is 5.</param>
    /// <param name="maxConcurrency">
    /// Maximum concurrent <see cref="LmpModule.ForwardAsync"/> calls during full-set evaluation.
    /// Default is 4. For modules with multiple concurrent sub-predictors, the effective number of
    /// concurrent API calls is <c>maxConcurrency × predictors_per_forward</c>. Reduce this value
    /// if you encounter rate limit errors or HTTP timeouts during optimization.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <param name="progress">Optional progress reporter for iteration updates.</param>
    public GEPA(
        IChatClient reflectionClient,
        int maxIterations = 50,
        int miniBatchSize = 5,
        int mergeEvery = 5,
        int maxConcurrency = 4,
        int? seed = null,
        IProgress<GEPAProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reflectionClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(miniBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(mergeEvery, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _reflectionClient = reflectionClient;
        _maxIterations = maxIterations;
        _miniBatchSize = miniBatchSize;
        _mergeEvery = mergeEvery;
        _maxConcurrency = maxConcurrency;
        _seed = seed;
        _progress = progress;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var module = ctx.Target.GetService<LmpModule>()
            ?? throw new NotSupportedException(
                $"{nameof(GEPA)} requires an LmpModule target. Use ModuleTarget.For(module).");

        // Capture external reflection entries (e.g., from EvaluationCritique) for this run
        _externalObservations = ctx.ReflectionLog.Entries;

        // Auto-register StringValued description params for AIFunction tools in the search space
        AddToolDescriptionParams(ctx);

        // When trajectory metric is set, sample trajectory observations before evolution.
        // Trajectory content is added to the reflection log so GEPA's instruction reflection
        // prompts include multi-turn execution context alongside single-turn failure traces.
        if (ctx.TrajectoryMetric != null && ctx.TrainSet.Count > 0)
            await SampleTrajectoryObservationsAsync(ctx, ct).ConfigureAwait(false);

        var best = await CompileAsync(module, ctx.TrainSet, ctx.Metric, CompileOptions.RuntimeOnly, ct,
            trajectoryMetric: ctx.TrajectoryMetric)
            .ConfigureAwait(false);

        if (!ReferenceEquals(best, module))
            ctx.Target.ApplyState(TargetState.From(best.GetState()));
    }

    /// <inheritdoc />
    /// <param name="trajectoryMetric">
    /// Optional trajectory metric. When set, evaluation loops call
    /// <see cref="IOptimizationTarget.ExecuteTrajectoryAsync"/> and score with
    /// <see cref="ITrajectoryMetric.ScoreAsync"/> instead of <paramref name="metric"/>.
    /// Full-set evaluation runs sequentially in trajectory mode to avoid trace races.
    /// </param>
    public async Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CompileOptions? options = null,
        CancellationToken cancellationToken = default,
        ITrajectoryMetric? trajectoryMetric = null)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        if (trainSet.Count == 0)
            return module;

        cancellationToken.ThrowIfCancellationRequested();

        var rng = _seed.HasValue ? new Random(_seed.Value) : new Random();
        var frontier = new ParetoFrontier<TModule>();
        int componentIndex = 0; // Round-robin predictor index
        float bestIndividualScore = float.MinValue; // Best single-candidate average score seen so far

        // Seed the frontier: evaluate initial module on the FULL trainSet (stable valset)
        var initialScores = await EvaluateOnFullSet(module, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
        frontier.Add(module, initialScores);

        float baselineAvg = initialScores.Average(s => s.Score);
        bestIndividualScore = baselineAvg;

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (iter % _mergeEvery == 0 && iter > 0 && frontier.Count >= 2)
            {
                // Merge: per-predictor crossover of two Pareto-optimal parents
                var (mergeCandidate, p1MiniBatch, p2MiniBatch, mergeMiniBatch) =
                    await MergeAsync(frontier, trainSet, metric, rng, cancellationToken, trajectoryMetric);

                // Gate check: merge must not be worse than both parents on the same mini-batch
                float mergeMiniBatchScore = await EvaluateMiniBatchScore(
                    mergeCandidate, mergeMiniBatch, metric, cancellationToken, trajectoryMetric);

                if (mergeMiniBatchScore < Math.Max(p1MiniBatch, p2MiniBatch))
                {
                    _progress?.Report(new GEPAProgressReport(
                        iter + 1, _maxIterations, frontier.Count,
                        bestIndividualScore,
                        GEPAIterationType.Merge, false));
                    continue;
                }

                // Passed gate: evaluate on the full set and add to frontier
                var mergeScores = await EvaluateOnFullSet(mergeCandidate, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
                frontier.Add(mergeCandidate, mergeScores);

                var mergeAvg = mergeScores.Average(s => s.Score);
                if (mergeAvg > bestIndividualScore) bestIndividualScore = mergeAvg;

                _progress?.Report(new GEPAProgressReport(
                    iter + 1, _maxIterations, frontier.Count,
                    bestIndividualScore,
                    GEPAIterationType.Merge, true));
            }
            else
            {
                // Reflective mutation with round-robin component selection
                var (mutated, parentMiniBatchScore, miniBatch) = await ReflectAndMutate(
                    frontier, trainSet, metric, rng, componentIndex, cancellationToken, trajectoryMetric);
                componentIndex++;

                // Gate check: evaluate mutated on the SAME mini-batch used for reflection
                float mutatedMiniBatchScore = await EvaluateMiniBatchScore(
                    mutated, miniBatch, metric, cancellationToken, trajectoryMetric);

                if (mutatedMiniBatchScore <= parentMiniBatchScore)
                {
                    _progress?.Report(new GEPAProgressReport(
                        iter + 1, _maxIterations, frontier.Count,
                        bestIndividualScore,
                        GEPAIterationType.Mutation, false));
                    continue; // Mini-batch didn't improve — skip expensive full eval
                }

                // Passed gate: evaluate on the FULL trainSet for Pareto tracking
                var fullScores = await EvaluateOnFullSet(mutated, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
                frontier.Add(mutated, fullScores);

                var fullAvg = fullScores.Average(s => s.Score);
                if (fullAvg > bestIndividualScore) bestIndividualScore = fullAvg;

                _progress?.Report(new GEPAProgressReport(
                    iter + 1, _maxIterations, frontier.Count,
                    bestIndividualScore,
                    GEPAIterationType.Mutation, true));
            }
        }

        var best = frontier.Best ?? module;

        // Auto-emit .g.cs artifact
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            var evalResult = await Evaluator.EvaluateAsync(
                best, trainSet, metric, cancellationToken: cancellationToken);
            await CSharpArtifactWriter.WriteAsync(
                best, outputDir, evalResult.AverageScore, nameof(GEPA),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return best;
    }

    /// <summary>
    /// Reflective mutation: select a candidate from the frontier, run on mini-batch,
    /// capture traces, ask the reflection LLM to diagnose failures and propose better instructions.
    /// Uses round-robin to optimize ONE predictor per iteration.
    /// </summary>
    /// <returns>
    /// The mutated candidate, the parent's score on the mini-batch (for gate check),
    /// and the mini-batch examples used.
    /// </returns>
    private async Task<(TModule Candidate, float ParentMiniBatchScore, List<Example> MiniBatch)> ReflectAndMutate<TModule>(
        ParetoFrontier<TModule> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        int componentIndex,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
        where TModule : LmpModule
    {
        // Select a parent from the frontier
        var parent = frontier.Frontier[rng.Next(frontier.Count)];
        var candidate = parent.Clone<TModule>();

        // Sample mini-batch for reflection
        var miniBatch = SampleMiniBatch(trainSet, rng);
        var traceResults = new List<(Example Example, object Output, float Score, Trace Trace)>();

        foreach (var example in miniBatch)
        {
            var trace = new Trace();
            candidate.Trace = trace;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await candidate.ForwardAsync(example.WithInputs(), cancellationToken);
                    float score;
                    if (trajectoryMetric != null)
                    {
                        var traj = Trajectory.FromTrace(trace, example);
                        score = await trajectoryMetric.ScoreAsync(traj, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        score = metric(example, output);
                    }
                    traceResults.Add((example, output, score, trace));
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), cancellationToken);
                }
                catch
                {
                    // Score as 0 for failed examples
                    traceResults.Add((example, "error", 0f, trace));
                    break;
                }
            }
        }

        float parentMiniBatchScore = traceResults.Count > 0
            ? traceResults.Average(r => r.Score)
            : 0f;

        if (traceResults.Count == 0)
            return (candidate, parentMiniBatchScore, miniBatch);

        // Round-robin: select ONE predictor to optimize
        var predictors = candidate.GetPredictors().ToList();
        if (predictors.Count == 0)
            return (candidate, parentMiniBatchScore, miniBatch);

        var (targetName, targetPredictor) = predictors[componentIndex % predictors.Count];

        // Get failed traces (score < 1.0 to catch partial failures too)
        var failedTraces = traceResults
            .Where(r => r.Score < 1.0f)
            .ToList();

        if (failedTraces.Count > 0)
        {
            try
            {
                // Build trajectory-derived reflection entries for failed examples (trajectory mode only).
                // These supplement the predictor-level trace analysis with multi-turn execution context.
                IReadOnlyList<ReflectionEntry>? trajectoryObs = null;
                if (trajectoryMetric != null)
                {
                    trajectoryObs = failedTraces.Select((r, i) =>
                    {
                        var turnSummary = string.Join("; ", Trajectory.FromTrace(r.Trace, r.Example)
                            .Turns.Select(t => $"[{t.Kind}] {t.Output}"));
                        return new ReflectionEntry(
                            Text: $"Failed example {i + 1} (trajectory score {r.Score:F2}): {turnSummary}",
                            Source: "GEPA.trajectory",
                            Scope: ReflectionScope.Global,
                            Score: r.Score);
                    }).ToList();
                }

                var newInstruction = await ReflectOnPredictor(
                    targetName, targetPredictor.Instructions, failedTraces, cancellationToken, trajectoryObs);

                if (!string.IsNullOrWhiteSpace(newInstruction))
                    targetPredictor.Instructions = newInstruction;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Reflection failed (e.g., content filter, transient API error).
                // Skip instruction mutation for this iteration and continue optimization.
            }
        }

        return (candidate, parentMiniBatchScore, miniBatch);
    }

    /// <summary>
    /// Asks the reflection LLM to analyze failures for a specific predictor
    /// and propose an improved instruction. Delegates to the shared
    /// <see cref="InstructionReflector"/> helper used by GEPA and SIMBA.
    /// </summary>
    private Task<string> ReflectOnPredictor(
        string predictorName,
        string currentInstruction,
        List<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken,
        IReadOnlyList<ReflectionEntry>? trajectoryObservations = null)
    {
        IReadOnlyList<ReflectionEntry> observations = trajectoryObservations == null
            ? _externalObservations
            : [.. _externalObservations, .. trajectoryObservations];
        return InstructionReflector.ReflectAsync(
            _reflectionClient, predictorName, currentInstruction, failedTraces,
            cancellationToken, externalObservations: observations);
    }

    /// <summary>
    /// Per-predictor crossover: combine instructions from two Pareto-optimal parents.
    /// For each predictor independently, selects the instruction via weighted random
    /// using each parent's aggregate training score as the weight. This allows combining
    /// a strong urgency instruction from one lineage with a strong sentiment instruction
    /// from another. Returns the merge candidate along with both parents' mini-batch scores
    /// and the mini-batch used, so the caller can apply a gate check.
    /// </summary>
    private async Task<(TModule Candidate, float P1MiniBatchScore, float P2MiniBatchScore, List<Example> MiniBatch)> MergeAsync<TModule>(
        ParetoFrontier<TModule> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
        where TModule : LmpModule
    {
        var (parent1, aggScore1, parent2, aggScore2) = frontier.SelectParents(rng);
        var child = parent1.Clone<TModule>();

        // Evaluate both parents on the same mini-batch (used for gate check by caller)
        var miniBatch = SampleMiniBatch(trainSet, rng);
        float p1Score = await EvaluateMiniBatchScore(parent1, miniBatch, metric, cancellationToken, trajectoryMetric);
        float p2Score = await EvaluateMiniBatchScore(parent2, miniBatch, metric, cancellationToken, trajectoryMetric);

        // Per-predictor weighted random crossover using aggregate training scores.
        // P(parent1 wins predictor i) = aggScore1 / (aggScore1 + aggScore2)
        float totalAgg = aggScore1 + aggScore2;
        double p1Weight = totalAgg > 0f ? (double)aggScore1 / totalAgg : 0.5;

        var p2Predictors = parent2.GetPredictors().ToDictionary(p => p.Name, p => p.Predictor);

        foreach (var (name, childPredictor) in child.GetPredictors())
        {
            if (!p2Predictors.TryGetValue(name, out var pred2)) continue;

            // If both parents have identical instructions, no crossover needed
            if (childPredictor.Instructions == pred2.Instructions) continue;

            // Weighted random: assign this predictor's instruction from parent2 based on its weight
            if (rng.NextDouble() >= p1Weight)
                childPredictor.Instructions = pred2.Instructions;
        }

        return (child, p1Score, p2Score, miniBatch);
    }

    /// <summary>
    /// Evaluates a module on the FULL training set. Returns per-example scores
    /// in a STABLE order (index i always corresponds to trainSet[i]) for Pareto
    /// frontier tracking. All candidates must be evaluated with this same order.
    /// Runs examples concurrently for performance (sequential in trajectory mode to avoid
    /// trace races on the shared module).
    /// </summary>
    private static async Task<IReadOnlyList<ExampleResult>> EvaluateOnFullSet<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        int maxConcurrency = 4,
        ITrajectoryMetric? trajectoryMetric = null)
        where TModule : LmpModule
    {
        var results = new ExampleResult[trainSet.Count];
        var evalModule = module.Clone<TModule>();

        if (trajectoryMetric != null)
        {
            // Sequential loop: trajectory scoring requires per-example Trace capture.
            // Concurrent calls on a shared module would race on evalModule.Trace.
            for (int i = 0; i < trainSet.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trace = new Trace();
                evalModule.Trace = trace;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var output = await evalModule.ForwardAsync(trainSet[i].WithInputs(), cancellationToken);
                        var traj = Trajectory.FromTrace(trace, trainSet[i]);
                        var score = await trajectoryMetric.ScoreAsync(traj, cancellationToken).ConfigureAwait(false);
                        results[i] = new ExampleResult(trainSet[i], output, score);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), cancellationToken);
                    }
                    catch
                    {
                        results[i] = new ExampleResult(trainSet[i], "error", 0f);
                    }
                }
            }

            return results;
        }

        // Run concurrently but write results by position to maintain stable order.
        // Multiple concurrent ForwardAsync calls share evalModule.Trace — safe because
        // we don't use trace data for full-set evals (only for reflection mini-batches).
        await Parallel.ForEachAsync(
            Enumerable.Range(0, trainSet.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (i, ct) =>
            {
                // Retry up to 3 times with exponential back-off (2 s, 4 s) so that
                // transient rate-limit (429) errors don't score candidates as 0 and
                // falsely bias frontier.Best toward the initial module.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var output = await evalModule.ForwardAsync(trainSet[i].WithInputs(), ct);
                        var score = metric(trainSet[i], output);
                        results[i] = new ExampleResult(trainSet[i], output, score);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), ct);
                    }
                    catch
                    {
                        results[i] = new ExampleResult(trainSet[i], "error", 0f);
                    }
                }
            });

        return results;
    }

    /// <summary>
    /// Evaluates a module on a specific mini-batch and returns the average score.
    /// Used for gate checks and merge evidence.
    /// </summary>
    private static async Task<float> EvaluateMiniBatchScore<TModule>(
        TModule module,
        List<Example> miniBatch,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
        where TModule : LmpModule
    {
        float totalScore = 0f;
        int count = 0;
        var evalModule = module.Clone<TModule>();

        foreach (var example in miniBatch)
        {
            var trace = new Trace();
            evalModule.Trace = trace;
            bool scored = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await evalModule.ForwardAsync(example.WithInputs(), cancellationToken);
                    if (trajectoryMetric != null)
                    {
                        var traj = Trajectory.FromTrace(trace, example);
                        totalScore += await trajectoryMetric.ScoreAsync(traj, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        totalScore += metric(example, output);
                    }
                    count++;
                    scored = true;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), cancellationToken);
                }
                catch { break; }
            }
            if (!scored) count++; // count the failure as score 0
        }

        return count > 0 ? totalScore / count : 0f;
    }

    /// <summary>
    /// Samples a random mini-batch from the training set.
    /// </summary>
    private List<Example> SampleMiniBatch(IReadOnlyList<Example> trainSet, Random rng)
        => InstructionReflector.SampleMiniBatch(trainSet, rng, _miniBatchSize);

    /// <summary>
    /// Scans <see cref="OptimizationContext.SearchSpace"/> for <see cref="Subset"/> parameters
    /// whose pool contains <see cref="AIFunction"/> entries, and registers a
    /// <see cref="StringValued"/> description parameter for each discovered function.
    /// This allows downstream optimizers to evolve tool descriptions alongside the tool selection.
    /// </summary>
    private static void AddToolDescriptionParams(OptimizationContext ctx)
    {
        foreach (var (paramName, kind) in ctx.SearchSpace.Parameters)
        {
            if (kind is not Subset subset) continue;
            foreach (var poolItem in subset.Pool)
            {
                if (poolItem is not AIFunction fn) continue;
                var descKey = $"{paramName}.{fn.Name}.description";
                if (!ctx.SearchSpace.Parameters.ContainsKey(descKey))
                    ctx.SearchSpace = ctx.SearchSpace.Add(descKey, new StringValued(fn.Description));
            }
        }
    }

    /// <summary>
    /// Evaluates a sample of training examples via <see cref="IOptimizationTarget.ExecuteTrajectoryAsync"/>
    /// and adds trajectory observations to <see cref="OptimizationContext.ReflectionLog"/> and
    /// <see cref="OptimizationContext.TrialHistory"/> so that GEPA's reflection prompts include
    /// multi-turn execution context. Called only when
    /// <see cref="OptimizationContext.TrajectoryMetric"/> is non-null.
    /// </summary>
    private async Task SampleTrajectoryObservationsAsync(OptimizationContext ctx, CancellationToken ct)
    {
        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        var sampleSize = Math.Min(5, ctx.TrainSet.Count);
        var sample = InstructionReflector.SampleMiniBatch(ctx.TrainSet, rng, sampleSize);

        foreach (var example in sample)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var trajectory = await ctx.Target
                    .ExecuteTrajectoryAsync(example.WithInputs(), source: example, ct: ct)
                    .ConfigureAwait(false);
                var score = await ctx.TrajectoryMetric!.ScoreAsync(trajectory, ct).ConfigureAwait(false);

                ctx.TrialHistory.Add(new Trial(
                    Score: score,
                    Cost: new TrialCost(0, 0, 0, 0, 0),
                    Notes: "GEPA:trajectory"));

                if (trajectory.TurnCount > 0 && ctx.ReflectionLog != ReflectionLog.Empty)
                {
                    var spanText = string.Join("\n", trajectory.Turns
                        .Select((t, i) => $"[step {i + 1}] {t.Kind}: {t.Input} → {t.Output}"));
                    ctx.ReflectionLog.Add(
                        text: $"Trajectory score={score:F2}: {spanText}",
                        source: nameof(GEPA),
                        scope: ReflectionScope.Global,
                        score: score);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Skip failed trajectory samples silently */ }
        }

        // Refresh external observations to include any newly-added trajectory entries.
        _externalObservations = ctx.ReflectionLog.Entries;
    }
}

/// <summary>
/// Progress report emitted by <see cref="GEPA"/> after each iteration.
/// </summary>
/// <param name="Iteration">Current iteration (1-based).</param>
/// <param name="TotalIterations">Total iterations requested.</param>
/// <param name="FrontierSize">Number of candidates currently in the Pareto frontier.</param>
/// <param name="BestScore">Average training-set score of the best individual candidate seen so far.
/// This is a monotonically non-decreasing value that tracks the actual score of the single
/// best module produced by the optimizer — the score a user should expect at final evaluation
/// (subject to train/dev gap). It is distinct from the Pareto ensemble score, which represents
/// the theoretical ceiling of an oracle that picks the best candidate per training instance.</param>
/// <param name="IterationType">Whether this iteration was a mutation or merge.</param>
/// <param name="Passed">
/// Whether the gate check passed (<see langword="true"/> = accepted and added to frontier;
/// <see langword="false"/> = rejected/skipped). Always non-null for both mutation and merge iterations.
/// </param>
public sealed record GEPAProgressReport(
    int Iteration,
    int TotalIterations,
    int FrontierSize,
    float BestScore,
    GEPAIterationType IterationType,
    bool? Passed);

/// <summary>
/// Type of GEPA iteration.
/// </summary>
public enum GEPAIterationType
{
    /// <summary>Reflective mutation of one predictor.</summary>
    Mutation,
    /// <summary>Merge crossover from two Pareto-optimal parents.</summary>
    Merge
}
