using Microsoft.Extensions.AI;

namespace LMP.Optimizers;

/// <summary>
/// SIMBA (Simple Instruction Mini-Batch Ascent) — greedy mini-batch stochastic ascent
/// with LLM self-reflection for instruction improvement.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="GEPA"/>, which maintains a Pareto frontier and explores a population,
/// SIMBA greedily accepts any mini-batch score improvement and continues from the accepted
/// candidate. This makes SIMBA faster and more suitable for agents and tool-using modules
/// where fast incremental improvement is preferred over global exploration.
/// </para>
/// <para>
/// Algorithm per iteration:
/// <list type="number">
/// <item><description>Sample a random mini-batch from the training set.</description></item>
/// <item><description>Run the current module on the mini-batch, capturing failure traces.</description></item>
/// <item><description>Select one predictor (round-robin) and reflect on its failures using an LLM.</description></item>
/// <item><description>Apply the proposed instruction to a candidate clone.</description></item>
/// <item><description>If the candidate scores higher on the mini-batch, accept it as the new current.</description></item>
/// <item><description>On acceptance, re-evaluate on the full eval set and update the best state.</description></item>
/// </list>
/// </para>
/// <para>
/// Inspired by DSPy SIMBA (2025): <see href="https://github.com/stanfordnlp/dspy"/>.
/// </para>
/// </remarks>
public sealed class SIMBA : IOptimizer
{
    private readonly IChatClient _reflectionClient;
    private readonly int _maxIterations;
    private readonly int _miniBatchSize;
    private readonly int? _seed;
    private readonly IProgress<SimbaProgressReport>? _progress;

    /// <summary>
    /// Creates a new SIMBA optimizer.
    /// </summary>
    /// <param name="reflectionClient">
    /// LLM used for instruction reflection and improvement. Can be the same model
    /// as the module's client, or a cheaper model for cost efficiency.
    /// </param>
    /// <param name="maxIterations">Maximum ascent iterations. Default is 32.</param>
    /// <param name="miniBatchSize">Number of examples per mini-batch. Default is 8.</param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <param name="progress">Optional per-iteration progress reporter.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reflectionClient"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxIterations"/> or <paramref name="miniBatchSize"/> is less than 1.
    /// </exception>
    public SIMBA(
        IChatClient reflectionClient,
        int maxIterations = 32,
        int miniBatchSize = 8,
        int? seed = null,
        IProgress<SimbaProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reflectionClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(miniBatchSize, 1);

        _reflectionClient = reflectionClient;
        _maxIterations = maxIterations;
        _miniBatchSize = miniBatchSize;
        _seed = seed;
        _progress = progress;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var module = ctx.Target.GetService<LmpModule>()
            ?? throw new NotSupportedException(
                $"{nameof(SIMBA)} requires an LmpModule target. Use ModuleTarget.For(module).");

        if (ctx.TrainSet.Count == 0)
            return;

        var predictors = module.GetPredictors().ToList();
        if (predictors.Count == 0)
            return;

        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;

        // Use devSet when available for full-set scoring (consistent with pipeline baseline).
        var evalSet = ctx.DevSet.Count > 0 ? ctx.DevSet : ctx.TrainSet;

        // Reuse pipeline-computed baseline when available; otherwise evaluate now.
        float baselineScore = ctx.Bag.TryGetValue("baseline", out var b) && b is float f
            ? f
            : ctx.TrajectoryMetric != null
                ? await EvaluateTrajectoryScoreAsync(ctx.Target, evalSet, ctx.TrajectoryMetric, ct).ConfigureAwait(false)
                : await EvaluateScoreAsync(module, evalSet, ctx.Metric, ct).ConfigureAwait(false);

        float bestScore = baselineScore;
        var best = module.Clone();
        var current = module.Clone();
        float currentFullSetScore = baselineScore;

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory))
                break;

            // Sample mini-batch and run current with trace capture for reflection.
            var miniBatch = InstructionReflector.SampleMiniBatch(ctx.TrainSet, rng, _miniBatchSize);
            var traceResults = await InstructionReflector.RunWithTracesAsync(
                current, miniBatch, ctx.Metric, ct).ConfigureAwait(false);

            float currentMiniBatchScore = traceResults.Count > 0
                ? traceResults.Average(r => r.Score)
                : 0f;

            // Round-robin predictor selection — cycle through all predictors.
            predictors = current.GetPredictors().ToList();
            if (predictors.Count == 0) break;

            var (targetName, targetPredictor) = predictors[iter % predictors.Count];

            // Reflect on failures to propose an improved instruction.
            var failedTraces = traceResults.Where(r => r.Score < 1.0f).ToList();
            string newInstruction = "";
            if (failedTraces.Count > 0)
            {
                try
                {
                    newInstruction = await InstructionReflector.ReflectAsync(
                        _reflectionClient, targetName, targetPredictor.Instructions,
                        failedTraces, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Reflection failed (content filter, transient API error, etc.) —
                    // skip mutation for this iteration and continue.
                }
            }

            // Build candidate: clone current and apply the proposed instruction.
            var candidate = current.Clone();
            if (!string.IsNullOrWhiteSpace(newInstruction))
            {
                var cp = candidate.GetPredictors()
                    .ToDictionary(p => p.Name, p => p.Predictor);
                if (cp.TryGetValue(targetName, out var pred))
                    pred.Instructions = newInstruction;
            }

            // Gate check: accept if the candidate scores better on the SAME mini-batch.
            float candidateMiniBatchScore = await EvaluateMiniBatchScoreAsync(
                candidate, miniBatch, ctx.Metric, ct).ConfigureAwait(false);

            bool accepted = candidateMiniBatchScore > currentMiniBatchScore;
            if (accepted)
            {
                current = candidate;

                // Lazy full-set re-evaluation: only incur the cost on acceptance.
                if (ctx.TrajectoryMetric != null)
                {
                    // Apply the accepted candidate's state to the target before trajectory scoring.
                    ctx.Target.ApplyState(TargetState.From(current.GetState()));
                    currentFullSetScore = await EvaluateTrajectoryScoreAsync(
                        ctx.Target, evalSet, ctx.TrajectoryMetric, ct).ConfigureAwait(false);
                }
                else
                {
                    currentFullSetScore = await EvaluateScoreAsync(current, evalSet, ctx.Metric, ct)
                        .ConfigureAwait(false);
                }

                if (currentFullSetScore > bestScore)
                {
                    bestScore = currentFullSetScore;
                    best = current.Clone();
                }
            }

            // Log the actual iteration score (current full-set score, not just best).
            ctx.TrialHistory.Add(new Trial(
                Score: currentFullSetScore,
                Cost: new TrialCost(0, 0, 0, 0, 1),
                Notes: $"SIMBA iter {iter + 1}: {(accepted ? "accepted" : "rejected")}"));

            ctx.Progress?.Report(new OptimizationProgress(
                OptimizerName: nameof(SIMBA),
                TrialNumber: iter + 1,
                TotalTrials: _maxIterations,
                CurrentBestScore: bestScore,
                BaselineScore: baselineScore));

            _progress?.Report(new SimbaProgressReport(iter + 1, _maxIterations, bestScore, accepted));
        }

        ctx.Target.ApplyState(TargetState.From(best.GetState()));
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static async Task<float> EvaluateScoreAsync(
        LmpModule module,
        IReadOnlyList<Example> evalSet,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        if (evalSet.Count == 0)
            return 0f;

        var result = await Evaluator.EvaluateAsync(module, evalSet, metric, cancellationToken: ct)
            .ConfigureAwait(false);
        return result.AverageScore;
    }

    private static async Task<float> EvaluateMiniBatchScoreAsync(
        LmpModule module,
        IEnumerable<Example> batch,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        float total = 0f;
        int count = 0;

        // Clone to avoid mutating module.Trace during gate-check evaluation.
        var evalModule = module.Clone();

        foreach (var ex in batch)
        {
            evalModule.Trace = new Trace();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await evalModule.ForwardAsync(ex.WithInputs(), ct).ConfigureAwait(false);
                    total += metric(ex, output);
                    count++;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), ct).ConfigureAwait(false);
                }
                catch
                {
                    count++; // score 0 for failed examples
                    break;
                }
            }
        }

        return count > 0 ? total / count : 0f;
    }

    /// <summary>
    /// Evaluates a set of examples using <see cref="IOptimizationTarget.ExecuteTrajectoryAsync"/>
    /// and the provided <see cref="ITrajectoryMetric"/>. Used when
    /// <see cref="OptimizationContext.TrajectoryMetric"/> is set, replacing the standard
    /// module-based evaluation path.
    /// </summary>
    private static async Task<float> EvaluateTrajectoryScoreAsync(
        IOptimizationTarget target,
        IReadOnlyList<Example> evalSet,
        ITrajectoryMetric metric,
        CancellationToken ct)
    {
        if (evalSet.Count == 0)
            return 0f;

        float total = 0f;
        int count = 0;

        foreach (var example in evalSet)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var trajectory = await target
                    .ExecuteTrajectoryAsync(example.WithInputs(), source: example, ct: ct)
                    .ConfigureAwait(false);
                total += await metric.ScoreAsync(trajectory, ct).ConfigureAwait(false);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { count++; /* score 0 for failed examples */ }
        }

        return count > 0 ? total / count : 0f;
    }
}

/// <summary>
/// Progress report emitted by <see cref="SIMBA"/> after each iteration.
/// </summary>
/// <param name="Iteration">Current iteration (1-based).</param>
/// <param name="MaxIterations">Total iterations requested.</param>
/// <param name="BestScore">Best full-set score seen so far.</param>
/// <param name="Improved">
/// <see langword="true"/> if the candidate was accepted (mini-batch score improved).
/// </param>
public sealed record SimbaProgressReport(
    int Iteration,
    int MaxIterations,
    float BestScore,
    bool Improved);
