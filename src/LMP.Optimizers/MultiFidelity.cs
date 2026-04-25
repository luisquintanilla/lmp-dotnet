using System.Diagnostics;

namespace LMP.Optimizers;

/// <summary>
/// Hyperband-inspired optimizer that prunes low-performing parameter assignments early
/// by evaluating them at increasing fidelity levels (subset sizes).
/// </summary>
/// <remarks>
/// <para>
/// Algorithm sketch (successive halving):
/// <list type="number">
/// <item><description>
/// Sample <see cref="NumCandidates"/> random assignments from <c>ctx.SearchSpace</c>.
/// </description></item>
/// <item><description>
/// Evaluate all candidates on a small training subset (size = <see cref="InitialFidelity"/>).
/// </description></item>
/// <item><description>
/// Keep the top <c>1 / <see cref="PruningFactor"/></c> fraction of candidates.
/// </description></item>
/// <item><description>
/// Double the fidelity (subset size) and repeat until one candidate remains or budget exhausted.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Stores <see cref="MetricVector"/> results for surviving candidates in
/// <see cref="OptimizationContext.ParetoBoundary"/> and applies the best-scoring
/// state to <c>ctx.Target</c>.
/// </para>
/// </remarks>
public sealed class MultiFidelity : IOptimizer
{
    /// <summary>Number of candidate assignments to evaluate initially. Default: 8.</summary>
    public int NumCandidates { get; }

    /// <summary>Initial training-subset size (fidelity level 0). Default: 4.</summary>
    public int InitialFidelity { get; }

    /// <summary>
    /// Factor by which candidates are pruned each round (keep 1/PruningFactor).
    /// Default: 2 (keep top half each round).
    /// </summary>
    public int PruningFactor { get; }

    /// <summary>Optional random seed for reproducibility.</summary>
    public int? Seed { get; }

    /// <summary>
    /// Creates a <see cref="MultiFidelity"/> optimizer.
    /// </summary>
    public MultiFidelity(
        int numCandidates = 8,
        int initialFidelity = 4,
        int pruningFactor = 2,
        int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numCandidates, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialFidelity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pruningFactor, 2);

        NumCandidates = numCandidates;
        InitialFidelity = initialFidelity;
        PruningFactor = pruningFactor;
        Seed = seed;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        if (ctx.SearchSpace.IsEmpty)
            return;

        if (ctx.TrainSet.Count == 0)
            return;

        var rng = new Random(Seed ?? 42);
        ctx.ParetoBoundary ??= new ParetoFrontier();

        // Sample initial candidates
        var candidates = SampleCandidates(ctx.SearchSpace, NumCandidates, rng);

        int fidelity = InitialFidelity;
        int round = 0;

        while (candidates.Count > 1 && ctx.Budget.IsWithinBudget(ctx.TrialHistory))
        {
            ct.ThrowIfCancellationRequested();

            var evalSet = SampleExamples(ctx.TrainSet, fidelity, rng);
            var results = new List<(ParameterAssignment Assignment, float Score, long Tokens, int Turns)>();

            foreach (var assignment in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory)) break;

                IOptimizationTarget candidate;
                try { candidate = ctx.Target.WithParameters(assignment); }
                catch (NotSupportedException) { continue; }

                float totalScore = 0f;
                long totalTokens = 0;
                int totalTurns = 0;
                int evaluated = 0;
                var sw = Stopwatch.StartNew();

                foreach (var example in evalSet)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var (output, trace) = await candidate.ExecuteAsync(example.WithInputs(), ct);
                        totalScore += ctx.Metric(example, output);
                        totalTokens += trace.TotalTokens;
                        totalTurns += trace.TotalApiCalls;
                        evaluated++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* skip failed */ }
                }

                sw.Stop();
                if (evaluated == 0) continue;

                float avgScore = totalScore / evaluated;
                results.Add((assignment, avgScore, totalTokens, totalTurns));

                ctx.TrialHistory.Add(new Trial(avgScore,
                    new TrialCost(totalTokens, totalTokens / 2, totalTokens / 2,
                        sw.ElapsedMilliseconds, totalTurns)));
            }

            if (results.Count == 0) break;

            // Prune: keep top 1/PruningFactor of candidates
            int keepCount = Math.Max(1, results.Count / PruningFactor);
            var survivors = results
                .OrderByDescending(r => r.Score)
                .Take(keepCount)
                .Select(r => r.Assignment)
                .ToList();

            candidates = survivors;
            fidelity = Math.Min(fidelity * PruningFactor, ctx.TrainSet.Count);
            round++;
        }

        // Final evaluation of surviving candidates on full set (or current best)
        if (candidates.Count == 0) return;

        TargetState? bestState = null;
        float bestScore = float.MinValue;

        foreach (var assignment in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory)) break;

            IOptimizationTarget candidate;
            try { candidate = ctx.Target.WithParameters(assignment); }
            catch (NotSupportedException) { continue; }

            float totalScore = 0f;
            long totalTokens = 0;
            int totalTurns = 0;
            int evaluated = 0;
            var sw = Stopwatch.StartNew();

            foreach (var example in ctx.TrainSet)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (output, trace) = await candidate.ExecuteAsync(example.WithInputs(), ct);
                    totalScore += ctx.Metric(example, output);
                    totalTokens += trace.TotalTokens;
                    totalTurns += trace.TotalApiCalls;
                    evaluated++;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip */ }
            }

            sw.Stop();
            if (evaluated == 0) continue;

            float avgScore = totalScore / evaluated;
            var state = candidate.GetState();
            var vector = new MetricVector(avgScore, totalTokens, sw.Elapsed.TotalMilliseconds, totalTurns);
            ctx.ParetoBoundary.Add(state, vector);

            ctx.TrialHistory.Add(new Trial(avgScore,
                new TrialCost(totalTokens, totalTokens / 2, totalTokens / 2,
                    sw.ElapsedMilliseconds, totalTurns)));

            if (avgScore > bestScore)
            {
                bestScore = avgScore;
                bestState = state;
            }
        }

        if (bestState is not null)
            ctx.Target.ApplyState(bestState);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private List<ParameterAssignment> SampleCandidates(
        TypedParameterSpace space, int count, Random rng)
    {
        var assignments = new List<ParameterAssignment>(count);
        for (int i = 0; i < count; i++)
        {
            var assignment = ParameterAssignment.Empty;
            foreach (var (name, kind) in space.Parameters)
            {
                object value = SampleValue(kind, rng);
                assignment = assignment.With(name, value);
            }
            assignments.Add(assignment);
        }
        return assignments;
    }

    private static object SampleValue(ParameterKind kind, Random rng) => kind switch
    {
        Categorical cat => (object)rng.Next(cat.Count),
        Integer intKind => rng.Next(intKind.Min, intKind.Max + 1),
        Continuous contKind => contKind.Min + rng.NextDouble() * (contKind.Max - contKind.Min),
        Subset subKind when subKind.Pool.Count > 0 =>
            subKind.Pool[rng.Next(subKind.Pool.Count)],
        _ => 0
    };

    private static List<Example> SampleExamples(IReadOnlyList<Example> trainSet, int n, Random rng)
    {
        if (n >= trainSet.Count)
            return trainSet.ToList();

        var indices = new HashSet<int>();
        while (indices.Count < n)
            indices.Add(rng.Next(trainSet.Count));

        return indices.Select(i => trainSet[i]).ToList();
    }
}
