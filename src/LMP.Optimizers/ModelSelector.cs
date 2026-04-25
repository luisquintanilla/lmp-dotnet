using System.Diagnostics;

namespace LMP.Optimizers;

/// <summary>
/// Evaluates a small set of model configurations and selects the non-dominated one
/// based on quality and token cost. Stores all evaluated configurations in
/// <see cref="OptimizationContext.ParetoBoundary"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ModelSelector</c> performs <em>global</em> model selection: it chooses one
/// parameter assignment for the entire optimization run, not per-example routing.
/// It evaluates each candidate configuration on a random sample and records
/// quality/cost vectors in the Pareto frontier.
/// </para>
/// <para>
/// The winning configuration (best by score) is applied to <c>ctx.Target</c> via
/// <see cref="IOptimizationTarget.WithParameters"/> and
/// <see cref="IOptimizationTarget.ApplyState"/>. Targets that don't support the
/// named parameter (throw <see cref="NotSupportedException"/>) are skipped.
/// </para>
/// <para>
/// Typical use: place <c>ModelSelector</c> before instruction-tuning steps so that
/// the best model configuration is locked in before expensive optimization begins.
/// </para>
/// </remarks>
public sealed class ModelSelector : IOptimizer
{
    /// <summary>Name of the <see cref="Categorical"/> or <see cref="StringValued"/>
    /// parameter in <see cref="OptimizationContext.SearchSpace"/> to select across.</summary>
    public string ParameterName { get; }

    /// <summary>Number of training examples to sample per candidate. Default: 10.</summary>
    public int SampleSize { get; }

    /// <summary>Optional random seed for reproducibility.</summary>
    public int? Seed { get; }

    /// <summary>
    /// Creates a <see cref="ModelSelector"/> optimizer.
    /// </summary>
    /// <param name="parameterName">Name of the Categorical parameter to select from.</param>
    /// <param name="sampleSize">Number of examples per candidate evaluation (default: 10).</param>
    /// <param name="seed">Optional random seed.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="parameterName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="sampleSize"/> is less than 1.</exception>
    public ModelSelector(string parameterName, int sampleSize = 10, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 1);

        ParameterName = parameterName;
        SampleSize = sampleSize;
        Seed = seed;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        if (!ctx.SearchSpace.Parameters.TryGetValue(ParameterName, out var kind))
            return; // parameter not in search space

        if (ctx.TrainSet.Count == 0)
            return;

        // Build candidate assignments based on parameter kind
        var assignments = BuildAssignments(kind);
        if (assignments.Count == 0) return;

        var rng = new Random(Seed ?? 42);
        var evalSet = SampleExamples(ctx.TrainSet, SampleSize, rng);

        ctx.ParetoBoundary ??= new ParetoFrontier();

        TargetState? bestState = null;
        float bestScore = float.MinValue;

        foreach (var assignment in assignments)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory)) break;

            IOptimizationTarget candidate;
            try { candidate = ctx.Target.WithParameters(assignment); }
            catch (NotSupportedException) { continue; } // target doesn't support this param

            // Evaluate candidate on sample
            long totalTokens = 0;
            int totalTurns = 0;
            float totalScore = 0f;
            var sw = Stopwatch.StartNew();

            int evaluated = 0;
            foreach (var example in evalSet)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (output, trace) = await candidate.ExecuteAsync(example.WithInputs(), ct);
                    float score = ctx.Metric(example, output);
                    totalScore += score;
                    totalTokens += trace.TotalTokens;
                    totalTurns += trace.TotalApiCalls;
                    evaluated++;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip failed examples */ }
            }

            sw.Stop();
            if (evaluated == 0) continue;

            float avgScore = totalScore / evaluated;
            var vector = new MetricVector(avgScore, totalTokens, sw.Elapsed.TotalMilliseconds, totalTurns);

            var candidateState = candidate.GetState();
            ctx.ParetoBoundary.Add(candidateState, vector);

            // Record trial
            ctx.TrialHistory.Add(new Trial(avgScore,
                new TrialCost(totalTokens, totalTokens / 2, totalTokens / 2,
                    sw.ElapsedMilliseconds, totalTurns)));

            if (avgScore > bestScore)
            {
                bestScore = avgScore;
                bestState = candidateState;
            }
        }

        // Apply the best configuration found
        if (bestState is not null)
            ctx.Target.ApplyState(bestState);
    }

    private IReadOnlyList<ParameterAssignment> BuildAssignments(ParameterKind kind)
    {
        if (kind is Categorical cat)
        {
            // One assignment per category index
            var assignments = new List<ParameterAssignment>(cat.Count);
            for (int i = 0; i < cat.Count; i++)
                assignments.Add(ParameterAssignment.Empty.With(ParameterName, i));
            return assignments;
        }

        return []; // Unsupported kind
    }

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
