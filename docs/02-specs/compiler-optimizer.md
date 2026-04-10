# Optimizer Specification

> **Status:** Implementation Reference — aligned with `system-architecture.md` Layer 3
> **Target:** .NET 10 / C# 14
> **Audience:** Implementers — self-contained enough to build `LMP.Optimizers` from scratch.
> **DSPy lineage:** [`dspy/evaluate`](https://github.com/stanfordnlp/dspy/tree/main/dspy/evaluate), [`dspy/teleprompt`](https://github.com/stanfordnlp/dspy/tree/main/dspy/teleprompt)

---

## 1. What "Compiling" an LM Program Means

An LM program starts life with defaults — a hand-written instruction, no few-shot examples. **Compiling** that program means running it on training data, collecting successful traces, and filling in its learnable parameters (demos and instructions) so it performs well.

This is exactly what DSPy calls "compilation": **the optimizer fills `predictor.Demos` and optionally `predictor.Instructions`, then returns the same module.**

```
run on training data → collect successful traces → fill demos → evaluate → return best
```

### What Gets Optimized

| Parameter | Where It Lives | Optimizer That Fills It |
|---|---|---|
| Few-shot demos | `predictor.Demos` | BootstrapFewShot, BootstrapRandomSearch, MIPROv2 |
| Instructions | `predictor.Instructions` | MIPROv2, GEPA |

That's it. No model selection, no temperature tuning, no retrieval topK — those are configuration concerns handled by `IChatClient` middleware or `IOptions<T>`. The optimizer layer focuses on what DSPy focuses on: **demos and instructions**.

---

## 2. IOptimizer — The Core Interface

All optimizers implement this interface. All return the **same module type** with parameters filled in — no new types created.

```csharp
public interface IOptimizer
{
    Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

### Design Decisions

| Decision | Rationale |
|---|---|
| Returns `TModule`, not a new type | DSPy's `compile()` returns the same module with demos filled. Same pattern. |
| `Func<Example, object, float>` metric | Simple — label + output → score in `[0, 1]`. No metric framework. |
| Single `trainSet` parameter | BootstrapFewShot needs only training data. BootstrapRandomSearch splits internally for validation. |
| `CancellationToken` on everything | Optimization runs are long. Ctrl+C must work. |

### Predictor Discovery — Source-Generated `GetPredictors()`

Optimizers need to find all `Predictor` instances inside a module to fill their `Demos`. In Python, DSPy's `named_predictors()` walks `__dict__` at runtime. In LMP, the source generator emits `GetPredictors()` at build time — zero reflection.

```csharp
// Source-generated on TicketTriageModule:
public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
    => [
        ("_classify", _classify),
        ("_draft", _draft),
    ];
```

This is how BootstrapFewShot knows which predictors exist and can fill each one's `Demos` collection.

---

## 3. Evaluator

The Evaluator runs a module on a dataset and scores results. It is the foundation — every optimizer uses it internally. Uses `System.Numerics.Tensors.TensorPrimitives` for SIMD-accelerated aggregate score computation.

### API

```csharp
public static class Evaluator
{
    // Primary untyped overload (sync metric)
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;

    // Primary untyped overload (async metric — for LLM-as-judge)
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, Task<float>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;

    // Typed overload (float metric)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    // Typed overload (bool metric)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, bool> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    // Typed overload (async float metric)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, Task<float>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    // Typed overload (async bool metric)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, Task<bool>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);
}
```

The typed overloads delegate to the untyped primary via `Metric.Create()` / `Metric.CreateAsync()`. When module output and dataset label types match, the compiler infers all three type parameters automatically.

### Algorithm

```
for each example in devSet (via Parallel.ForEachAsync):
    output = await module.ForwardAsync(example.WithInputs())
    score  = metric(example, output)
    collect (example, output, score)

return EvaluationResult { PerExample, AverageScore, ... }
```

### Implementation

```csharp
public static async Task<EvaluationResult> EvaluateAsync<TModule>(
    TModule module,
    IReadOnlyList<Example> devSet,
    Func<Example, object, float> metric,
    int maxConcurrency = 4,
    CancellationToken cancellationToken = default)
    where TModule : LmpModule
{
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(devSet);
    ArgumentNullException.ThrowIfNull(metric);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

    if (devSet.Count == 0)
        return new EvaluationResult([], 0f, 0f, 0f, 0);

    var results = new ConcurrentBag<ExampleResult>();

    await Parallel.ForEachAsync(
        devSet,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = cancellationToken
        },
        async (example, ct) =>
        {
            var output = await module.ForwardAsync(example.WithInputs(), ct);
            var score = metric(example, output);
            results.Add(new ExampleResult(example, output, score));
        });

    var scores = results.Select(r => r.Score).ToArray();
    return new EvaluationResult(
        PerExample: [.. results],
        AverageScore: TensorPrimitives.Average<float>(scores),
        MinScore: TensorPrimitives.Min<float>(scores),
        MaxScore: TensorPrimitives.Max<float>(scores),
        Count: scores.Length);
}
```

> **Note:** `TensorPrimitives.Average/Min/Max` from `System.Numerics.Tensors` provides SIMD-accelerated aggregate computation. This avoids LINQ allocations and is consistent with how other numerical operations are done throughout `LMP.Optimizers` (e.g., in `CategoricalTpeSampler`, `CategoricalRandomForest`, `TraceAnalyzer`, and `ParetoFrontier`).

### Result Types

```csharp
public sealed record EvaluationResult(
    IReadOnlyList<ExampleResult> PerExample,
    float AverageScore,
    float MinScore,
    float MaxScore,
    int Count);

public sealed record ExampleResult(
    Example Example,
    object Output,
    float Score);
```

### Metric Functions

There are three ways to create metrics:

**1. `Metric.Create` factory — typed, compile-time safe (recommended):**

```csharp
// Same types — compiler infers both type params:
var m1 = Metric.Create((DraftReply predicted, DraftReply expected) =>
    predicted.Category == expected.Category ? 1f : 0f);

// Bool predicate — true → 1.0f, false → 0.0f:
var m2 = Metric.Create((DraftReply predicted, DraftReply expected) =>
    predicted.Category == expected.Category);

// Different predicted/expected types:
var m3 = Metric.Create<AnswerWithConfidence, SimpleAnswer>(
    (predicted, expected) => predicted.Answer == expected.Answer ? 1f : 0f);

// Async metric for LLM-as-judge:
var m4 = Metric.CreateAsync<DraftReply, DraftReply>(async (predicted, expected) =>
{
    var result = await judgeClient.GetResponseAsync<JudgeScore>(...);
    return result.Score / 5f;
});
```

**2. Inline untyped lambda — for simple cases:**

```csharp
Func<Example, object, float> accuracy =
    (example, output) => ((ClassifyTicket)output).Category ==
        ((ClassifyTicket)example.GetLabel()).Category ? 1f : 0f;
```

**3. `M.E.AI.Evaluation` evaluators — for LM-judged quality:**

`Microsoft.Extensions.AI.Evaluation` provides production-quality evaluators: `RelevancyEvaluator`, `TruthfulnessEvaluator`, `CoherenceEvaluator`, `GroundednessEvaluator`, `CompletenessEvaluator`. LMP wraps these as metric functions — it does not reimplement scoring or LM-as-judge patterns.

---

## 4. BootstrapFewShot — The Core Compile Step

This is the heart of DSPy-style optimization. It runs a teacher on training data, collects successful traces, and fills predictors with those traces as few-shot demos.

### API

```csharp
public sealed class BootstrapFewShot : IOptimizer
{
    public BootstrapFewShot(
        int maxDemos = 4,
        int maxRounds = 1,
        float metricThreshold = 1.0f);

    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

### Algorithm (from DSPy's `bootstrap.py`)

```
1. For each round 1..maxRounds:
   a. teacher = Clone(module)              // deep copy — student's demos are independent
   b. if round > 1: copy demos from student to teacher (multi-round accumulation)
   c. for each example in trainSet (sequentially — trace isolation):
      i.   teacher.Trace = new Trace()     // fresh trace per example
      ii.  output = await teacher.ForwardAsync(example.WithInputs())
      iii. score  = metric(example, output)
      iv.  if score >= metricThreshold:
           - Extract entries from teacher.Trace.Entries
           - Add to successful_traces[entry.PredictorName]
2. for each predictor in student.GetPredictors():
   a. predictor.Demos.Clear()
   b. for each trace in successful_traces[predictor.Name].Take(maxDemos):
      predictor.AddDemo(trace.Input, trace.Output)
3. return student
```

### Implementation

```csharp
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

    var successfulTraces = new ConcurrentDictionary<string, ConcurrentBag<TraceEntry>>();
    foreach (var (name, _) in module.GetPredictors())
        successfulTraces[name] = new ConcurrentBag<TraceEntry>();

    for (int round = 0; round < _maxRounds; round++)
    {
        var teacher = module.Clone<TModule>();
        if (round > 0) CopyDemos(module, teacher);

        // Sequential to ensure trace isolation — each example gets a fresh Trace.
        foreach (var example in trainSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            teacher.Trace = new Trace();
            try
            {
                var output = await teacher.ForwardAsync(example.WithInputs(), cancellationToken);
                var score = metric(example, output);

                if (score >= _metricThreshold)
                {
                    foreach (var entry in teacher.Trace.Entries)
                    {
                        if (successfulTraces.TryGetValue(entry.PredictorName, out var bag))
                            bag.Add(entry);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Skip failed examples — DSPy does the same */ }
        }
    }

    // Fill student predictors with successful demos
    foreach (var (name, predictor) in module.GetPredictors())
    {
        if (successfulTraces.TryGetValue(name, out var traces) && !traces.IsEmpty)
        {
            predictor.Demos.Clear();
            foreach (var entry in traces.Take(_maxDemos))
                predictor.AddDemo(entry.Input, entry.Output);
        }
    }

    return module;
}
```

### Key Design Points

| Point | Detail |
|---|---|
| Teacher/student split | Teacher generates traces; student receives demos. Same pattern as DSPy. |
| Sequential training | Training examples run sequentially to ensure trace isolation — each example gets a fresh `Trace` object on the teacher's `module.Trace` property. |
| Multi-round | `maxRounds > 1` copies demos from the student to the teacher for subsequent rounds, allowing demo accumulation. |
| Tracing | `module.Trace = new Trace()` sets up recording. Each `PredictAsync()` call records a `TraceEntry`. `teacher.Trace.Entries` retrieves them. |
| Failed examples | Swallowed (not thrown). DSPy's bootstrap does the same — a few failures in the training set are expected. |
| `Clone()` | Source-generated deep copy of module + all predictors. |
| `AddDemo()` | `IPredictor.AddDemo(object, object)` provides type-erased demo addition, bridging the gap between untyped trace entries and typed `(TInput, TOutput)` demo storage. |

---

## 5. BootstrapRandomSearch

Runs BootstrapFewShot N times with random demo subsets, evaluates each candidate on a validation set, returns the best.

### API

```csharp
public sealed class BootstrapRandomSearch : IOptimizer
{
    public BootstrapRandomSearch(
        int numTrials = 8,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        int? seed = null);

    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

### Algorithm

```
1. Split trainSet into train/validation (80/20 via Fisher-Yates shuffle)
2. candidates = []
3. for i in 1..numTrials:
   a. shuffled = Shuffle(trainSplit)           // different random demo subset each time
   b. candidate = await BootstrapFewShot.CompileAsync(module.Clone(), shuffled, metric)
   c. candidates.Add(candidate)
4. Evaluate all candidates on validation set (via Task.WhenAll):
   a. scores = await Task.WhenAll(candidates.Select(c => Evaluator.EvaluateAsync(c, valSet, metric)))
5. Return candidate with highest average score
```

### Key Design Points

| Point | Detail |
|---|---|
| Random subsets | Each trial shuffles the training split differently, so each candidate sees different demo examples. |
| `Task.WhenAll` for evaluation | All N candidates evaluated in parallel — true parallelism. |
| No constraint model | Selection is purely by metric score. No Pareto frontiers, no constraint enforcement. |
| Deterministic seeding | Optional `seed` parameter for reproducible results. |
| Dataset split | `SplitDataset` (internal) uses Fisher-Yates shuffle for an unbiased 80/20 split. |

---

## 6. ISampler — Bayesian Search Abstraction

The `ISampler` interface abstracts over different Bayesian search strategies. Samplers propose configurations from a categorical search space and learn from evaluation results.

### API

```csharp
public interface ISampler
{
    /// <summary>Number of completed trials recorded so far.</summary>
    int TrialCount { get; }

    /// <summary>Proposes a new configuration to evaluate.</summary>
    /// <returns>Dictionary mapping parameter name → selected category index.</returns>
    Dictionary<string, int> Propose();

    /// <summary>Reports the result of evaluating a proposed configuration.</summary>
    void Update(Dictionary<string, int> config, float score);
}
```

### Design Decisions

| Decision | Rationale |
|---|---|
| `Dictionary<string, int>` configs | Categorical-only spaces — maps parameter names to category indices. Simple, serializable. |
| `Propose()` + `Update()` | Standard ask/tell interface used by Optuna, FLAML, and ML.NET AutoML. |
| Injected via factory in MIPROv2 | `Func<Dictionary<string, int>, ISampler>? samplerFactory` — users can swap TPE for SMAC or custom samplers. |

### CategoricalTpeSampler

Minimal Tree-structured Parzen Estimator for categorical-only spaces. Maintains frequency-based surrogate models for "good" and "bad" trials, then proposes configurations that maximize `l(x) / g(x)`.

```csharp
public sealed class CategoricalTpeSampler : ISampler
{
    public CategoricalTpeSampler(
        Dictionary<string, int> parameterCardinalities,
        double gamma = 0.25,
        int? seed = null);

    public int TrialCount { get; }
    public Dictionary<string, int> Propose();
    public void Update(Dictionary<string, int> config, float score);
}
```

**How it works:**

1. For the first few trials (before enough history), uses **uniform random sampling**.
2. Once enough trials exist, sorts by score descending and splits at the `gamma` quantile (default 0.25 — top 25% are "good").
3. For each parameter, computes frequency counts with **Laplace smoothing** (add-1) for both `l(x)` (good) and `g(x)` (bad) distributions.
4. Computes acquisition values `l(x) / g(x)` per category and **samples proportionally**.
5. Uses `TensorPrimitives.Sum` for efficient normalization.

### SmacSampler

SMAC (Sequential Model-based Algorithm Configuration) sampler using a random forest surrogate model and Expected Improvement (EI) acquisition function. Algorithm ported from ML.NET AutoML's SmacTuner, adapted for categorical-only spaces.

```csharp
public sealed class SmacSampler : ISampler
{
    public SmacSampler(
        Dictionary<string, int> parameterCardinalities,
        int numTrees = 10,
        int? numInitialTrials = null,
        int numRandomEISearch = 100,
        int? seed = null);

    public int TrialCount { get; }
    public Dictionary<string, int> Propose();
    public void Update(Dictionary<string, int> config, float score);
}
```

**How it works:**

1. **Initial phase:** Uniform random proposals for `numInitialTrials` trials (default: `max(2 × numParams, 6)`).
2. **Surrogate phase:** Fits a `CategoricalRandomForest` (internal, `numTrees` trees) on observed trials.
3. **Candidate generation:**
   - **Local search:** Starts from the top-k best configurations, iteratively moves to the one-mutation neighbor with highest EI (up to 20 steps).
   - **Random EI search:** Evaluates `numRandomEISearch` random configurations.
4. **Selection:** Returns the candidate with the highest Expected Improvement.
5. **EI formula:** `EI = (mean - bestScore) × Φ(z) + σ × φ(z)` where `z = (mean - bestScore) / σ`, using rational approximation for the normal CDF (Abramowitz & Stegun 7.1.26).

### When to Use Which

| Sampler | Best For | Trade-off |
|---|---|---|
| `CategoricalTpeSampler` | Small-to-medium search spaces (< 50 trials) | Fast, low overhead, good default |
| `SmacSampler` | Larger search spaces, parameter interactions | Higher per-trial cost, better exploitation of structure |

---

## 7. MIPROv2 — Bayesian Instruction + Demo Optimization

Bayesian optimization over both instructions and demo set selection. Implements DSPy's most sophisticated optimizer with three phases.

### API

```csharp
public sealed class MIPROv2 : IOptimizer
{
    public MIPROv2(
        IChatClient proposalClient,
        Func<Dictionary<string, int>, ISampler>? samplerFactory = null,
        int numTrials = 20,
        int numInstructionCandidates = 5,
        int numDemoSubsets = 5,
        int maxDemos = 4,
        float metricThreshold = 1.0f,
        double gamma = 0.25,
        int? seed = null);

    /// <summary>
    /// Trial history from the last CompileAsync call — (configuration, score) per trial.
    /// Useful with TraceAnalyzer for post-optimization analysis.
    /// </summary>
    public IReadOnlyList<TrialResult>? LastTrialHistory { get; }

    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

### Three Phases

**Phase 1 — Bootstrap demos:**
Runs `BootstrapFewShot` with a larger `maxDemos` (= `maxDemos × numDemoSubsets`) to collect a diverse pool of successful demos per predictor. Demos are extracted from the bootstrapped module's predictors.

**Phase 2 — Propose instructions (via LM):**
For each predictor, uses the `proposalClient` to generate `numInstructionCandidates - 1` new instruction variants (the original instruction is always included as candidate 0). The LM sees the predictor name, current instruction, and example demos.

**Phase 3 — Bayesian search:**
Search over a categorical space: *instruction index × demo subset index* per predictor. The search space is keyed as `{predictorName}_instr` and `{predictorName}_demos`.

### Search Space

```
Per predictor (e.g., "_classify"):
  _classify_instr → 0..numInstructionCandidates
  _classify_demos → 0..numDemoSubsets
```

### Implementation Flow

```csharp
public async Task<TModule> CompileAsync<TModule>(...) where TModule : LmpModule
{
    // Split train set 80/20
    var (bootstrapSplit, valSplit) = BootstrapRandomSearch.SplitDataset(trainSet, 0.8, rng);

    // Phase 1: Bootstrap demo pool
    var demoPool = await BootstrapDemoPoolAsync(module, bootstrapSplit, metric, ...);

    // Phase 2: Propose instruction candidates via LM
    var instructionCandidates = await ProposeInstructionsAsync(module, demoPool, ...);

    // Phase 3: Bayesian search
    var sampler = _samplerFactory?.Invoke(cardinalities)
        ?? new CategoricalTpeSampler(cardinalities, _gamma, _seed);

    for (int trial = 0; trial < _numTrials; trial++)
    {
        var config = sampler.Propose();
        var candidate = module.Clone<TModule>();

        // Apply instruction + demo-set selection from config
        foreach (var (name, predictor) in candidate.GetPredictors())
        {
            predictor.Instructions = instructionCandidates[name][config[$"{name}_instr"]];
            predictor.Demos.Clear();
            foreach (var (input, output) in demoSubsets[name][config[$"{name}_demos"]])
                predictor.AddDemo(input, output);
        }

        var result = await Evaluator.EvaluateAsync(candidate, valSplit, metric, ...);
        sampler.Update(config, result.AverageScore);
        trialHistory.Add(new TrialResult(config, result.AverageScore));

        if (result.AverageScore > bestScore) { bestCandidate = candidate; ... }
    }

    _lastTrialHistory = trialHistory;
    return bestCandidate;
}
```

### Key Design Points

| Point | Detail |
|---|---|
| `proposalClient` | Separate from the module's client — can use a cheaper model for instruction generation. |
| `samplerFactory` | Inject `SmacSampler`, custom samplers, or `null` for the default `CategoricalTpeSampler`. |
| `LastTrialHistory` | Exposed for post-optimization analysis with `TraceAnalyzer`. |
| Demo subsets | Created via Fisher-Yates shuffle of the bootstrap pool, taking up to `maxDemos` per subset. |

---

## 8. GEPA — Evolutionary Instruction Optimization

GEPA (Genetic-Pareto Evolutionary Algorithm) evolves **instructions only** (not demos) using LLM-driven reflection. Unlike Bayesian optimizers that search based on scores alone, GEPA captures execution traces, uses an LLM to diagnose failures, and proposes targeted instruction fixes. Maintains a Pareto frontier of non-dominated candidates.

Inspired by [gepa-ai/gepa](https://github.com/gepa-ai/gepa), integrated into DSPy as `dspy.GEPA`.

### API

```csharp
public sealed class GEPA : IOptimizer
{
    public GEPA(
        IChatClient reflectionClient,
        int maxIterations = 50,
        int miniBatchSize = 5,
        int mergeEvery = 5,
        int? seed = null);

    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

### Algorithm

```
1. Seed Pareto frontier with initial module (evaluated on random mini-batch)
2. For iter in 0..maxIterations:
   a. If iter % mergeEvery == 0 and frontier.Count >= 2:
      - Merge: select two parents, crossover instructions
   b. Else:
      - Reflective Mutation:
        i.   Select random parent from frontier
        ii.  Clone parent, run on mini-batch, capture traces
        iii. For each predictor with failures (score < 0.8):
             - Send traces + current instruction to reflection LLM
             - LLM diagnoses failures and proposes improved instruction
   c. Evaluate candidate on random mini-batch
   d. Add to Pareto frontier (removes dominated candidates)
3. Return frontier.Best (highest average score)
```

### Two Operations

**Reflective Mutation:** Run a candidate on a mini-batch → capture execution traces → ask the reflection LLM to analyze failures for each predictor → replace instruction with LLM-proposed improvement. The reflection prompt includes the current instruction, failed examples with their inputs/outputs/scores, and per-predictor trace entries.

**Merge (Crossover):** Select two diverse parents from the Pareto frontier. For each predictor in the child, randomly pick the instruction from one parent (50/50). The `ParetoFrontier.SelectParents()` method prefers diverse parents — picks one at random, then picks the most different one (by per-example score distance using `TensorPrimitives.Subtract/Abs/Sum`).

### ParetoFrontier (Internal)

```csharp
internal sealed class ParetoFrontier<TModule> where TModule : LmpModule
{
    int Count { get; }
    IReadOnlyList<TModule> Frontier { get; }
    TModule Best { get; }
    void Add(TModule candidate, IReadOnlyList<ExampleResult> scores);
    (TModule Parent1, TModule Parent2) SelectParents(Random rng);
}
```

A candidate is non-dominated if no other candidate scores better on **all** examples. The frontier tracks per-example scores to determine dominance. Uses `TensorPrimitives` for efficient distance computation when selecting diverse parents.

### Key Design Points

| Point | Detail |
|---|---|
| Instructions only | GEPA does not optimize demos — only instructions. Combine with BootstrapFewShot for demo optimization. |
| `reflectionClient` | Can be the same model as the module's client, or a cheaper model for cost efficiency. |
| Mini-batch evaluation | Evaluates on random subsets (`miniBatchSize`) each iteration, not the full training set. Reduces cost. |
| Pareto frontier | Maintains non-dominated candidates across all iterations. Automatically prunes dominated candidates. |

---

## 9. TraceAnalyzer — Post-Optimization Analysis

Empirical Bayesian analysis of optimization trial history. Computes per-parameter-value posterior distributions, detects interactions between parameters, and provides warm-start priors for transfer learning. Zero external dependencies — uses frequentist statistics on observed trial data.

### Supporting Types

```csharp
/// <summary>Result of a single optimization trial.</summary>
public sealed record TrialResult(Dictionary<string, int> Config, float Score);

/// <summary>Posterior statistics for a single parameter value.</summary>
public sealed record ParameterPosterior(double Mean, double StandardError, int Count);
```

### API

```csharp
public static class TraceAnalyzer
{
    /// <summary>
    /// Computes posterior distributions (mean ± standard error) for each parameter value.
    /// A high mean with low standard error indicates a confident winner.
    /// </summary>
    static Dictionary<string, Dictionary<int, ParameterPosterior>> ComputePosteriors(
        IReadOnlyList<TrialResult> trials,
        Dictionary<string, int> cardinalities);

    /// <summary>
    /// Detects interaction effects between parameter pairs using ANOVA-style residual analysis.
    /// High interaction strength means combined effect differs from sum of individual effects.
    /// </summary>
    static Dictionary<(string, string), double> DetectInteractions(
        IReadOnlyList<TrialResult> trials);

    /// <summary>
    /// Warm-starts an ISampler by generating synthetic trials from posteriors.
    /// Transfers knowledge from a prior optimization run to a new sampler.
    /// </summary>
    static void WarmStart(
        ISampler sampler,
        Dictionary<string, Dictionary<int, ParameterPosterior>> posteriors,
        int numSyntheticTrials = 3);
}
```

### Warm-Start Loop

The typical warm-start workflow: optimize a module, analyze the trial history, then use the posteriors to warm-start a new sampler for a related task.

```csharp
// 1. Run initial optimization
var mipro = new MIPROv2(proposalClient, numTrials: 20);
var optimized = await mipro.CompileAsync(module, trainSet, metric);

// 2. Analyze trial history
var posteriors = TraceAnalyzer.ComputePosteriors(mipro.LastTrialHistory!, cardinalities);
var interactions = TraceAnalyzer.DetectInteractions(mipro.LastTrialHistory!);

// 3. Warm-start a new sampler for a related task
var newSampler = new CategoricalTpeSampler(newCardinalities);
TraceAnalyzer.WarmStart(newSampler, posteriors, numSyntheticTrials: 3);

// 4. Use the warm-started sampler in a new optimization
var mipro2 = new MIPROv2(proposalClient, samplerFactory: _ => newSampler, numTrials: 10);
var reOptimized = await mipro2.CompileAsync(newModule, newTrainSet, metric);
```

### Implementation Notes

- `ComputePosteriors` uses `TensorPrimitives.Average` and `TensorPrimitives.Multiply/Sum` for efficient statistics via `CollectionsMarshal.AsSpan`.
- `DetectInteractions` computes ANOVA-style residual variance: for each parameter pair, measures how much the actual scores deviate from the sum of individual main effects.
- `WarmStart` generates synthetic trials using each parameter value's posterior mean, paired with the best values from other parameters. These synthetic trials bias the sampler toward configurations that worked well previously.

---

## 10. Tracing Infrastructure

Optimizers need to observe what happens inside a module during execution. The tracing system records `(predictor, input, output)` tuples per `ForwardAsync` call.

### Trace Types

```csharp
/// <summary>
/// Records predictor invocations during a ForwardAsync call.
/// Thread-safe: concurrent predictor calls can record simultaneously.
/// </summary>
public sealed class Trace
{
    public IReadOnlyList<TraceEntry> Entries { get; }
    public void Record(string predictorName, object input, object output);
}

/// <summary>A single predictor invocation record.</summary>
public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);
```

### How Tracing Works

1. Optimizer sets `module.Trace = new Trace()` before running on a training example.
2. Each `Predictor.PredictAsync()` call checks `module.Trace`; if non-null, calls `trace.Record(Name, input, output)`.
3. After execution, optimizer reads `module.Trace.Entries` to retrieve recorded traces.
4. Traces from successful examples (metric ≥ threshold) become demos via `predictor.AddDemo()`.

```csharp
// Inside Predictor<TInput, TOutput>.PredictAsync():
var output = await _chatClient.GetResponseAsync<TOutput>(messages, options, ct);

trace?.Record(Name, input!, output);

return output;
```

The `Trace` class is thread-safe (internal lock) to support concurrent predictor calls within a single `ForwardAsync` (e.g., BestOfN, parallel chains). Each training example gets a fresh `Trace` instance to avoid cross-contamination.

---

## 11. Performance Considerations

### TensorPrimitives

All numerical aggregations use `System.Numerics.Tensors.TensorPrimitives` for SIMD-accelerated operations:
- **Evaluator:** `Average`, `Min`, `Max` on score arrays
- **CategoricalTpeSampler:** `Sum` for probability normalization
- **CategoricalRandomForest:** `Average`, `Multiply` for prediction statistics
- **TraceAnalyzer:** `Average`, `Multiply`, `Sum` for posterior computation
- **ParetoFrontier:** `Subtract`, `Abs`, `Sum` for diversity distance

### Parallel Execution — No GIL

.NET's `Parallel.ForEachAsync` and `Task.WhenAll` provide true parallelism. Candidate evaluation in BootstrapRandomSearch and MIPROv2 runs across real OS threads. This is a structural advantage over Python's `asyncio` + GIL.

```csharp
// BootstrapRandomSearch: parallel candidate evaluation via Task.WhenAll
var evaluationTasks = candidates.Select(c =>
    Evaluator.EvaluateAsync(c, valSplit, metric, cancellationToken: cancellationToken));
var results = await Task.WhenAll(evaluationTasks);

```

### Resilience

LM API calls during optimization should be wrapped in a `ResiliencePipeline` (retry + circuit breaker + timeout) via `Microsoft.Extensions.Resilience`. This is middleware on `IChatClient` — the optimizer itself doesn't manage resilience.

```csharp
// Applied at the ChatClient level, not the optimizer level:
var client = new ChatClientBuilder(innerClient)
    .UseRetry(maxRetries: 3)
    .UseRateLimiting(maxConcurrent: 10)
    .Build();
```

### Caching

Identical prompts across trials should hit cache. Use `IChatClient` middleware caching (e.g., `UseDistributedCache()`). The optimizer doesn't manage caching directly.

### Cancellation

Every async method accepts `CancellationToken`. Long-running optimization can be cancelled gracefully.

---

## 12. End-to-End Example

```csharp
// === Types ===
public record TicketInput(
    [Description("The raw ticket text")] string TicketText);

[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1-5")]
    public required int Urgency { get; init; }
}

[LmpSignature("Draft a helpful reply to the customer")]
public partial record DraftReply
{
    [Description("The reply text")]
    public required string ReplyText { get; init; }
}

// === Module ===
public partial class TicketTriageModule : LmpModule
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public override async Task<object> ForwardAsync(object input, CancellationToken ct = default)
    {
        var ticket = (TicketInput)input;
        var classification = await _classify.PredictAsync(ticket, ct);
        return await _draft.PredictAsync(classification, ct);
    }
}

// === Optimize ===
var module = new TicketTriageModule(chatClient);
var trainSet = Example.LoadFromJsonl<TicketInput, ClassifyTicket>("train.jsonl");
var devSet   = Example.LoadFromJsonl<TicketInput, ClassifyTicket>("dev.jsonl");

// Define a typed metric
var metric = Metric.Create((ClassifyTicket predicted, ClassifyTicket expected) =>
    predicted.Category == expected.Category);

// Option A: Bootstrap few-shot demos
var bootstrap = new BootstrapFewShot(maxDemos: 4);
var optimized = await bootstrap.CompileAsync(module, trainSet, metric);

// Option B: Random search for better results
var search = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, seed: 42);
var best = await search.CompileAsync(module, trainSet, metric);

// Option C: MIPROv2 for best results (optimizes instructions + demos)
var mipro = new MIPROv2(proposalClient, numTrials: 20, seed: 42);
var compiled = await mipro.CompileAsync(module, trainSet, metric);

// Option D: GEPA for instruction-only evolution
var gepa = new GEPA(reflectionClient, maxIterations: 50);
var evolved = await gepa.CompileAsync(module, trainSet, metric);

// Evaluate any optimized module
var result = await Evaluator.EvaluateAsync(compiled, devSet, metric);
Console.WriteLine($"Accuracy: {result.AverageScore:P1}");

// Save and load optimized state
await compiled.SaveAsync("triage-v1.json");
var production = new TicketTriageModule(chatClient);
await production.LoadAsync("triage-v1.json");
```

### Post-Optimization Analysis with TraceAnalyzer

```csharp
// After MIPROv2 optimization:
var posteriors = TraceAnalyzer.ComputePosteriors(mipro.LastTrialHistory!, cardinalities);
var interactions = TraceAnalyzer.DetectInteractions(mipro.LastTrialHistory!);

// Which instruction variant won for _classify?
var classifyInstr = posteriors["_classify_instr"];
var bestInstr = classifyInstr.MaxBy(kv => kv.Value.Mean);
Console.WriteLine($"Best instruction: variant {bestInstr.Key} " +
    $"(mean: {bestInstr.Value.Mean:F3} ± {bestInstr.Value.StandardError:F3})");

// Are instruction and demo choices interacting?
foreach (var ((p1, p2), strength) in interactions.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {p1} × {p2}: {strength:F4}");
```

---

## 13. DSPy → LMP Optimizer Mapping

| DSPy | LMP | Status | Notes |
|---|---|---|---|
| `dspy.Evaluate` | `Evaluator.EvaluateAsync()` | ✅ | `Parallel.ForEachAsync` + TensorPrimitives |
| `BootstrapFewShot` (~250 LOC) | `BootstrapFewShot` | ✅ | Teacher traces → student demos, multi-round |
| `BootstrapFewShotWithRandomSearch` | `BootstrapRandomSearch` | ✅ | × N candidates, `Task.WhenAll` eval |
| `MIPROv2` (~35K LOC, Optuna TPE) | `MIPROv2` | ✅ | CategoricalTpeSampler or SmacSampler |
| `dspy.GEPA` | `GEPA` | ✅ | Pareto frontier + LLM reflection |
| Optuna `TPESampler` | `CategoricalTpeSampler` | ✅ | Categorical-only, ~170 LOC |
| SMAC | `SmacSampler` | ✅ | Random forest + EI acquisition |
| Trial analysis | `TraceAnalyzer` | ✅ | Posteriors, interactions, warm-start |
| `dspy.Example` | `Example<TInput, TLabel>` | ✅ | Typed with `WithInputs()` / `GetLabel()` |
| Metric function | `Metric.Create<TPredicted, TExpected>()` | ✅ | Typed factory + async variants |

---

## 14. Extension Points

| Extension | Notes |
|---|---|
| **LMP.Extensions.Z3** | Z3 constraint-based demo selection. Already implemented as `Z3ConstrainedDemoSelector`. |
| **LMP.Extensions.Evaluation** | Bridge for `Microsoft.Extensions.AI.Evaluation` evaluators as metric functions. |
| **Custom ISampler** | Implement `ISampler` for custom search strategies (e.g., Hyperband, population-based). |
| **CostAwareSampler** | Budget-aware optimization — planned for Phase 9B. |
| **`dotnet lmp optimize` CLI** | CLI wrapper around `IOptimizer.CompileAsync()` for CI/CD pipelines. |
| **LMP.Aspire.Hosting** | Aspire integration with telemetry for optimization runs. Already implemented. |

---

## 15. What's Intentionally Excluded

| Dropped | Why |
|---|---|
| `IOptimizerBackend` interface | Replaced by concrete optimizer classes that each implement `IOptimizer`. |
| `CompileSpec` fluent builder | Optimizers take `(module, trainSet, metric)` directly. No spec object needed. |
| `CompileReport` | Evaluator returns `EvaluationResult`. Optimizer returns a module. No compile report artifact. |
| `VariantDescriptor` / search space IR | No intermediate representation. Optimizers operate on modules directly. |
| Custom trial runners | `Parallel.ForEachAsync` is the trial runner. No abstraction needed. |
| Weighted multi-metric aggregation | Single `Func<Example, object, float>` metric. Compose multiple metrics in user code if needed. |
| Model / temperature / topK tuning | Configuration concerns — handled by `IChatClient` middleware, not the optimizer. |
