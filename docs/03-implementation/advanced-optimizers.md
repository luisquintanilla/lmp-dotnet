# Advanced Optimizers — ISampler, SmacSampler, TraceAnalyzer, and Roadmap

LMP's optimizer layer is designed around two extension points: `IOptimizer` (what to optimize) and `ISampler` (how to search). This document covers the built-in implementations, the sampling abstraction, and the roadmap for Z3 and GEPA.

## Built-In Optimizers

### BootstrapRandomSearch

Random-search optimizer that samples demo subsets from bootstrap traces and picks the best-scoring combination.

```
IOptimizer interface
  └─ CompileAsync<TModule>(module, trainSet, metric) → TModule
```

**Key parameters:** `numTrials`, `maxDemos`, `metricThreshold`, `seed`

**Behavior:** Generates `numTrials` random demo subsets (each ≤ `maxDemos` size), evaluates each on the training set, and returns the module configured with the highest-scoring demos. Simple, embarrassingly parallel, and a strong baseline.

### MIPROv2 (Bayesian Instruction + Demo Optimization)

The most sophisticated optimizer. Tunes both **instructions** and **demos** using Bayesian optimization.

**3-Phase Algorithm:**

1. **Bootstrap Demo Pool** — Runs the module on training examples to collect traces. Successful traces (above `metricThreshold`) become the demo candidate pool.

2. **Propose Instructions** — Uses a separate LLM call (the `proposalClient`) to generate `numInstructionCandidates` instruction variants per predictor, informed by the task description and demo examples.

3. **Bayesian Search** — Uses an `ISampler` (default: `CategoricalTpeSampler`) to jointly search over instruction variants × demo subsets. Runs `numTrials` iterations, selecting the configuration with the highest metric score.

**Key parameters:** `proposalClient`, `numTrials`, `numInstructionCandidates`, `numDemoSubsets`, `maxDemos`, `metricThreshold`, `gamma`, `seed`, `samplerFactory`

**Trial History:** After calling `CompileAsync`, access `mipro.LastTrialHistory` to get the full `IReadOnlyList<TrialResult>` of (configuration, score) pairs. This enables post-optimization analysis with `TraceAnalyzer`.

## The `ISampler` Abstraction

```csharp
public interface ISampler
{
    int TrialCount { get; }
    Dictionary<string, int> Propose();
    void Update(Dictionary<string, int> config, float score);
}
```

`ISampler` defines how the Bayesian search explores the configuration space. The contract mirrors ML.NET's `ITuner` pattern:

- **`Propose()`** — Returns the next configuration to evaluate. Keys are parameter names (e.g., `"classify_instr"`), values are categorical indices.
- **`Update()`** — Reports the score for a previously proposed configuration. The sampler uses this feedback to guide future proposals.
- **`TrialCount`** — Number of completed trials.

Search space configuration (cardinalities per parameter) is handled by the constructor, not the interface. This keeps the contract minimal.

### Injecting a Custom Sampler into MIPROv2

MIPROv2 accepts an optional sampler factory. The factory receives the cardinalities dictionary (built at optimization time from the actual instruction/demo candidates) and returns an `ISampler`:

```csharp
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 20,
    samplerFactory: cardinalities => new SmacSampler(cardinalities, numTrees: 10, seed: 42));
```

If no factory is provided, MIPROv2 defaults to `CategoricalTpeSampler`.

### CategoricalTpeSampler (Default)

Tree-structured Parzen Estimator adapted for categorical parameters. Splits observations into "good" (top γ%) and "bad" groups, then proposes configurations that are more likely under the "good" distribution.

```csharp
var sampler = new CategoricalTpeSampler(cardinalities, gamma: 0.25, seed: 42);
```

**When to use:** Good default. Fast, handles independent parameters well. Best when parameters don't interact strongly.

### SmacSampler (SMAC / Random Forest)

Sequential Model-based Algorithm Configuration. Uses a random forest surrogate model to predict scores, then selects the configuration with the highest Expected Improvement.

```csharp
var sampler = new SmacSampler(cardinalities, numTrees: 10, seed: 42);
```

**Algorithm:**
1. **Random init phase:** First `max(2 × numParams, 6)` trials use uniform random sampling.
2. **Surrogate phase:** Fits a `CategoricalRandomForest` to all observed trials.
3. **EI acquisition:** Evaluates Expected Improvement: `EI = (bestScore - μ) × Φ(z) + σ × φ(z)`.
4. **Local search:** One-mutation neighborhood (cycle to next category per dimension). Max 20 iterations.
5. **Random EI search:** 100 random configurations evaluated for EI.
6. Returns the candidate with the highest EI.

**When to use:** Better than TPE when parameters interact (e.g., certain instructions work better with certain demo sets). The random forest naturally captures these joint effects. Slightly higher per-trial cost due to forest fitting.

**Implementation:** Zero dependencies. Includes an internal `CategoricalRandomForest` (~170 LOC) — bootstrap samples, variance-reduction splits, 10 trees.

## TraceAnalyzer

Empirical analysis of optimization trial history. Zero dependencies. Three capabilities:

### Parameter Posteriors

For each parameter value, compute the mean score ± standard error across all trials that used that value:

```csharp
var history = mipro.LastTrialHistory;
var cardinalities = new Dictionary<string, int>
{
    ["classify_instr"] = 4, ["classify_demos"] = 4,
    ["draft_instr"] = 4, ["draft_demos"] = 4
};

var posteriors = TraceAnalyzer.ComputePosteriors(history, cardinalities);

foreach (var (param, values) in posteriors)
{
    foreach (var (valueIdx, post) in values.OrderByDescending(kv => kv.Value.Mean))
        Console.WriteLine($"  {param}[{valueIdx}]: {post.Mean:F3} ± {post.StandardError:F3} (n={post.Count})");
}
```

**Use case:** Identify which instruction variants or demo subsets consistently score well. High mean + low stderr = high confidence.

### Interaction Detection

ANOVA-style residual analysis that detects parameter pairs with synergy or conflict:

```csharp
var interactions = TraceAnalyzer.DetectInteractions(history);

foreach (var ((p1, p2), strength) in interactions.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {p1} × {p2} = {strength:F4}");
```

High interaction strength means the score depends on the *combination* of those two parameters, not just their individual effects. This can guide the choice of sampler (SmacSampler handles interactions better than TPE).

### Warm-Start Transfer Learning

Convert posteriors from a previous optimization run into synthetic trials that seed a new sampler:

```csharp
// After first optimization
var posteriors = TraceAnalyzer.ComputePosteriors(history, cardinalities);

// Start a new optimization with transferred knowledge
var warmSampler = new SmacSampler(cardinalities, seed: 123);
TraceAnalyzer.WarmStart(warmSampler, posteriors, numSyntheticTrials: 5);

var mipro2 = new MIPROv2(proposalClient: client, numTrials: 10,
    samplerFactory: _ => warmSampler);
```

The warm-started sampler begins with knowledge of which parameter values tend to score well, rather than exploring from scratch. This is valuable when:
- Re-optimizing after changing training data slightly
- Transferring knowledge from a smaller pilot study
- Adapting an optimized module to a related task

## The `IOptimizer` Extension Point

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

Any new optimizer implements this single method. The contract:

- **Input:** An unoptimized module, training data, and a scoring function
- **Output:** The same module type, now configured with optimized learnable parameters (demos, instructions)
- **Constraint:** Must preserve the module's `ForwardAsync` signature and type contracts

### Accessing Learnable Parameters

Optimizers read and write module state through `LmpModule`:

```csharp
module.GetPredictors()  // → IReadOnlyList<(string Name, IPredictor Predictor)>
predictor.Demos          // read/write demo collection
predictor.Instructions   // read/write instruction text
```

## M.E.AI.Evaluation Integration

LMP includes `LMP.Extensions.Evaluation`, a bridge adapter that connects Microsoft.Extensions.AI.Evaluation evaluators to LMP's metric system. This enables using production-grade evaluators (Coherence, Relevance, Groundedness) as LMP optimization metrics.

See `samples/LMP.Samples.Evaluation` for a complete demo.

## Z3 Constraint-Based Demo Selection

**Status: ✅ Shipped** — `LMP.Extensions.Z3` package.

Z3 enables constraint-based demo selection: "Select exactly N demos, cover all categories, minimize tokens, maximize quality." Uses `Microsoft.Z3` (MIT, ~8 MB).

```csharp
var z3 = new Z3ConstrainedDemoSelector(
    categoryExtractor: demo => ExtractCategory(demo),
    maxDemos: 4,
    metricThreshold: 1.0f);
var optimized = await z3.CompileAsync(module, trainSet, metric);
```

## GEPA (Evolutionary Reflection-Driven Optimization)

**Status: ✅ Shipped** — In `LMP.Optimizers`.

GEPA represents a fundamentally different optimization paradigm:

- **MIPROv2:** "Config scored 0.45. Based on score patterns, try this next config." (Bayesian)
- **GEPA:** "Config scored 0.45. The classify predictor output 'urgent' when the input was clearly routine. Propose: 'Be more conservative — only classify as urgent when the customer mentions safety or legal issues.'" (Evolutionary + Reflection)

GEPA evolves **instructions only** using LLM-based reflection on execution traces. It captures full `Trace`/`TraceEntry` data (LMP's existing trace system IS what GEPA calls "Actionable Side Information"), sends failures to a reflection LLM for diagnosis, and proposes targeted instruction fixes per predictor.

### GEPA Algorithm

```
1. For each iteration:
   a. Evaluate candidate module on a mini-batch
   b. For each predictor with failures:
      - Read actual (input, output) from Trace entries
      - Ask reflection LLM: "Why did this fail? Propose a better instruction."
   c. Create mutated candidate with new instructions
   d. Gate: only accept mutation if mini-batch score > parent
   e. Add accepted candidate to Pareto frontier
   f. Every N iterations: merge two Pareto-optimal parents (crossover)
2. Return module with highest score from Pareto frontier
```

### Progress Reporting

GEPA emits progress via `IProgress<GEPAProgressReport>` — wire a callback to monitor optimization:

```csharp
var gepa = new GEPA(
    reflectionClient: client,
    maxIterations: 30,
    miniBatchSize: 5,
    mergeEvery: 5,
    seed: 42,
    progress: new Progress<GEPAProgressReport>(r =>
    {
        string status = r.IterationType == GEPAIterationType.Merge ? "MERGE"
            : r.Passed == true ? "PASS " : "skip ";
        Console.WriteLine($"  iter {r.Iteration,2}/{r.TotalIterations} [{status}]  frontier={r.FrontierSize,2}  best={r.BestScore:P1}");
    }));

var optimized = await gepa.CompileAsync(module, trainSet, metric);
```

**`GEPAProgressReport`** record fields:

| Field | Type | Meaning |
|---|---|---|
| `Iteration` | `int` | Current iteration number (1-based) |
| `TotalIterations` | `int` | Total iterations configured |
| `IterationType` | `GEPAIterationType` | `Mutation` or `Merge` |
| `Passed` | `bool?` | `true` = accepted into frontier, `false` = rejected (null for Merge) |
| `FrontierSize` | `int` | Current Pareto frontier size |
| `BestScore` | `float` | Best full dev-set score seen so far |

**Complements MIPROv2:** GEPA for instruction refinement (100-500 evals), then MIPROv2 for joint instruction+demo optimization (20 trials).

## Architecture

```
LMP.Abstractions    ← ISampler interface (public contract)
LMP.Optimizers      ← CategoricalTpeSampler : ISampler  (TPE, default)
                    ← SmacSampler : ISampler             (SMAC/RF)
                    ← TraceAnalyzer                      (posteriors, interactions, warm-start)
                    ← MIPROv2 : IOptimizer               (accepts ISampler factory)
                    ← GEPA : IOptimizer                  (reflection + Pareto frontier)
                    ← GEPAProgressReport                 (IProgress<T> callback data)
LMP.Extensions.Z3  ← Z3ConstrainedDemoSelector           (constraint-based demos)
```

## Summary

| Component | Status | Purpose | Dependencies |
|---|---|---|---|
| ISampler | ✅ Shipped | Sampling abstraction | None (in Abstractions) |
| CategoricalTpeSampler | ✅ Shipped | TPE search | None |
| SmacSampler | ✅ Shipped | SMAC/RF search | None |
| TraceAnalyzer | ✅ Shipped | Post-optimization analysis | None |
| MIPROv2 (history) | ✅ Shipped | Trial history export | None |
| Z3 Constrained | ✅ Shipped | Constraint-based demos | Microsoft.Z3 |
| GEPA | ✅ Shipped | Reflection-driven evolution | IChatClient |
