using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace LMP.Optimizers;

/// <summary>
/// Bayesian optimization over both instructions and demo set selection.
/// Implements DSPy's MIPROv2 algorithm: bootstrap demos, propose instruction
/// variants via an LM, then search over (instruction × demo-set) per predictor
/// using a Tree-structured Parzen Estimator (TPE).
/// </summary>
public sealed class MIPROv2 : IOptimizer
{
    private readonly IChatClient _proposalClient;
    private readonly int _numTrials;
    private readonly int _numInstructionCandidates;
    private readonly int _numDemoSubsets;
    private readonly int _maxDemos;
    private readonly float _metricThreshold;
    private readonly double _gamma;
    private readonly int? _seed;

    /// <summary>
    /// Creates a new MIPROv2 optimizer.
    /// </summary>
    /// <param name="proposalClient">
    /// Chat client used to generate instruction candidates in Phase 2.
    /// This can be a different (possibly cheaper) model than the one used by predictors.
    /// </param>
    /// <param name="numTrials">Number of Bayesian search trials. Default is 20.</param>
    /// <param name="numInstructionCandidates">
    /// Number of instruction variants to propose per predictor. Default is 5.
    /// </param>
    /// <param name="numDemoSubsets">
    /// Number of random demo subsets to create per predictor. Default is 5.
    /// </param>
    /// <param name="maxDemos">Maximum demos per predictor in each subset. Default is 4.</param>
    /// <param name="metricThreshold">
    /// Minimum metric score for a trace to be used as a demo during bootstrapping. Default is 1.0.
    /// </param>
    /// <param name="gamma">
    /// TPE quantile threshold (0, 1). Top gamma fraction of trials are "good". Default is 0.25.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="proposalClient"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="numTrials"/> is less than 1,
    /// <paramref name="numInstructionCandidates"/> is less than 1,
    /// <paramref name="numDemoSubsets"/> is less than 1,
    /// <paramref name="maxDemos"/> is less than 1,
    /// or <paramref name="gamma"/> is not in (0, 1).
    /// </exception>
    public MIPROv2(
        IChatClient proposalClient,
        int numTrials = 20,
        int numInstructionCandidates = 5,
        int numDemoSubsets = 5,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        double gamma = 0.25,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(proposalClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numInstructionCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numDemoSubsets, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);
        if (gamma is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be in (0, 1).");

        _proposalClient = proposalClient;
        _numTrials = numTrials;
        _numInstructionCandidates = numInstructionCandidates;
        _numDemoSubsets = numDemoSubsets;
        _maxDemos = maxDemos;
        _metricThreshold = metricThreshold;
        _gamma = gamma;
        _seed = seed;
    }

    /// <summary>
    /// Optimizes the module using three-phase MIPROv2:
    /// <list type="number">
    /// <item><description>Phase 1 — Bootstrap a pool of demos via <see cref="BootstrapFewShot"/>.</description></item>
    /// <item><description>Phase 2 — Propose instruction candidates via LM.</description></item>
    /// <item><description>Phase 3 — Bayesian search over (instruction, demo-set) per predictor.</description></item>
    /// </list>
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to optimize. Cloned for each trial.</param>
    /// <param name="trainSet">Training examples — split 80/20 into bootstrap/validation internally.</param>
    /// <param name="metric">Scoring function: (example, module output) → score in [0, 1].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The best-performing candidate module after Bayesian search.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/>, <paramref name="trainSet"/>, or <paramref name="metric"/> is null.
    /// </exception>
    public async Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        if (trainSet.Count == 0)
            return module;

        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        var (bootstrapSplit, valSplit) = BootstrapRandomSearch.SplitDataset(trainSet, 0.8, rng);

        if (valSplit.Count == 0)
            valSplit = bootstrapSplit;

        // Phase 1: Bootstrap a pool of demos per predictor
        var demoPool = await BootstrapDemoPoolAsync(
            module, bootstrapSplit, metric, rng, cancellationToken);

        // Phase 2: Generate instruction candidates per predictor via LM
        var instructionCandidates = await ProposeInstructionsAsync(
            module, demoPool, cancellationToken);

        // Phase 3: Bayesian search over (instruction_index, demo_set_index) per predictor
        var predictors = module.GetPredictors();

        // Create random demo subsets for each predictor
        var demoSubsets = CreateDemoSubsets(demoPool, predictors, rng);

        // Build the TPE search space
        var cardinalities = new Dictionary<string, int>();
        foreach (var (name, _) in predictors)
        {
            cardinalities[$"{name}_instr"] = instructionCandidates.TryGetValue(name, out var instrs)
                ? instrs.Count : 1;
            cardinalities[$"{name}_demos"] = demoSubsets.TryGetValue(name, out var subsets)
                ? subsets.Count : 1;
        }

        var sampler = new CategoricalTpeSampler(cardinalities, _gamma, _seed);
        TModule bestCandidate = module;
        float bestScore = float.MinValue;

        for (int trial = 0; trial < _numTrials; trial++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = sampler.Propose();
            var candidate = module.Clone<TModule>();

            // Apply the proposed configuration to the candidate
            foreach (var (name, predictor) in candidate.GetPredictors())
            {
                // Set instruction
                if (instructionCandidates.TryGetValue(name, out var instrs))
                {
                    int instrIdx = config.TryGetValue($"{name}_instr", out var idx) ? idx : 0;
                    predictor.Instructions = instrs[instrIdx % instrs.Count];
                }

                // Set demos
                if (demoSubsets.TryGetValue(name, out var subsets) && subsets.Count > 0)
                {
                    int demoIdx = config.TryGetValue($"{name}_demos", out var didx) ? didx : 0;
                    var selectedDemos = subsets[demoIdx % subsets.Count];

                    predictor.Demos.Clear();
                    foreach (var (input, output) in selectedDemos)
                    {
                        predictor.AddDemo(input, output);
                    }
                }
            }

            // Evaluate
            var result = await Evaluator.EvaluateAsync(
                candidate, valSplit, metric, cancellationToken: cancellationToken);

            sampler.Report(config, result.AverageScore);

            if (result.AverageScore > bestScore)
            {
                bestScore = result.AverageScore;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Phase 1: Runs BootstrapFewShot to collect a pool of successful demos per predictor.
    /// Uses a larger maxDemos to build a diverse pool, not just the final demo set.
    /// </summary>
    private async Task<Dictionary<string, List<(object Input, object Output)>>> BootstrapDemoPoolAsync<TModule>(
        TModule module,
        List<Example> trainSplit,
        Func<Example, object, float> metric,
        Random rng,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        // Bootstrap with a larger demo count to build a pool
        int poolSize = _maxDemos * _numDemoSubsets;
        var bootstrap = new BootstrapFewShot(poolSize, metricThreshold: _metricThreshold);

        // Clone the module for bootstrapping so the original stays clean
        var bootstrapModule = module.Clone<TModule>();
        await bootstrap.CompileAsync(bootstrapModule, trainSplit, metric, cancellationToken);

        // Extract demos from the bootstrapped module
        var pool = new Dictionary<string, List<(object Input, object Output)>>();
        foreach (var (name, predictor) in bootstrapModule.GetPredictors())
        {
            var demos = new List<(object Input, object Output)>();
            foreach (var demo in predictor.Demos)
            {
                // Demos are stored as (TInput, TOutput) tuples in the IList
                if (demo is System.Runtime.CompilerServices.ITuple tuple && tuple.Length == 2)
                {
                    demos.Add((tuple[0]!, tuple[1]!));
                }
            }
            pool[name] = demos;
        }

        return pool;
    }

    /// <summary>
    /// Phase 2: Uses the proposal LM client to generate diverse instruction variants
    /// for each predictor. The LM sees the current instructions, field names, and
    /// example demos to produce diverse phrasings.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> ProposeInstructionsAsync<TModule>(
        TModule module,
        Dictionary<string, List<(object Input, object Output)>> demoPool,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var (name, predictor) in module.GetPredictors())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = new List<string>();

            // Always include the original instruction as the first candidate
            candidates.Add(predictor.Instructions);

            // Generate N-1 new instruction candidates via LM
            var demoExamples = demoPool.TryGetValue(name, out var demos)
                ? demos.Take(3).ToList()
                : [];

            for (int i = 0; i < _numInstructionCandidates - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var proposed = await GenerateInstructionAsync(
                        name, predictor.Instructions, demoExamples, i, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(proposed))
                        candidates.Add(proposed.Trim());
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // If instruction generation fails, skip this candidate.
                    // We still have the original instruction.
                }
            }

            result[name] = candidates;
        }

        return result;
    }

    /// <summary>
    /// Generates a single instruction candidate via the proposal LM.
    /// </summary>
    private async Task<string> GenerateInstructionAsync(
        string predictorName,
        string currentInstruction,
        List<(object Input, object Output)> demoExamples,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        var systemPrompt = """
            You are an expert prompt engineer. Your task is to write a clear, concise instruction
            for a language model predictor. The instruction should tell the model what task to perform
            and what kind of output to produce.

            Generate a single instruction that is different from the current one but achieves the same goal.
            Be creative and diverse in your phrasing. Output ONLY the instruction text, nothing else.
            """;

        var userPrompt = $"Predictor name: {predictorName}\n" +
                         $"Current instruction: {currentInstruction}\n";

        if (demoExamples.Count > 0)
        {
            userPrompt += "\nExample input/output pairs:\n";
            foreach (var (input, output) in demoExamples)
            {
                userPrompt += $"  Input: {input}\n  Output: {output}\n";
            }
        }

        userPrompt += $"\nGenerate instruction variant #{candidateIndex + 1} (different from the current one):";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await _proposalClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Creates random demo subsets from the bootstrapped pool for each predictor.
    /// Each subset contains up to <see cref="_maxDemos"/> demos randomly sampled
    /// from the pool. If the pool is empty for a predictor, returns an empty list of subsets.
    /// </summary>
    private Dictionary<string, List<List<(object Input, object Output)>>> CreateDemoSubsets(
        Dictionary<string, List<(object Input, object Output)>> demoPool,
        IReadOnlyList<(string Name, IPredictor Predictor)> predictors,
        Random rng)
    {
        var result = new Dictionary<string, List<List<(object Input, object Output)>>>();

        foreach (var (name, _) in predictors)
        {
            var subsets = new List<List<(object Input, object Output)>>();

            if (demoPool.TryGetValue(name, out var pool) && pool.Count > 0)
            {
                for (int i = 0; i < _numDemoSubsets; i++)
                {
                    // Fisher-Yates shuffle of indices, then take up to maxDemos
                    var indices = Enumerable.Range(0, pool.Count).ToArray();
                    for (int j = indices.Length - 1; j > 0; j--)
                    {
                        int k = rng.Next(j + 1);
                        (indices[j], indices[k]) = (indices[k], indices[j]);
                    }

                    var subset = indices.Take(Math.Min(_maxDemos, pool.Count))
                        .Select(idx => pool[idx])
                        .ToList();
                    subsets.Add(subset);
                }
            }
            else
            {
                // No demos available: create one empty subset so the search space is valid
                subsets.Add([]);
            }

            result[name] = subsets;
        }

        return result;
    }
}
