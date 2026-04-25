using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;

#pragma warning disable CS0618 // uses ISampler intentionally for backward compat
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
    private readonly Func<Dictionary<string, int>, ISampler>? _samplerFactory;
    private readonly int _numTrials;
    private List<TrialResult>? _lastTrialHistory;
    private Dictionary<string, int>? _lastCardinalities;
    private readonly int _numInstructionCandidates;
    private readonly int _numDemoSubsets;
    private readonly int _maxDemos;
    private readonly float _metricThreshold;
    private readonly double _gamma;
    private readonly int? _seed;
    private readonly int _maxConcurrency;

    /// <summary>
    /// Creates a new MIPROv2 optimizer.
    /// </summary>
    /// <param name="proposalClient">
    /// Chat client used to generate instruction candidates in Phase 2.
    /// This can be a different (possibly cheaper) model than the one used by predictors.
    /// </param>
    /// <param name="samplerFactory">
    /// Optional factory that creates an <see cref="ISampler"/> given parameter cardinalities.
    /// If <c>null</c>, uses <see cref="CategoricalTpeSampler"/> with the configured gamma and seed.
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
    /// Only used when <paramref name="samplerFactory"/> is null.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <param name="maxConcurrency">
    /// Maximum number of concurrent evaluation tasks during Phase 3 trial evaluation.
    /// Lower values reduce API rate-limit pressure. Default is 4.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="proposalClient"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="numTrials"/> is less than 1,
    /// <paramref name="numInstructionCandidates"/> is less than 1,
    /// <paramref name="numDemoSubsets"/> is less than 1,
    /// <paramref name="maxDemos"/> is less than 1,
    /// <paramref name="maxConcurrency"/> is less than 1,
    /// or <paramref name="gamma"/> is not in (0, 1).
    /// </exception>
    public MIPROv2(
        IChatClient proposalClient,
        Func<Dictionary<string, int>, ISampler>? samplerFactory = null,
        int numTrials = 20,
        int numInstructionCandidates = 5,
        int numDemoSubsets = 5,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        double gamma = 0.25,
        int? seed = null,
        int maxConcurrency = 4)
    {
        ArgumentNullException.ThrowIfNull(proposalClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numInstructionCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numDemoSubsets, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        if (gamma is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be in (0, 1).");

        _proposalClient = proposalClient;
        _samplerFactory = samplerFactory;
        _numTrials = numTrials;
        _numInstructionCandidates = numInstructionCandidates;
        _numDemoSubsets = numDemoSubsets;
        _maxDemos = maxDemos;
        _metricThreshold = metricThreshold;
        _gamma = gamma;
        _seed = seed;
        _maxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// Trial history from the last <see cref="CompileAsync{TModule}"/> call.
    /// Contains (configuration, score) pairs for each Bayesian search trial.
    /// Useful with <see cref="TraceAnalyzer"/> for post-optimization analysis.
    /// Returns <c>null</c> if <see cref="CompileAsync{TModule}"/> hasn't been called.
    /// </summary>
    public IReadOnlyList<TrialResult>? LastTrialHistory => _lastTrialHistory;

    /// <summary>
    /// Parameter cardinalities from the last <see cref="CompileAsync{TModule}"/> call.
    /// Maps parameter names (e.g. <c>"classify_instr"</c>, <c>"classify_demos"</c>) to
    /// the number of choices the optimizer evaluated. Use this instead of hardcoding
    /// cardinality values when constructing a <see cref="TraceAnalyzer"/> posteriors dict
    /// — the actual demo cardinality is <c>numDemoSubsets + 1</c> because the optimizer
    /// always includes a zero-shot (no demos) option.
    /// Returns <c>null</c> if <see cref="CompileAsync{TModule}"/> hasn't been called.
    /// </summary>
    public IReadOnlyDictionary<string, int>? LastCardinalities => _lastCardinalities;

    /// <inheritdoc />
    /// <remarks>
    /// Accepts both <see cref="LmpModule"/> targets (typed path — routes to
    /// <see cref="CompileAsync{TModule}"/>) and composite <see cref="ChainTarget"/>
    /// targets (walker-based path). Walker-empty targets (bare
    /// <see cref="Predictor{TIn,TOut}"/>, unsupported composite shapes) are
    /// rejected with <see cref="ArgumentException"/>.
    /// Composite targets optimize end-to-end but do not emit <c>.g.cs</c>
    /// artifacts; optimized state is applied in-place via
    /// <see cref="IOptimizationTarget.ApplyState"/>. Composite artifact emission
    /// is tracked as T4.
    /// </remarks>
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Clear stale state from prior runs so stale trial history / cardinalities
        // don't leak into the caller's context (rubber-duck Q7.1).
        _lastTrialHistory = null;
        _lastCardinalities = null;

        // Reject targets with no optimizable predictors (bare Predictor<>, unsupported
        // composite shapes). This preserves the pre-T2i.a bare-predictor rejection
        // contract without blocking composite ChainTarget-of-modules.
        var discoveredPredictors = PredictorWalker.Enumerate(ctx.Target).ToList();
        if (discoveredPredictors.Count == 0)
            throw new ArgumentException(
                $"{nameof(MIPROv2)} requires a target containing optimizable predictors " +
                $"(LmpModule or ChainTarget of modules).",
                nameof(ctx));

        if (ctx.Target is LmpModule module)
        {
            // Typed path: preserves CompileAsync<TModule> public API and artifact emission.
            var best = await CompileAsync(module, ctx.TrainSet, ctx.Metric, CompileOptions.RuntimeOnly, ct)
                .ConfigureAwait(false);

            if (!ReferenceEquals(best, module))
                ctx.Target.ApplyState(best.GetState());
        }
        else
        {
            // Composite path: walker-based end-to-end optimization. No artifact emission.
            var (best, _) = await CompileInternalAsync(ctx.Target, ctx.TrainSet, ctx.Metric, ct)
                .ConfigureAwait(false);

            if (!ReferenceEquals(best, ctx.Target))
                ctx.Target.ApplyState(best.GetState());
        }

        // Propagate per-trial results to the shared TrialHistory so the pipeline budget gate
        // and post-run analysis tools (TraceAnalyzer) can see MIPROv2's search history.
        // Guard against stale data from a prior run on an empty train set.
        if (ctx.TrainSet.Count > 0 && _lastTrialHistory is { Count: > 0 })
        {
            foreach (var t in _lastTrialHistory)
            {
                ctx.TrialHistory.Add(new Trial(
                    Score: t.Score,
                    Cost: t.Cost ?? new TrialCost(0, 0, 0, 0, 0),
                    Notes: "MIPROv2 trial"));
            }
        }

        // Publish the discovered parameter space to the context so downstream pipeline steps
        // (e.g., Z3Feasibility in Phase E) can see what MIPROv2 searched over.
        if (_lastCardinalities is { Count: > 0 } && ctx.SearchSpace.IsEmpty)
            ctx.SearchSpace = TypedParameterSpace.FromCategorical(_lastCardinalities);
    }

    /// <summary>
    /// Optimizes the module using three-phase MIPROv2:
    /// <list type="number">
    /// <item><description>Phase 1 — Bootstrap a pool of demos via <see cref="BootstrapFewShot"/>.</description></item>
    /// <item><description>Phase 2 — Propose instruction candidates via LM.</description></item>
    /// <item><description>Phase 3 — Bayesian search over (instruction, demo-set) per predictor.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Per-trial candidate construction goes through the fractal
    /// <see cref="IOptimizationTarget.WithParameters"/> seam:
    /// <c>var candidate = (TModule)module.WithParameters(pa);</c>. Because
    /// <see cref="LmpModule.WithParameters"/> routes per-predictor sub-assignments
    /// to each child's <see cref="IOptimizationTarget.WithParameters"/>, every
    /// <see cref="IPredictor"/> in the module must also implement
    /// <see cref="IOptimizationTarget"/>. <see cref="Predictor{TInput, TOutput}"/>
    /// from <c>LMP.Core</c> already does; custom <see cref="IPredictor"/>
    /// implementations must implement <see cref="IOptimizationTarget"/> as well.
    /// </remarks>
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
        CompileOptions? options = null,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        if (trainSet.Count == 0)
            return module;

        var (bestTarget, bestScore) = await CompileInternalAsync(module, trainSet, metric, cancellationToken)
            .ConfigureAwait(false);

        var bestCandidate = (TModule)bestTarget;

        // Auto-emit .g.cs artifact — typed-path only. Composite artifact emission
        // is deferred to T4 (shape not yet defined for multi-stage compositions).
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            await CSharpArtifactWriter.WriteAsync(
                bestCandidate, outputDir, bestScore, nameof(MIPROv2),
                options?.TrainDataPath, options?.Baseline, cancellationToken);
        }

        return bestCandidate;
    }

    /// <summary>
    /// Shared implementation of the three-phase MIPROv2 loop that operates on
    /// any <see cref="IOptimizationTarget"/>. Enumerates predictors via
    /// <see cref="PredictorWalker"/> so composite targets (ChainTarget of
    /// modules) are treated symmetrically with bare <see cref="LmpModule"/>s.
    /// Returns the best candidate target + its validation score.
    /// </summary>
    private async Task<(IOptimizationTarget Best, float Score)> CompileInternalAsync(
        IOptimizationTarget target,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken)
    {
        var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        var (bootstrapSplit, valSplit) = BootstrapRandomSearch.SplitDataset(trainSet, 0.8, rng);

        if (valSplit.Count == 0)
            valSplit = bootstrapSplit;

        // Phase 1: Bootstrap a pool of demos keyed by predictor path.
        var demoPool = await BootstrapDemoPoolAsync(
            target, bootstrapSplit, metric, cancellationToken).ConfigureAwait(false);

        // Phase 2: Generate instruction candidates per predictor via LM (walker-based).
        var instructionCandidates = await ProposeInstructionsAsync(
            target, demoPool, cancellationToken).ConfigureAwait(false);

        // Phase 3: Bayesian search over (instruction_index, demo_set_index) per predictor path.
        var predictors = PredictorWalker.Enumerate(target).ToList();

        // Create random demo subsets for each predictor path.
        var demoSubsets = CreateDemoSubsets(demoPool, predictors, rng);

        // Build the TPE search space — keyed by path so duplicate leaf names across
        // composite stages (e.g., child_0.classify vs child_1.classify) don't collide.
        var cardinalities = new Dictionary<string, int>();
        foreach (var (path, _) in predictors)
        {
            cardinalities[$"{path}_instr"] = instructionCandidates.TryGetValue(path, out var instrs)
                ? instrs.Count : 1;
            cardinalities[$"{path}_demos"] = demoSubsets.TryGetValue(path, out var subsets)
                ? subsets.Count : 1;
        }

        var sampler = _samplerFactory?.Invoke(cardinalities)
            ?? new CategoricalTpeSampler(cardinalities, _gamma, _seed);
        _lastCardinalities = cardinalities;
        IOptimizationTarget bestCandidate = target;
        float bestScore = float.MinValue;
        var trialHistory = new List<TrialResult>(_numTrials);

        for (int trial = 0; trial < _numTrials; trial++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = sampler.Propose();

            // Build a ParameterAssignment keyed by predictor path. The fractal
            // WithParameters seam routes `"{path}.instructions"` / `"{path}.demos"`
            // through ChainTarget → LmpModule → predictor without ambiguity.
            var pa = ParameterAssignment.Empty;
            foreach (var (path, _) in predictors)
            {
                if (instructionCandidates.TryGetValue(path, out var instrs) && instrs.Count > 0)
                {
                    int instrIdx = config.TryGetValue($"{path}_instr", out var iIdx) ? iIdx : 0;
                    pa = pa.With($"{path}.instructions", instrs[instrIdx % instrs.Count]);
                }

                if (demoSubsets.TryGetValue(path, out var subsets) && subsets.Count > 0)
                {
                    int demoIdx = config.TryGetValue($"{path}_demos", out var dIdx) ? dIdx : 0;
                    var selectedDemos = subsets[demoIdx % subsets.Count];
                    // Box each demo as ValueTuple<object,object> per the
                    // IPredictor.WithParameters 'demos' contract.
                    var demoList = (IReadOnlyList<object>)selectedDemos
                        .Select(d => (object)new ValueTuple<object, object>(d.Input, d.Output))
                        .ToList();
                    pa = pa.With($"{path}.demos", demoList);
                }
            }

            var candidate = target.WithParameters(pa);

            // Evaluate via the shared F2 helper — accumulates TrialCost per example by
            // calling IOptimizationTarget.ExecuteAsync (fresh Trace per call). We do NOT
            // use the Evaluator.EvaluateAsync IOT overload here because it does not
            // aggregate TrialCost — a switch would silently regress cost-aware sampling.
            var (avgScore, trialCost) = await EvalCandidateAsync(
                candidate, valSplit, metric, _maxConcurrency, cancellationToken).ConfigureAwait(false);

            sampler.Update(config, avgScore, trialCost);
            trialHistory.Add(new TrialResult(new Dictionary<string, int>(config), avgScore, trialCost));

            if (avgScore > bestScore)
            {
                bestScore = avgScore;
                bestCandidate = candidate;
            }
        }

        _lastTrialHistory = trialHistory;
        return (bestCandidate, bestScore);
    }

    /// <summary>
    /// Shared per-trial evaluation helper. Runs the candidate against
    /// <paramref name="valSplit"/> via <see cref="IOptimizationTarget.ExecuteAsync"/>
    /// (fresh trace per call) so per-example token / API-call usage can be
    /// aggregated into a single <see cref="TrialCost"/>. Used by both typed
    /// and composite paths.
    /// </summary>
    private static async Task<(float Score, TrialCost Cost)> EvalCandidateAsync(
        IOptimizationTarget candidate,
        IReadOnlyList<Example> valSplit,
        Func<Example, object, float> metric,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        if (valSplit.Count == 0)
            return (0f, new TrialCost(0, 0, 0, 0, 0));

        long totalTokens = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        long apiCalls = 0;
        var scores = new float[valSplit.Count];

        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, valSplit.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (i, ct) =>
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var (output, trace) = await candidate.ExecuteAsync(valSplit[i].WithInputs(), ct)
                            .ConfigureAwait(false);
                        scores[i] = metric(valSplit[i], output);
                        Interlocked.Add(ref totalTokens, trace.TotalTokens);
                        Interlocked.Add(ref inputTokens, trace.InputTokens);
                        Interlocked.Add(ref outputTokens, trace.OutputTokens);
                        Interlocked.Add(ref apiCalls, trace.TotalApiCalls);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        scores[i] = 0f;
                        return;
                    }
                }
            }).ConfigureAwait(false);
        stopwatch.Stop();

        float avg = 0f;
        for (int i = 0; i < scores.Length; i++)
            avg += scores[i];
        avg /= scores.Length;

        var cost = new TrialCost(
            TotalTokens: totalTokens,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            ElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
            ApiCalls: (int)apiCalls);
        return (avg, cost);
    }

    /// <summary>
    /// Phase 1: Runs BootstrapFewShot to collect a pool of successful demos per predictor path.
    /// Uses a larger maxDemos to build a diverse pool, not just the final demo set.
    /// Operates on any <see cref="IOptimizationTarget"/> via BFS's IOT entry point so
    /// both bare <see cref="LmpModule"/>s and composite <see cref="ChainTarget"/>s share
    /// a single bootstrap path.
    /// </summary>
    private async Task<Dictionary<string, List<(object Input, object Output)>>> BootstrapDemoPoolAsync(
        IOptimizationTarget target,
        List<Example> trainSplit,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken)
    {
        // Bootstrap with a larger demo count to build a pool
        int poolSize = _maxDemos * _numDemoSubsets;
        var bootstrap = new BootstrapFewShot(poolSize, metricThreshold: _metricThreshold);

        // WithParameters(Empty) gives a pristine clone for both LmpModule and ChainTarget,
        // so BootstrapFewShot's in-place ApplyState doesn't mutate the user's target.
        var bootstrapTarget = target.WithParameters(ParameterAssignment.Empty);
        await bootstrap.OptimizeAsync(new OptimizationContext
        {
            Target = bootstrapTarget,
            TrainSet = trainSplit,
            Metric = metric
        }, cancellationToken).ConfigureAwait(false);

        // Extract demos from the bootstrapped target, keyed by predictor path
        // (raw name for LmpModule; "child_{i}.{inner}" for composite ChainTarget).
        var pool = new Dictionary<string, List<(object Input, object Output)>>();
        foreach (var (path, predictor) in PredictorWalker.Enumerate(bootstrapTarget))
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
            pool[path] = demos;
        }

        return pool;
    }

    /// <summary>
    /// Phase 2: Uses the proposal LM client to generate diverse instruction variants
    /// for each predictor. The LM sees the current instructions, field names, and
    /// example demos to produce diverse phrasings. Keyed by predictor path so
    /// duplicate leaf names across composite stages don't collide.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> ProposeInstructionsAsync(
        IOptimizationTarget target,
        Dictionary<string, List<(object Input, object Output)>> demoPool,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var (path, predictor) in PredictorWalker.Enumerate(target))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = new List<string>();

            // Always include the original instruction as the first candidate
            candidates.Add(predictor.Instructions);

            // Generate N-1 new instruction candidates via LM
            var demoExamples = demoPool.TryGetValue(path, out var demos)
                ? demos.Take(3).ToList()
                : [];

            for (int i = 0; i < _numInstructionCandidates - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var proposed = await GenerateInstructionAsync(
                        path, predictor.Instructions, demoExamples, i, cancellationToken);
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

            result[path] = candidates;
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

            // Always include zero-shot (empty demos) as the first option.
            // This lets the optimizer choose "no demos" when that outperforms few-shot —
            // which matters for tasks where the base model is already strong or where
            // bootstrapped demos don't generalize well to the validation split.
            // This mirrors DSPy's MIPROv2 behavior.
            subsets.Add([]);

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

            result[name] = subsets;
        }

        return result;
    }
}
