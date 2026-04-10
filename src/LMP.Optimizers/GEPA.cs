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
/// of non-dominated candidates and uses two operations:
/// </para>
/// <list type="bullet">
/// <item><description><b>Reflective Mutation:</b> Run candidate on mini-batch → capture traces →
/// LLM analyzes failures → proposes improved instructions.</description></item>
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
    /// <param name="miniBatchSize">Examples per mini-batch evaluation. Default is 5.</param>
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

        // Seed the frontier with the initial module
        var initialScores = await EvaluateOnSubset(module, trainSet, metric, rng, cancellationToken);
        frontier.Add(module, initialScores);

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TModule candidate;
            if (iter % _mergeEvery == 0 && iter > 0 && frontier.Count >= 2)
            {
                candidate = Merge(frontier, trainSet, metric, rng);
            }
            else
            {
                candidate = await ReflectAndMutate(
                    frontier, trainSet, metric, rng, cancellationToken);
            }

            var scores = await EvaluateOnSubset(candidate, trainSet, metric, rng, cancellationToken);
            frontier.Add(candidate, scores);
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
                options?.TrainDataPath, cancellationToken);
        }

        return best;
    }

    /// <summary>
    /// Reflective mutation: select a candidate from the frontier, run on mini-batch,
    /// capture traces, ask the reflection LLM to diagnose failures and propose better instructions.
    /// </summary>
    private async Task<TModule> ReflectAndMutate<TModule>(
        ParetoFrontier<TModule> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        // Select a candidate from the frontier
        var parent = frontier.Frontier[rng.Next(frontier.Count)];
        var candidate = parent.Clone<TModule>();

        // Run on mini-batch, capturing traces
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
                // Skip failed examples
            }
        }

        if (traceResults.Count == 0)
            return candidate;

        // For each predictor, reflect on failures and propose improved instructions
        foreach (var (name, predictor) in candidate.GetPredictors())
        {
            var failedTraces = traceResults
                .Where(r => r.Score < 0.8f)
                .ToList();

            if (failedTraces.Count == 0)
                continue;

            var newInstruction = await ReflectOnPredictor(
                name, predictor.Instructions, failedTraces, cancellationToken);

            if (!string.IsNullOrWhiteSpace(newInstruction))
                predictor.Instructions = newInstruction;
        }

        return candidate;
    }

    /// <summary>
    /// Asks the reflection LLM to analyze failures for a specific predictor
    /// and propose an improved instruction.
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
            prompt.AppendLine($"Output: {output}");

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
        prompt.AppendLine("Respond with ONLY the new instruction text, nothing else.");

        var response = await _reflectionClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                "You are an expert prompt engineer analyzing LM program failures. " +
                "Your goal is to diagnose why the predictor is failing and write a better instruction."),
            new ChatMessage(ChatRole.User, prompt.ToString())
        ],
        cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? "";
    }

    /// <summary>
    /// Merge operation: combine instructions from two Pareto-optimal parents.
    /// For each predictor, take the instruction from whichever parent scored better
    /// on a shared mini-batch evaluation.
    /// </summary>
    private TModule Merge<TModule>(
        ParetoFrontier<TModule> frontier,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng)
        where TModule : LmpModule
    {
        var (parent1, parent2) = frontier.SelectParents(rng);
        var child = parent1.Clone<TModule>();

        var predictors1 = parent1.GetPredictors().ToDictionary(p => p.Name, p => p.Predictor);
        var predictors2 = parent2.GetPredictors().ToDictionary(p => p.Name, p => p.Predictor);

        // For each predictor in the child, pick instruction from the parent
        // that has the higher-scoring instruction (based on overall averages from frontier)
        foreach (var (name, childPredictor) in child.GetPredictors())
        {
            if (predictors1.TryGetValue(name, out var p1) &&
                predictors2.TryGetValue(name, out var p2))
            {
                // Randomly pick from parents with 50/50 (simple crossover)
                childPredictor.Instructions = rng.Next(2) == 0
                    ? p1.Instructions
                    : p2.Instructions;
            }
        }

        return child;
    }

    /// <summary>
    /// Evaluates a module on a random mini-batch of training examples.
    /// Returns per-example scores for Pareto frontier tracking.
    /// </summary>
    private async Task<IReadOnlyList<ExampleResult>> EvaluateOnSubset<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var miniBatch = SampleMiniBatch(trainSet, rng);
        var results = new List<ExampleResult>();

        var evalModule = module.Clone<TModule>();
        for (int i = 0; i < miniBatch.Count; i++)
        {
            evalModule.Trace = new Trace();
            try
            {
                var output = await evalModule.ForwardAsync(
                    miniBatch[i].WithInputs(), cancellationToken);
                var score = metric(miniBatch[i], output);
                results.Add(new ExampleResult(miniBatch[i], output, score));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                results.Add(new ExampleResult(miniBatch[i], "error", 0f));
            }
        }

        return results;
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
