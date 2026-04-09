# Advanced Optimizers — Extension Points and Roadmap

LMP ships with two optimizers out of the box. This document describes the `IOptimizer` extension model and explores three post-MVP directions: Z3 constraint-based demo selection, Infer.NET probabilistic search, and ML.NET AutoML integration.

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

The most sophisticated optimizer. Tunes both **instructions** and **demos** using Bayesian optimization (TPE).

**3-Phase Algorithm:**

1. **Bootstrap Demo Pool** — Runs the module on training examples to collect traces. Successful traces (above `metricThreshold`) become the demo candidate pool.

2. **Propose Instructions** — Uses a separate LLM call (the `proposalClient`) to generate `numInstructionCandidates` instruction variants per predictor, informed by the task description and demo examples.

3. **Bayesian Search** — Uses `CategoricalTpeSampler` (Tree-structured Parzen Estimator) to jointly search over instruction variants × demo subsets. Runs `numTrials` iterations, selecting the configuration with the highest metric score.

**Key parameters:** `proposalClient`, `numTrials`, `numInstructionCandidates`, `numDemoSubsets`, `maxDemos`, `metricThreshold`, `gamma`, `seed`

**Why it matters:** MIPROv2 is the only optimizer that tunes *instructions*. BootstrapRandomSearch only selects demos. For tasks where prompt wording significantly affects quality, MIPROv2 can find instructions the developer wouldn't have written.

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
// Get all predictors (each has Instructions + Demos)
module.GetPredictors()  // → IReadOnlyList<(string Name, IPredictor Predictor)>

// Set demos on a specific predictor
predictor.Demos = selectedDemos;

// Set instructions (MIPROv2 only today)
predictor.Instructions = optimizedInstructions;
```

### Bootstrap Traces

Both built-in optimizers use `BootstrapAsync` to generate candidate demos:

```csharp
var traces = await Bootstrapper.BootstrapAsync(module, trainSet, metric, maxDemos);
```

This runs the module on each training example, filters by metric threshold, and returns successful input→output traces as `Demo` instances.

## Roadmap: Z3 Constraint-Based Demo Selection

**Readiness: HIGH** — Can be implemented as a standalone `IOptimizer`.

### Motivation

BootstrapRandomSearch selects demos randomly. For structured tasks (e.g., ticket triage with 5 categories × 5 urgency levels), random selection often produces unbalanced demo sets. Z3 can enforce constraints:

- Cover all categories
- Minimize total token count
- Maintain minimum per-category quality scores
- Respect diversity requirements

### Design Sketch

```csharp
public class Z3ConstrainedDemoSelector : IOptimizer
{
    public Task<TModule> CompileAsync<TModule>(...) where TModule : LmpModule
    {
        // 1. Bootstrap demo pool (same as BRS)
        var pool = await Bootstrapper.BootstrapAsync(module, trainSet, metric, maxDemos);

        // 2. Build Z3 model
        var ctx = new Context();
        var solver = ctx.MkOptimize();

        // Boolean variable per demo: selected or not
        var vars = pool.Select((d, i) => ctx.MkBoolConst($"d{i}")).ToArray();

        // Constraint: exactly maxDemos selected
        solver.Add(ctx.MkEq(
            ctx.MkAdd(vars.Select(v => (ArithExpr)ctx.MkITE(v, ctx.MkInt(1), ctx.MkInt(0))).ToArray()),
            ctx.MkInt(maxDemos)));

        // Constraint: at least 1 demo per category
        foreach (var category in categories)
        {
            var categoryVars = pool
                .Select((d, i) => (d, i))
                .Where(x => GetCategory(x.d) == category)
                .Select(x => vars[x.i]);
            solver.Add(ctx.MkOr(categoryVars.ToArray()));
        }

        // Objective: minimize total tokens
        solver.MkMinimize(ctx.MkAdd(
            pool.Select((d, i) => (ArithExpr)ctx.MkITE(vars[i],
                ctx.MkInt(TokenCount(d)), ctx.MkInt(0))).ToArray()));

        // 3. Solve and extract selected demos
        solver.Check();
        // ...assign selected demos to module predictors
    }
}
```

**Dependencies:** `Microsoft.Z3` NuGet package (MIT licensed, ~10 MB).

**Trade-offs:**
- ✅ Guaranteed constraint satisfaction (category coverage, token budgets)
- ✅ Optimal within constraints (minimize tokens, maximize quality)
- ❌ Requires user to define constraints (category extraction, token counting)
- ❌ Demo pool must be pre-generated (still needs bootstrap phase)

## Roadmap: Infer.NET Probabilistic Optimization

**Readiness: HIGH** — Replaces `CategoricalTpeSampler` in MIPROv2 with richer probabilistic models.

### Motivation

MIPROv2's TPE sampler treats instruction and demo choices as independent categoricals. In reality, certain instructions work better with certain demo styles. Infer.NET's factor graphs can model these dependencies.

### Design Sketch

Replace the TPE inner loop of MIPROv2 with an Infer.NET model:

```csharp
// Factor graph: instruction choice → demo subset → expected score
var instructionVar = Variable.Discrete(instructionPrior);
var demoVars = Enumerable.Range(0, numDemoSlots)
    .Select(_ => Variable.Discrete(demoPrior))
    .ToArray();

// Observed scores update the posterior
Variable<double> score = Variable.GaussianFromMeanAndVariance(
    ComputeExpectedScore(instructionVar, demoVars), noiseVariance);

// After each trial, observe the actual score
score.ObservedValue = trialScore;

// Infer posterior → sample next configuration
var engine = new InferenceEngine();
var instrPosterior = engine.Infer<Discrete>(instructionVar);
var nextInstruction = instrPosterior.Sample();
```

**Advantages over TPE:**
- Models instruction-demo synergy (joint distribution)
- Principled uncertainty quantification
- Can incorporate prior knowledge (e.g., "longer instructions tend to work better")

**Dependencies:** `Microsoft.ML.Probabilistic` (MIT licensed).

**Trade-offs:**
- ✅ Richer probabilistic model captures variable interactions
- ✅ Better sample efficiency (fewer trials to find good configs)
- ❌ More complex to configure (priors, noise model)
- ❌ Higher per-trial computational cost

## Roadmap: ML.NET AutoML

**Readiness: LOW** — Significant architectural mismatch.

### Why It's Challenging

ML.NET AutoML is designed for tabular ML pipelines:

```csharp
// ML.NET's SearchSpace is for numeric/categorical hyperparameters
var searchSpace = new SearchSpace<MyHyperParams>();

// SweepableEstimator wraps an ML.NET data pipeline
var pipeline = context.Auto().BinaryClassification(...);
```

LMP's search space is **symbolic** (instructions are strings, demos are structured examples), not numeric hyperparameters. The mapping doesn't naturally fit:

| ML.NET Concept | LMP Equivalent | Fit |
|---|---|---|
| `SearchSpace<T>` | Instruction candidates × demo subsets | Poor — symbolic, not numeric |
| `SweepableEstimator` | Module's `ForwardAsync` | Poor — no data pipeline |
| `AutoMLExperiment` | `IOptimizer.CompileAsync` | Moderate — both iterate trials |
| `TrialResult` | `EvaluationResult` | Good — both report scores |

### If We Did It Anyway

The adapter layer would encode symbolic choices as categorical integers, losing the semantic structure that TPE and Infer.NET can exploit. The effort-to-value ratio is unfavorable compared to Z3 and Infer.NET.

**Recommendation:** Defer ML.NET AutoML integration unless the community identifies specific tabular-ML-adjacent use cases (e.g., optimizing embedding model selection, tuning temperature/top-p numerics).

## M.E.AI.Evaluation Integration

LMP includes `LMP.Extensions.Evaluation`, a bridge adapter that connects Microsoft.Extensions.AI.Evaluation evaluators to LMP's metric system. This enables using production-grade evaluators (Coherence, Relevance, Groundedness) as LMP optimization metrics.

See `samples/LMP.Samples.Evaluation` for a complete demo showing:
- Custom `IEvaluator` bridged into LMP metrics via `EvaluationBridge`
- LLM-as-judge pattern with `CoherenceEvaluator` (documented)
- Combined multi-metric evaluation
- Optimization using bridged M.E.AI metrics

## Summary

| Optimizer | Status | Tunes | Search Method |
|---|---|---|---|
| BootstrapRandomSearch | ✅ Shipped | Demos | Random sampling |
| MIPROv2 | ✅ Shipped | Instructions + Demos | Bayesian TPE |
| Z3 Constrained | 🗺️ Roadmap | Demos (constrained) | SMT solving |
| Infer.NET Probabilistic | 🗺️ Roadmap | Instructions + Demos | Variational inference |
| ML.NET AutoML | ⏸️ Deferred | — | — |
