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

        if (ctx.TrainSet.Count == 0)
            return;

        // Enumerate predictors via the walker so SIMBA is composite-aware
        // (ChainTarget/Pipeline of LmpModules). Empty → no-op (bare Predictor targets, etc.).
        var predictors = PredictorWalker.Enumerate(ctx.Target).ToList();
        if (predictors.Count == 0)
            return;

        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;

        // Use devSet when available for full-set scoring (consistent with pipeline baseline).
        var evalSet = ctx.DevSet.Count > 0 ? ctx.DevSet : ctx.TrainSet;

        // Reuse pipeline-computed baseline when available; otherwise evaluate now.
        float baselineScore = ctx.Diagnostics.BaselineScore is { } cached
            ? cached
            : await EvaluateAvgScoreAsync(
                ctx.Target, evalSet, ctx.Metric, ctx.TrajectoryMetric, ct).ConfigureAwait(false);

        // Pristine clones via the seam — no direct module.Clone, no LmpModule-typed locals.
        var best = ctx.Target.WithParameters(ParameterAssignment.Empty);
        var current = ctx.Target.WithParameters(ParameterAssignment.Empty);
        float bestScore = baselineScore;
        float currentFullSetScore = baselineScore;

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory))
                break;

            // Sample mini-batch and run current with inline trace capture (GEPA T2g pattern).
            var miniBatch = InstructionReflector.SampleMiniBatch(ctx.TrainSet, rng, _miniBatchSize);
            var evalClone = current.WithParameters(ParameterAssignment.Empty);

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
                        var (output, trace) = await evalClone.ExecuteAsync(example.WithInputs(), ct)
                            .ConfigureAwait(false);
                        capturedOutput = output;
                        capturedTrace = trace;
                        if (ctx.TrajectoryMetric != null)
                        {
                            var traj = Trajectory.FromTrace(trace, example);
                            capturedScore = await ctx.TrajectoryMetric.ScoreAsync(traj, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            capturedScore = ctx.Metric(example, output);
                        }
                        scored = true;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), ct).ConfigureAwait(false);
                    }
                    catch { break; }
                }

                traceResults.Add((example, capturedOutput, scored ? capturedScore : 0f, capturedTrace));
            }

            float currentMiniBatchScore = traceResults.Count > 0
                ? traceResults.Average(r => r.Score)
                : 0f;

            // Round-robin predictor selection via the walker — cycle through all predictors.
            predictors = PredictorWalker.Enumerate(current).ToList();
            if (predictors.Count == 0) break;

            var (targetPath, targetPredictor) = predictors[iter % predictors.Count];

            // Reflect on failures to propose an improved instruction.
            var failedTraces = traceResults.Where(r => r.Score < 1.0f).ToList();
            string newInstruction = "";
            if (failedTraces.Count > 0)
            {
                try
                {
                    newInstruction = await InstructionReflector.ReflectAsync(
                        _reflectionClient, targetPath, targetPredictor.Instructions,
                        failedTraces, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Reflection failed (content filter, transient API error, etc.) —
                    // skip mutation for this iteration and continue.
                }
            }

            // Build candidate via the seam: non-empty instruction mutates ONE predictor;
            // otherwise candidate is a pristine clone of current (no mutation).
            IOptimizationTarget candidate;
            if (!string.IsNullOrWhiteSpace(newInstruction))
            {
                var pa = ParameterAssignment.Empty.With($"{targetPath}.instructions", newInstruction);
                candidate = current.WithParameters(pa);
            }
            else
            {
                candidate = current.WithParameters(ParameterAssignment.Empty);
            }

            // Gate check: accept if the candidate scores better on the SAME mini-batch.
            float candidateMiniBatchScore = await EvaluateAvgScoreAsync(
                candidate, miniBatch, ctx.Metric, ctx.TrajectoryMetric, ct).ConfigureAwait(false);

            bool accepted = candidateMiniBatchScore > currentMiniBatchScore;
            if (accepted)
            {
                current = candidate;

                // Lazy full-set re-evaluation on the accepted candidate directly (no state
                // copy to ctx.Target needed — the IOT seam routes eval through `current`).
                currentFullSetScore = await EvaluateAvgScoreAsync(
                    current, evalSet, ctx.Metric, ctx.TrajectoryMetric, ct).ConfigureAwait(false);

                if (currentFullSetScore > bestScore)
                {
                    bestScore = currentFullSetScore;
                    best = current;
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

        if (!ReferenceEquals(best, ctx.Target))
            ctx.Target.ApplyState(best.GetState());
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a target over an example set and returns the average score.
    /// Unified IOT-parameterized helper handling baseline, gate-check mini-batch, and
    /// post-acceptance full-set evaluation. Trajectory mode routes through
    /// <see cref="IOptimizationTarget.ExecuteTrajectoryAsync"/> and
    /// <see cref="ITrajectoryMetric.ScoreAsync"/>; non-trajectory mode calls
    /// <see cref="IOptimizationTarget.ExecuteAsync"/> and applies <paramref name="metric"/>.
    /// </summary>
    private static async Task<float> EvaluateAvgScoreAsync(
        IOptimizationTarget target,
        IEnumerable<Example> set,
        Func<Example, object, float> metric,
        ITrajectoryMetric? trajectoryMetric,
        CancellationToken ct)
    {
        float total = 0f;
        int count = 0;

        foreach (var example in set)
        {
            ct.ThrowIfCancellationRequested();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (trajectoryMetric != null)
                    {
                        var traj = await target
                            .ExecuteTrajectoryAsync(example.WithInputs(), source: example, ct: ct)
                            .ConfigureAwait(false);
                        total += await trajectoryMetric.ScoreAsync(traj, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var (output, _) = await target.ExecuteAsync(example.WithInputs(), ct)
                            .ConfigureAwait(false);
                        total += metric(example, output);
                    }
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
