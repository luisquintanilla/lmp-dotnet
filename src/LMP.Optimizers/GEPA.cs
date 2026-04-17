using System.Text;
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

        if (trainSet.Count == 0)
            return module;

        cancellationToken.ThrowIfCancellationRequested();

        var rng = _seed.HasValue ? new Random(_seed.Value) : new Random();
        var frontier = new ParetoFrontier<TModule>();
        int componentIndex = 0; // Round-robin predictor index
        float bestIndividualScore = float.MinValue; // Best single-candidate average score seen so far

        // Seed the frontier: evaluate initial module on the FULL trainSet (stable valset)
        var initialScores = await EvaluateOnFullSet(module, trainSet, metric, cancellationToken, _maxConcurrency);
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
                    await MergeAsync(frontier, trainSet, metric, rng, cancellationToken);

                // Gate check: merge must not be worse than both parents on the same mini-batch
                float mergeMiniBatchScore = await EvaluateMiniBatchScore(
                    mergeCandidate, mergeMiniBatch, metric, cancellationToken);

                if (mergeMiniBatchScore < Math.Max(p1MiniBatch, p2MiniBatch))
                {
                    _progress?.Report(new GEPAProgressReport(
                        iter + 1, _maxIterations, frontier.Count,
                        bestIndividualScore,
                        GEPAIterationType.Merge, false));
                    continue;
                }

                // Passed gate: evaluate on the full set and add to frontier
                var mergeScores = await EvaluateOnFullSet(mergeCandidate, trainSet, metric, cancellationToken, _maxConcurrency);
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
                    frontier, trainSet, metric, rng, componentIndex, cancellationToken);
                componentIndex++;

                // Gate check: evaluate mutated on the SAME mini-batch used for reflection
                float mutatedMiniBatchScore = await EvaluateMiniBatchScore(
                    mutated, miniBatch, metric, cancellationToken);

                if (mutatedMiniBatchScore <= parentMiniBatchScore)
                {
                    _progress?.Report(new GEPAProgressReport(
                        iter + 1, _maxIterations, frontier.Count,
                        bestIndividualScore,
                        GEPAIterationType.Mutation, false));
                    continue; // Mini-batch didn't improve — skip expensive full eval
                }

                // Passed gate: evaluate on the FULL trainSet for Pareto tracking
                var fullScores = await EvaluateOnFullSet(mutated, trainSet, metric, cancellationToken, _maxConcurrency);
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
        CancellationToken cancellationToken)
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
            candidate.Trace = new Trace();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await candidate.ForwardAsync(example.WithInputs(), cancellationToken);
                    var score = metric(example, output);
                    traceResults.Add((example, output, score, candidate.Trace));
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
                    traceResults.Add((example, "error", 0f, candidate.Trace));
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
                var newInstruction = await ReflectOnPredictor(
                    targetName, targetPredictor.Instructions, failedTraces, cancellationToken);

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
    /// and propose an improved instruction. Uses predictor-specific trace I/O
    /// rather than the full module output to avoid cross-task confusion.
    /// </summary>
    private async Task<string> ReflectOnPredictor(
        string predictorName,
        string currentInstruction,
        List<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken)
    {
        // Only reflect on examples that have trace entries for this predictor
        // (error examples with no trace entries can't be diagnosed)
        var diagnosable = failedTraces
            .Where(r => r.Trace.Entries.Any(e => e.PredictorName == predictorName))
            .Take(5)
            .ToList();

        if (diagnosable.Count == 0)
            return "";

        var prompt = new StringBuilder();
        prompt.AppendLine($"You are improving the '{predictorName}' predictor in a multi-predictor LM pipeline.");
        prompt.AppendLine($"Current instruction: \"{currentInstruction}\"");
        prompt.AppendLine();
        prompt.AppendLine($"This predictor has ONE specific job: classify the '{predictorName}' of the input.");
        prompt.AppendLine("Other sub-tasks are handled by separate predictors — do NOT include them in this instruction.");
        prompt.AppendLine();
        prompt.AppendLine("Examples where this predictor contributed to errors:");
        prompt.AppendLine();

        int shown = 0;
        foreach (var (example, _, score, trace) in diagnosable)
        {
            shown++;
            var entries = trace.Entries.Where(e => e.PredictorName == predictorName).ToList();
            if (entries.Count == 0) continue;

            prompt.AppendLine($"--- Example {shown} (combined module score: {score:F2}) ---");
            foreach (var entry in entries)
            {
                prompt.AppendLine($"  Input:    {entry.Input}");
                prompt.AppendLine($"  Produced: {entry.Output}");
            }
            // Show the full expected label so the LLM can infer the correct value for this sub-task
            prompt.AppendLine($"  Full expected: {example.GetLabel()}");
            prompt.AppendLine();
        }

        prompt.AppendLine($"Write an improved instruction for the '{predictorName}' predictor.");
        prompt.AppendLine();
        prompt.AppendLine("CRITICAL RULES — violation breaks the pipeline:");
        prompt.AppendLine("  1. Output ONLY the instruction text — no explanation, no preamble");
        prompt.AppendLine("  2. Do NOT describe output format, JSON, or field names");
        prompt.AppendLine("  3. Do NOT instruct the predictor to output more than one field");
        prompt.AppendLine($"  4. Focus exclusively on '{predictorName}' — ignore other sub-tasks");

        var response = await _reflectionClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                $"You are an expert prompt engineer. You are improving a SINGLE predictor called '{predictorName}' " +
                "in a multi-task pipeline. The predictor's output schema is fixed and enforced automatically — " +
                "never include output format instructions. Write a focused, concise instruction that helps the " +
                $"predictor classify '{predictorName}' more accurately. Output ONLY the instruction text."),
            new ChatMessage(ChatRole.User, prompt.ToString())
        ],
        cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? "";
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
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var (parent1, aggScore1, parent2, aggScore2) = frontier.SelectParents(rng);
        var child = parent1.Clone<TModule>();

        // Evaluate both parents on the same mini-batch (used for gate check by caller)
        var miniBatch = SampleMiniBatch(trainSet, rng);
        float p1Score = await EvaluateMiniBatchScore(parent1, miniBatch, metric, cancellationToken);
        float p2Score = await EvaluateMiniBatchScore(parent2, miniBatch, metric, cancellationToken);

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
    /// Runs examples concurrently for performance.
    /// </summary>
    private static async Task<IReadOnlyList<ExampleResult>> EvaluateOnFullSet<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken,
        int maxConcurrency = 4)
        where TModule : LmpModule
    {
        var results = new ExampleResult[trainSet.Count];
        var evalModule = module.Clone<TModule>();

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
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        float totalScore = 0f;
        int count = 0;
        var evalModule = module.Clone<TModule>();

        foreach (var example in miniBatch)
        {
            evalModule.Trace = new Trace();
            bool scored = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await evalModule.ForwardAsync(example.WithInputs(), cancellationToken);
                    totalScore += metric(example, output);
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
    {
        int size = Math.Min(_miniBatchSize, trainSet.Count);
        var indices = Enumerable.Range(0, trainSet.Count)
            .OrderBy(_ => rng.Next())
            .Take(size)
            .ToList();
        return indices.Select(i => trainSet[i]).ToList();
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
