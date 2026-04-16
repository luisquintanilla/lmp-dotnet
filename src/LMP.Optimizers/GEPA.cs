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
    private readonly int? _seed;

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
    /// <param name="seed">Optional random seed for reproducibility.</param>
    public GEPA(
        IChatClient reflectionClient,
        int maxIterations = 50,
        int miniBatchSize = 5,
        int mergeEvery = 5,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(reflectionClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(miniBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(mergeEvery, 1);

        _reflectionClient = reflectionClient;
        _maxIterations = maxIterations;
        _miniBatchSize = miniBatchSize;
        _mergeEvery = mergeEvery;
        _seed = seed;
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

        var rng = _seed.HasValue ? new Random(_seed.Value) : new Random();
        var frontier = new ParetoFrontier<TModule>();
        int componentIndex = 0; // Round-robin predictor index

        // Seed the frontier: evaluate initial module on the FULL trainSet (stable valset)
        var initialScores = await EvaluateOnFullSet(module, trainSet, metric, cancellationToken);
        frontier.Add(module, initialScores);

        float baselineAvg = initialScores.Average(s => s.Score);

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TModule candidate;
            if (iter % _mergeEvery == 0 && iter > 0 && frontier.Count >= 2)
            {
                // Merge: evidence-based crossover from two frontier parents
                candidate = await MergeAsync(frontier, trainSet, metric, rng, cancellationToken);

                // Always evaluate merge candidates on full set (no gate check for merges)
                var mergeScores = await EvaluateOnFullSet(candidate, trainSet, metric, cancellationToken);
                frontier.Add(candidate, mergeScores);
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
                    continue; // Mini-batch didn't improve — skip expensive full eval

                // Passed gate: evaluate on the FULL trainSet for Pareto tracking
                var fullScores = await EvaluateOnFullSet(mutated, trainSet, metric, cancellationToken);
                frontier.Add(mutated, fullScores);
            }
        }

        var best = frontier.Best;

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
            try
            {
                var output = await candidate.ForwardAsync(example.WithInputs(), cancellationToken);
                var score = metric(example, output);
                traceResults.Add((example, output, score, candidate.Trace));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Score as 0 for failed examples
                traceResults.Add((example, "error", 0f, candidate.Trace));
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
            var newInstruction = await ReflectOnPredictor(
                targetName, targetPredictor.Instructions, failedTraces, cancellationToken);

            if (!string.IsNullOrWhiteSpace(newInstruction))
                targetPredictor.Instructions = newInstruction;
        }

        return (candidate, parentMiniBatchScore, miniBatch);
    }

    /// <summary>
    /// Asks the reflection LLM to analyze failures for a specific predictor
    /// and propose an improved instruction. Includes expected labels for diagnosis.
    /// </summary>
    private async Task<string> ReflectOnPredictor(
        string predictorName,
        string currentInstruction,
        List<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"You are analyzing the '{predictorName}' predictor in an LM program.");
        prompt.AppendLine($"Current instruction: \"{currentInstruction}\"");
        prompt.AppendLine();
        prompt.AppendLine("Here are examples where this predictor performed poorly:");
        prompt.AppendLine();

        int shown = 0;
        foreach (var (example, output, score, trace) in failedTraces.Take(5))
        {
            shown++;
            prompt.AppendLine($"--- Example {shown} (score: {score:F2}) ---");
            prompt.AppendLine($"Input: {example.WithInputs()}");
            prompt.AppendLine($"Expected: {example.GetLabel()}");
            prompt.AppendLine($"Got: {output}");

            // Show trace entries for this predictor
            var entries = trace.Entries.Where(e => e.PredictorName == predictorName).ToList();
            foreach (var entry in entries)
            {
                prompt.AppendLine($"  Predictor received: {entry.Input}");
                prompt.AppendLine($"  Predictor produced: {entry.Output}");
            }
            prompt.AppendLine();
        }

        prompt.AppendLine("Based on these failures, propose an improved instruction for this predictor.");
        prompt.AppendLine("The instruction should help the predictor produce outputs that match the expected labels more accurately.");
        prompt.AppendLine("Respond with ONLY the new instruction text, nothing else.");

        var response = await _reflectionClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                "You are an expert prompt engineer analyzing LM program failures. " +
                "Your goal is to diagnose why the predictor is failing and write a better instruction. " +
                "Compare the expected output with what was actually produced to identify specific patterns of error."),
            new ChatMessage(ChatRole.User, prompt.ToString())
        ],
        cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? "";
    }

    /// <summary>
    /// Evidence-based merge: combine instructions from two Pareto-optimal parents.
    /// For each predictor, evaluate both parents on a shared mini-batch and pick
    /// the instruction from whichever parent scored better.
    /// </summary>
    private async Task<TModule> MergeAsync<TModule>(
        ParetoFrontier<TModule> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var (parent1, parent2) = frontier.SelectParents(rng);
        var child = parent1.Clone<TModule>();

        // Evaluate both parents on the same mini-batch
        var miniBatch = SampleMiniBatch(trainSet, rng);
        float score1 = await EvaluateMiniBatchScore(parent1, miniBatch, metric, cancellationToken);
        float score2 = await EvaluateMiniBatchScore(parent2, miniBatch, metric, cancellationToken);

        // Pick instructions from the better-scoring parent for each predictor
        var betterParent = score1 >= score2 ? parent1 : parent2;
        var worseParent = score1 >= score2 ? parent2 : parent1;

        var betterPredictors = betterParent.GetPredictors().ToDictionary(p => p.Name, p => p.Predictor);
        var worsePredictors = worseParent.GetPredictors().ToDictionary(p => p.Name, p => p.Predictor);

        foreach (var (name, childPredictor) in child.GetPredictors())
        {
            if (betterPredictors.TryGetValue(name, out var better))
            {
                // Default to the better parent's instruction, with 20% chance of using the other
                // parent's instruction (to maintain diversity)
                if (rng.NextDouble() < 0.2 && worsePredictors.TryGetValue(name, out var worse))
                    childPredictor.Instructions = worse.Instructions;
                else
                    childPredictor.Instructions = better.Instructions;
            }
        }

        return child;
    }

    /// <summary>
    /// Evaluates a module on the FULL training set. Returns per-example scores
    /// in a stable order for Pareto frontier tracking. All candidates are evaluated
    /// on the same examples so that per-instance comparison is valid.
    /// </summary>
    private static async Task<IReadOnlyList<ExampleResult>> EvaluateOnFullSet<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var results = new ExampleResult[trainSet.Count];
        var evalModule = module.Clone<TModule>();

        for (int i = 0; i < trainSet.Count; i++)
        {
            evalModule.Trace = new Trace();
            try
            {
                var output = await evalModule.ForwardAsync(
                    trainSet[i].WithInputs(), cancellationToken);
                var score = metric(trainSet[i], output);
                results[i] = new ExampleResult(trainSet[i], output, score);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                results[i] = new ExampleResult(trainSet[i], "error", 0f);
            }
        }

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
            try
            {
                var output = await evalModule.ForwardAsync(example.WithInputs(), cancellationToken);
                totalScore += metric(example, output);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                count++;
                // Score as 0 for failures
            }
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
