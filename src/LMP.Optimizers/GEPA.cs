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
/// Post-T2g GEPA operates on <see cref="IOptimizationTarget"/> via the fractal seam:
/// predictors are enumerated with <see cref="PredictorWalker"/>, per-predictor mutations
/// build a <see cref="ParameterAssignment"/> keyed by fully-qualified predictor path, and
/// the mutant is produced via <see cref="IOptimizationTarget.WithParameters"/>. This makes
/// GEPA composite-aware (works on <see cref="ChainTarget"/>/<see cref="Pipeline{TIn,TOut}"/>
/// of <see cref="LmpModule"/>s) without changing its optimization behavior on bare modules.
/// </para>
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
    /// Maximum concurrent <see cref="IOptimizationTarget.ExecuteAsync"/> calls during
    /// full-set evaluation. Default is 4. For modules with multiple concurrent
    /// sub-predictors, the effective number of concurrent API calls is
    /// <c>maxConcurrency × predictors_per_forward</c>. Reduce this value if you encounter
    /// rate limit errors or HTTP timeouts during optimization.
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

        // Capture external reflection entries (e.g., from EvaluationCritique) for this run
        _externalObservations = ctx.ReflectionLog.Entries;

        // Auto-register StringValued description params for AIFunction tools in the search space
        AddToolDescriptionParams(ctx);

        // When trajectory metric is set, sample trajectory observations before evolution.
        // Trajectory content is added to the reflection log so GEPA's instruction reflection
        // prompts include multi-turn execution context alongside single-turn failure traces.
        if (ctx.TrajectoryMetric != null && ctx.TrainSet.Count > 0)
            await SampleTrajectoryObservationsAsync(ctx, ct).ConfigureAwait(false);

        var best = await RunOptimizationLoopAsync(
            ctx.Target, ctx.TrainSet, ctx.Metric, ct, ctx.TrajectoryMetric).ConfigureAwait(false);

        if (!ReferenceEquals(best, ctx.Target))
            ctx.Target.ApplyState(best.GetState());
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

        var bestTarget = await RunOptimizationLoopAsync(
            module, trainSet, metric, cancellationToken, trajectoryMetric).ConfigureAwait(false);

        var bestModule = bestTarget as TModule ?? module;

        // Auto-emit .g.cs artifact (module-only today; composite artifact emission deferred to T4/T7)
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            var evalResult = await Evaluator.EvaluateAsync(
                bestModule, trainSet, metric, cancellationToken: cancellationToken);
            await CSharpArtifactWriter.WriteAsync(
                bestModule, outputDir, evalResult.AverageScore, nameof(GEPA),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return bestModule;
    }

    /// <summary>
    /// Runs the GEPA evolutionary loop against an <see cref="IOptimizationTarget"/> seed,
    /// returning the best candidate found. Unifies the module and composite code paths so
    /// both <see cref="CompileAsync{TModule}"/> and <see cref="OptimizeAsync"/> go through
    /// the same inner loop.
    /// </summary>
    private async Task<IOptimizationTarget> RunOptimizationLoopAsync(
        IOptimizationTarget seed,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric)
    {
        if (trainSet.Count == 0)
            return seed;

        cancellationToken.ThrowIfCancellationRequested();

        var rng = _seed.HasValue ? new Random(_seed.Value) : new Random();
        var frontier = new ParetoFrontier<IOptimizationTarget>();
        int componentIndex = 0; // Round-robin predictor index
        float bestIndividualScore = float.MinValue;

        // Seed the frontier: evaluate initial target on the FULL trainSet (stable valset)
        var initialScores = await EvaluateOnFullSet(
            seed, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
        frontier.Add(seed, initialScores);

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
                var mergeScores = await EvaluateOnFullSet(
                    mergeCandidate, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
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
                var fullScores = await EvaluateOnFullSet(
                    mutated, trainSet, metric, cancellationToken, _maxConcurrency, trajectoryMetric);
                frontier.Add(mutated, fullScores);

                var fullAvg = fullScores.Average(s => s.Score);
                if (fullAvg > bestIndividualScore) bestIndividualScore = fullAvg;

                _progress?.Report(new GEPAProgressReport(
                    iter + 1, _maxIterations, frontier.Count,
                    bestIndividualScore,
                    GEPAIterationType.Mutation, true));
            }
        }

        return frontier.TotalCandidates > 0 ? frontier.Best : seed;
    }

    /// <summary>
    /// Reflective mutation: select a parent from the frontier, run on mini-batch through
    /// a pristine <c>evalClone</c> (produced via <c>parent.WithParameters(Empty)</c> so
    /// trace capture does not depend on mutating shared state), capture traces, ask the
    /// reflection LLM to diagnose failures, and produce a mutant via
    /// <c>parent.WithParameters(pa)</c>. Uses round-robin to optimize ONE predictor per
    /// iteration.
    /// </summary>
    private async Task<(IOptimizationTarget Candidate, float ParentMiniBatchScore, List<Example> MiniBatch)> ReflectAndMutate(
        ParetoFrontier<IOptimizationTarget> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        int componentIndex,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
    {
        // Select a parent from the frontier
        var parent = frontier.Frontier[rng.Next(frontier.Count)];

        // Pristine clone for trace collection (Q4: decouples eval from mutation path).
        var evalClone = parent.WithParameters(ParameterAssignment.Empty);

        // Sample mini-batch for reflection
        var miniBatch = SampleMiniBatch(trainSet, rng);
        var traceResults = new List<(Example Example, object Output, float Score, Trace Trace)>();

        foreach (var example in miniBatch)
        {
            Trace capturedTrace = new();
            object capturedOutput = "error";
            float capturedScore = 0f;
            bool scored = false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (output, trace) = await evalClone.ExecuteAsync(example.WithInputs(), cancellationToken)
                        .ConfigureAwait(false);
                    capturedOutput = output;
                    capturedTrace = trace;
                    if (trajectoryMetric != null)
                    {
                        var traj = Trajectory.FromTrace(trace, example);
                        capturedScore = await trajectoryMetric.ScoreAsync(traj, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        capturedScore = metric(example, output);
                    }
                    scored = true;
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
                    break;
                }
            }

            traceResults.Add((example, capturedOutput, scored ? capturedScore : 0f, capturedTrace));
        }

        float parentMiniBatchScore = traceResults.Count > 0
            ? traceResults.Average(r => r.Score)
            : 0f;

        if (traceResults.Count == 0)
            return (parent, parentMiniBatchScore, miniBatch);

        // Round-robin: select ONE predictor to optimize, using the walker so composite
        // paths (e.g., "child_0.classify") are enumerated symmetrically with trace names.
        var predictors = PredictorWalker.Enumerate(parent).ToList();
        if (predictors.Count == 0)
            return (parent, parentMiniBatchScore, miniBatch);

        var (targetPath, targetPredictor) = predictors[componentIndex % predictors.Count];

        // Get failed traces (score < 1.0 to catch partial failures too)
        var failedTraces = traceResults
            .Where(r => r.Score < 1.0f)
            .ToList();

        if (failedTraces.Count == 0)
            return (parent, parentMiniBatchScore, miniBatch);

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
                targetPath, targetPredictor.Instructions, failedTraces, cancellationToken, trajectoryObs);

            if (string.IsNullOrWhiteSpace(newInstruction))
                return (parent, parentMiniBatchScore, miniBatch);

            // Produce mutant from pristine parent via the seam (Q4: NOT from evalClone).
            var pa = ParameterAssignment.Empty.With($"{targetPath}.instructions", newInstruction);
            var mutant = parent.WithParameters(pa);
            return (mutant, parentMiniBatchScore, miniBatch);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Reflection failed (e.g., content filter, transient API error).
            // Skip instruction mutation for this iteration and continue optimization.
            return (parent, parentMiniBatchScore, miniBatch);
        }
    }

    /// <summary>
    /// Asks the reflection LLM to analyze failures for a specific predictor
    /// and propose an improved instruction. Delegates to the shared
    /// <see cref="InstructionReflector"/> helper used by GEPA and SIMBA.
    /// </summary>
    private Task<string> ReflectOnPredictor(
        string predictorPath,
        string currentInstruction,
        List<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken,
        IReadOnlyList<ReflectionEntry>? trajectoryObservations = null)
    {
        IReadOnlyList<ReflectionEntry> observations = trajectoryObservations == null
            ? _externalObservations
            : [.. _externalObservations, .. trajectoryObservations];
        return InstructionReflector.ReflectAsync(
            _reflectionClient, predictorPath, currentInstruction, failedTraces,
            cancellationToken, externalObservations: observations);
    }

    /// <summary>
    /// Per-predictor crossover: combine instructions from two Pareto-optimal parents.
    /// Enumerates parent1's predictors via <see cref="PredictorWalker"/> (deterministic
    /// iteration order) and looks up parent2's predictor at the same path; when both
    /// differ, applies a weighted RNG draw and accumulates all selected paths into a
    /// single <see cref="ParameterAssignment"/> applied once via
    /// <see cref="IOptimizationTarget.WithParameters"/>.
    /// </summary>
    private async Task<(IOptimizationTarget Candidate, float P1MiniBatchScore, float P2MiniBatchScore, List<Example> MiniBatch)> MergeAsync(
        ParetoFrontier<IOptimizationTarget> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
    {
        var (parent1, aggScore1, parent2, aggScore2) = frontier.SelectParents(rng);

        // Evaluate both parents on the same mini-batch (used for gate check by caller)
        var miniBatch = SampleMiniBatch(trainSet, rng);
        float p1Score = await EvaluateMiniBatchScore(parent1, miniBatch, metric, cancellationToken, trajectoryMetric);
        float p2Score = await EvaluateMiniBatchScore(parent2, miniBatch, metric, cancellationToken, trajectoryMetric);

        // Per-predictor weighted random crossover using aggregate training scores.
        // P(parent1 wins predictor i) = aggScore1 / (aggScore1 + aggScore2)
        float totalAgg = aggScore1 + aggScore2;
        double p1Weight = totalAgg > 0f ? (double)aggScore1 / totalAgg : 0.5;

        var p2Predictors = PredictorWalker.Enumerate(parent2)
            .ToDictionary(t => t.Path, t => t.Predictor);

        // Accumulate per-predictor selections into a single ParameterAssignment.
        // Iteration order follows parent1's walker (deterministic, matches pre-T2g
        // GetPredictors() ordering on bare LmpModule per §27 rubber-duck Q5).
        var pa = ParameterAssignment.Empty;
        foreach (var (path, pred1) in PredictorWalker.Enumerate(parent1))
        {
            if (!p2Predictors.TryGetValue(path, out var pred2)) continue;

            // If both parents have identical instructions, no crossover needed
            if (pred1.Instructions == pred2.Instructions) continue;

            // Weighted random: parent1's instruction wins with probability p1Weight;
            // otherwise copy parent2's instruction to this slot.
            var chosen = rng.NextDouble() < p1Weight ? pred1.Instructions : pred2.Instructions;
            pa = pa.With($"{path}.instructions", chosen);
        }

        var child = parent1.WithParameters(pa);
        return (child, p1Score, p2Score, miniBatch);
    }

    /// <summary>
    /// Evaluates a target on the FULL training set. Returns per-example scores
    /// in a STABLE order (index i always corresponds to trainSet[i]) for Pareto
    /// frontier tracking. All candidates must be evaluated with this same order.
    /// Runs examples concurrently for performance (sequential in trajectory mode
    /// to preserve per-example trace capture).
    /// </summary>
    private static async Task<IReadOnlyList<ExampleResult>> EvaluateOnFullSet(
        IOptimizationTarget target,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        int maxConcurrency = 4,
        ITrajectoryMetric? trajectoryMetric = null)
    {
        var results = new ExampleResult[trainSet.Count];

        if (trajectoryMetric != null)
        {
            // Sequential loop: trajectory scoring reads per-example traces; this was also
            // the pre-T2g behavior (shared-module Trace race avoidance), preserved here
            // so trajectory-mode scoring remains deterministic and backward-compatible.
            for (int i = 0; i < trainSet.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var (output, trace) = await target.ExecuteAsync(trainSet[i].WithInputs(), cancellationToken)
                            .ConfigureAwait(false);
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
        // IOptimizationTarget.ExecuteAsync returns a fresh Trace per call so concurrent
        // invocations do not race on any shared mutable state (cf. pre-T2g shared
        // module.Trace which needed sequential trajectory mode).
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
                // falsely bias frontier.Best toward the initial target.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var (output, _) = await target.ExecuteAsync(trainSet[i].WithInputs(), ct)
                            .ConfigureAwait(false);
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
    /// Evaluates a target on a specific mini-batch and returns the average score.
    /// Used for gate checks and merge evidence.
    /// </summary>
    private static async Task<float> EvaluateMiniBatchScore(
        IOptimizationTarget target,
        List<Example> miniBatch,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        ITrajectoryMetric? trajectoryMetric = null)
    {
        float totalScore = 0f;
        int count = 0;

        foreach (var example in miniBatch)
        {
            bool scored = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (output, trace) = await target.ExecuteAsync(example.WithInputs(), cancellationToken)
                        .ConfigureAwait(false);
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

/// <summary>Classifies the type of GEPA iteration.</summary>
public enum GEPAIterationType
{
    /// <summary>Reflective mutation of one predictor.</summary>
    Mutation,
    /// <summary>Merge crossover from two Pareto-optimal parents.</summary>
    Merge
}
