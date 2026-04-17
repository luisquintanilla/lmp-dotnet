using System.Diagnostics;

namespace LMP;

/// <summary>
/// Thompson Sampling bandit optimizer for <see cref="Subset"/> or <see cref="Categorical"/>
/// parameter spaces. Learns which arms (pool items) yield the highest rewards and applies
/// the best-found selection to the optimization context.
/// </summary>
/// <remarks>
/// <para>
/// Each arm maintains a Beta(α, β) distribution initialized to Beta(1, 1) (uniform prior).
/// In each trial, arm probabilities θ_i ~ Beta(α_i, β_i) are sampled, and the top-k items
/// (where k ∈ [minSize, maxSize]) are selected and evaluated. Successful trials (score ≥
/// <see cref="SuccessThreshold"/>) increment α; unsuccessful trials increment β.
/// </para>
/// <para>
/// After exploration, the MAP-estimated best subset is stored in
/// <c>ctx.Bag["lmp.bandit:{paramName}:best"]</c> for downstream pipeline steps.
/// Beta distribution parameters are stored under
/// <c>"lmp.bandit:{paramName}:alphas"</c> and <c>"lmp.bandit:{paramName}:betas"</c>.
/// </para>
/// <para>
/// Use this optimizer for skill routing (<see cref="SkillPoolExtensions"/>),
/// tool selection (<see cref="ToolPoolExtensions"/>), or any <see cref="Subset"/>
/// parameter where arm-level reward learning is valuable.
/// </para>
/// </remarks>
public sealed class ContextualBandit : IOptimizer
{
    /// <summary>The name of the Subset or Categorical parameter to optimize.</summary>
    public string ParameterName { get; }

    /// <summary>
    /// Score threshold above which a trial is counted as a success for Beta update.
    /// Default: <c>0.5f</c>.
    /// </summary>
    public float SuccessThreshold { get; }

    /// <summary>Maximum number of Thompson Sampling trials. Default: <c>20</c>.</summary>
    public int NumTrials { get; }

    /// <summary>Optional random seed for reproducibility.</summary>
    public int? Seed { get; }

    /// <summary>
    /// Creates a <see cref="ContextualBandit"/> optimizer.
    /// </summary>
    /// <param name="parameterName">The Subset or Categorical parameter name in the search space.</param>
    /// <param name="successThreshold">Score threshold for Beta(α, β) success update (default: 0.5).</param>
    /// <param name="numTrials">Number of Thompson Sampling exploration trials (default: 20).</param>
    /// <param name="seed">Optional random seed.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameterName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="numTrials"/> is less than 1.
    /// </exception>
    public ContextualBandit(
        string parameterName,
        float successThreshold = 0.5f,
        int numTrials = 20,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials, 1);

        ParameterName = parameterName;
        SuccessThreshold = successThreshold;
        NumTrials = numTrials;
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
            return; // nothing to evaluate

        // Extract pool and size bounds from the parameter kind
        IReadOnlyList<object> pool;
        int minSize, maxSize;

        if (kind is Subset subsetKind)
        {
            pool = subsetKind.Pool;
            minSize = subsetKind.MinSize;
            maxSize = subsetKind.MaxSize == -1 ? pool.Count : subsetKind.MaxSize;
        }
        else if (kind is Categorical catKind)
        {
            // Treat each category index as an arm; pick exactly one
            var catPool = new object[catKind.Count];
            for (int i = 0; i < catKind.Count; i++) catPool[i] = i;
            pool = catPool;
            minSize = maxSize = 1;
        }
        else
            return; // unsupported kind

        int n = pool.Count;
        if (n == 0) return;

        // Beta(α, β) per arm — uniform prior Beta(1,1)
        float[] alphas = new float[n];
        float[] betas = new float[n];
        for (int i = 0; i < n; i++) { alphas[i] = 1f; betas[i] = 1f; }

        var rng = new Random(Seed ?? 42);

        for (int t = 0; t < NumTrials; t++)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory)) break;

            // Thompson Sampling: sample θ_i ~ Beta(α_i, β_i) for each arm
            float[] samples = new float[n];
            for (int i = 0; i < n; i++)
                samples[i] = SampleBeta(rng, alphas[i], betas[i]);

            // Select top-k arms by sampled value, k ∈ [minSize, maxSize]
            int[] ranked = RankDescending(samples, n);
            int k = CountAbove(samples, 0.5f, n);
            k = Math.Clamp(k == 0 ? minSize : k, minSize, maxSize);

            var selectedIndices = new HashSet<int>();
            for (int j = 0; j < k; j++)
                selectedIndices.Add(ranked[j]);

            var selectedItems = new List<object>(k);
            for (int i = 0; i < n; i++)
                if (selectedIndices.Contains(i)) selectedItems.Add(pool[i]);

            // Evaluate the selected subset
            var example = ctx.TrainSet[rng.Next(ctx.TrainSet.Count)];
            var assignment = ParameterAssignment.Empty.With(ParameterName, (IReadOnlyList<object>)selectedItems);

            IOptimizationTarget evalTarget;
            try { evalTarget = ctx.Target.WithParameters(assignment); }
            catch (NotSupportedException) { break; } // target doesn't support parameter application

            var sw = Stopwatch.StartNew();
            var (output, trace) = await evalTarget.ExecuteAsync(example.WithInputs(), ct);
            sw.Stop();

            float score = ctx.Metric(example, output);
            bool success = score >= SuccessThreshold;

            // Update Beta distributions for selected arms
            foreach (int i in selectedIndices)
            {
                if (success) alphas[i] += 1f;
                else betas[i] += 1f;
            }

            // Record trial
            ctx.TrialHistory.Add(new Trial(score,
                new TrialCost(trace.TotalTokens, trace.InputTokens, trace.OutputTokens,
                    sw.ElapsedMilliseconds, trace.TotalApiCalls)));
        }

        // Store learned Beta parameters in context bag
        ctx.Bag[$"lmp.bandit:{ParameterName}:alphas"] = alphas;
        ctx.Bag[$"lmp.bandit:{ParameterName}:betas"] = betas;

        // Compute MAP estimates and store the best subset
        float[] mapEstimates = new float[n];
        for (int i = 0; i < n; i++)
        {
            float denom = alphas[i] + betas[i] - 2f;
            mapEstimates[i] = denom <= 0 ? 0.5f : (alphas[i] - 1f) / denom;
        }

        int[] bestRanked = RankDescending(mapEstimates, n);
        int bestK = CountAbove(mapEstimates, 0.5f, n);
        bestK = Math.Clamp(bestK == 0 ? minSize : bestK, minSize, maxSize);

        var bestItems = new List<object>(bestK);
        for (int j = 0; j < bestK; j++)
            bestItems.Add(pool[bestRanked[j]]);
        ctx.Bag[$"lmp.bandit:{ParameterName}:best"] = (IReadOnlyList<object>)bestItems;
    }

    // ── Thompson Sampling math ─────────────────────────────────────────────

    private static float SampleBeta(Random rng, float alpha, float beta)
    {
        float x = SampleGamma(rng, alpha);
        float y = SampleGamma(rng, beta);
        float sum = x + y;
        return sum <= 0f ? 0.5f : x / sum;
    }

    /// <summary>
    /// Samples from Gamma(α, 1) using Marsaglia and Tsang's "A Simple Method for
    /// Generating Gamma Variables" (ACM TOMS 2000).
    /// </summary>
    private static float SampleGamma(Random rng, float alpha)
    {
        if (alpha < 1f)
            return SampleGamma(rng, alpha + 1f) * MathF.Pow(rng.NextSingle(), 1f / alpha);

        float d = alpha - 1f / 3f;
        float c = 1f / MathF.Sqrt(9f * d);

        while (true)
        {
            float x, v;
            do
            {
                x = NextGaussian(rng);
                v = 1f + c * x;
            } while (v <= 0f);

            v = v * v * v; // v^3
            float u = rng.NextSingle();
            float x2 = x * x;

            if (u < 1f - 0.0331f * x2 * x2)
                return d * v;
            if (MathF.Log(u) < 0.5f * x2 + d * (1f - v + MathF.Log(v)))
                return d * v;
        }
    }

    private static float NextGaussian(Random rng)
    {
        // Box-Muller transform
        float u1 = 1f - rng.NextSingle(); // avoid log(0)
        float u2 = rng.NextSingle();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int[] RankDescending(float[] values, int n)
    {
        int[] indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        Array.Sort(indices, (a, b) => values[b].CompareTo(values[a]));
        return indices;
    }

    private static int CountAbove(float[] values, float threshold, int n)
    {
        int count = 0;
        for (int i = 0; i < n; i++)
            if (values[i] > threshold) count++;
        return count;
    }
}
