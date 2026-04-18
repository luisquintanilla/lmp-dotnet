#pragma warning disable CS0618 // uses obsolete ISampler intentionally for index-space management

using System.Diagnostics;

namespace LMP.Optimizers;

/// <summary>
/// Bayesian calibration of continuous, integer, and categorical hyperparameters using
/// Tree-structured Parzen Estimation (TPE) over a discretized parameter grid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design note — index space:</b>
/// <see cref="BayesianCalibration"/> operates on the target's <i>own</i> parameter space
/// (<see cref="IOptimizationTarget.GetParameterSpace"/>), not on
/// <see cref="OptimizationContext.SearchSpace"/> (which belongs to BFS/GEPA/MIPROv2 instruction search).
/// </para>
/// <para>
/// Continuous and integer parameters are discretized into a fixed-size grid via
/// <see cref="ContinuousDiscretizer"/>. The TPE sampler (<see cref="CategoricalTpeSampler"/>)
/// operates in pure categorical index space — avoiding the categorical wraparound geometry
/// that makes <see cref="SmacSampler"/> unsuitable for numeric parameters.
/// <see cref="ContinuousDiscretizer.Decode"/> converts indices back to actual values before
/// evaluation; <see cref="ContinuousDiscretizer.Encode"/> maps results back for the update step.
/// </para>
/// <para>
/// For <see cref="ModuleTarget"/> pipelines this optimizer is a safe no-op:
/// <c>ModuleTarget.GetParameterSpace()</c> always returns <see cref="TypedParameterSpace.Empty"/>,
/// so the empty-space guard exits immediately. Add this step to a pipeline without fear of wasted
/// LM calls when the target has no numeric parameters.
/// </para>
/// </remarks>
public sealed class BayesianCalibration : IOptimizer
{
    private readonly int _numRefinements;
    private readonly int _continuousSteps;
    private readonly int? _seed;

    /// <summary>
    /// Creates a new <see cref="BayesianCalibration"/> optimizer.
    /// </summary>
    /// <param name="numRefinements">
    /// Number of TPE refinement iterations. Each iteration proposes one parameter
    /// configuration, evaluates it on a subsample, and updates the surrogate.
    /// Default is 10.
    /// </param>
    /// <param name="continuousSteps">
    /// Grid resolution for <see cref="Continuous"/> parameters. At least 2.
    /// Default is 8.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    public BayesianCalibration(
        int numRefinements = 10,
        int continuousSteps = 8,
        int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numRefinements, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(continuousSteps, 2);

        _numRefinements = numRefinements;
        _continuousSteps = continuousSteps;
        _seed = seed;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 1. Get the target's OWN hyperparameter space (not ctx.SearchSpace).
        var space = ctx.Target.GetParameterSpace();

        // 2. Build calibrationSpace: keep only Continuous, Integer, Categorical.
        //    Skip StringValued, Subset, Composite — those belong to other optimizers.
        var calibrationSpace = BuildCalibrationSpace(space);
        if (calibrationSpace.IsEmpty)
            return;

        // 3. Discretize continuous/integer parameters into categorical index grids.
        var discretizer = ContinuousDiscretizer.From(calibrationSpace, _continuousSteps);

        // 4. TPE sampler operating in pure index space via the ISampler interface.
        //    This avoids ISearchStrategy.Update's ToCategoricalDictionary() silently
        //    dropping non-integer values.
        var sampler = (ISampler)new CategoricalTpeSampler(discretizer.Cardinalities, seed: _seed);

        // 5. Determine evaluation set (devSet preferred for unbiased confirmation).
        var evalSet = ctx.DevSet.Count > 0 ? ctx.DevSet : ctx.TrainSet;
        if (evalSet.Count == 0)
            return;

        // 6. Baseline score on full evaluation set.
        var (incumbentScore, _) = await ScoreOnSetAsync(ctx.Target, evalSet, ctx.Metric, ct)
            .ConfigureAwait(false);

        // 7. Subsample pool for fast screening during the refinement loop.
        var samplePool = ctx.TrainSet.Count > 0 ? ctx.TrainSet : evalSet;
        var sampleSet = RandomSubsample(samplePool, max: 16, seed: _seed);

        // 8. TPE refinement loop.
        Dictionary<string, int>? bestCatConfig = null;
        float bestSampleScore = incumbentScore;

        for (int i = 0; i < _numRefinements; i++)
        {
            ct.ThrowIfCancellationRequested();

            var catConfig = sampler.Propose();
            var decoded = discretizer.Decode(catConfig);
            var candidate = ctx.Target.WithParameters(decoded);

            var (score, cost) = await ScoreOnSetAsync(candidate, sampleSet, ctx.Metric, ct)
                .ConfigureAwait(false);

            sampler.Update(catConfig, score);
            ctx.TrialHistory.Add(new Trial(score, cost, "BayesianCalibration"));

            if (score > bestSampleScore)
            {
                bestSampleScore = score;
                bestCatConfig = new Dictionary<string, int>(catConfig);
            }
        }

        // 9. Confirmation: re-evaluate the best config on the full eval set
        //    before committing (incumbent protection).
        if (bestCatConfig is not null && bestSampleScore > incumbentScore)
        {
            var bestAssignment = discretizer.Decode(bestCatConfig);
            var confirmedTarget = ctx.Target.WithParameters(bestAssignment);

            var (confirmedScore, confirmCost) = await ScoreOnSetAsync(confirmedTarget, evalSet, ctx.Metric, ct)
                .ConfigureAwait(false);

            ctx.TrialHistory.Add(new Trial(confirmedScore, confirmCost, "BayesianCalibration:confirmation"));

            if (confirmedScore > incumbentScore)
                ctx.Target.ApplyState(ctx.Target.WithParameters(bestAssignment).GetState());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters <paramref name="space"/> to only <see cref="Continuous"/>, <see cref="Integer"/>,
    /// and <see cref="Categorical"/> parameters.
    /// </summary>
    private static TypedParameterSpace BuildCalibrationSpace(TypedParameterSpace space)
    {
        var result = TypedParameterSpace.Empty;
        foreach (var (name, kind) in space.Parameters)
        {
            if (kind is Continuous or Integer or Categorical)
                result = result.Add(name, kind);
        }
        return result;
    }

    /// <summary>
    /// Returns a random subsample of up to <paramref name="max"/> examples from
    /// <paramref name="pool"/>. Returns the full pool unchanged when its size ≤ <paramref name="max"/>.
    /// </summary>
    private static IReadOnlyList<Example> RandomSubsample(
        IReadOnlyList<Example> pool,
        int max,
        int? seed)
    {
        if (pool.Count <= max)
            return pool;

        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        return pool
            .OrderBy(_ => rng.Next())
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Evaluates <paramref name="target"/> on every example in <paramref name="examples"/>,
    /// returning the average metric score and the accumulated cost.
    /// </summary>
    private static async Task<(float Score, TrialCost Cost)> ScoreOnSetAsync(
        IOptimizationTarget target,
        IReadOnlyList<Example> examples,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        if (examples.Count == 0)
            return (0f, new TrialCost(0, 0, 0, 0, 0));

        float totalScore = 0f;
        long inputTokens = 0L;
        long outputTokens = 0L;
        int apiCalls = 0;
        var sw = Stopwatch.StartNew();

        foreach (var example in examples)
        {
            ct.ThrowIfCancellationRequested();
            var (output, trace) = await target.ExecuteAsync(example.WithInputs(), ct)
                .ConfigureAwait(false);

            totalScore += metric(example, output);

            foreach (var entry in trace.Entries)
            {
                if (entry.Usage is { } usage)
                {
                    inputTokens += usage.InputTokenCount ?? 0;
                    outputTokens += usage.OutputTokenCount ?? 0;
                }
                apiCalls++;
            }
        }

        sw.Stop();
        return (
            totalScore / examples.Count,
            new TrialCost(
                TotalTokens: inputTokens + outputTokens,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                ElapsedMilliseconds: sw.ElapsedMilliseconds,
                ApiCalls: apiCalls));
    }
}

#pragma warning restore CS0618
