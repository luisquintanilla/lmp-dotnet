# Compiler / Optimizer Specification

> **Spec source:** `spec.org` §10A — Optimization and Constraint Architecture
> **Status:** MVP Implementation Reference
> **Audience:** Implementers — this document is designed to be self-contained enough for a junior developer to build the compiler from scratch.

---

## 1. What "Compiling" an LM Program Means

An LM program starts life with defaults — a hand-written instruction, no few-shot examples, a single model, and a guessed temperature. **Compiling** that program means automatically discovering the configuration that produces the best results under your rules.

**Analogy:** Just as the C# compiler finds the best IL representation for your source code, the LMP compiler finds the best *prompt configuration* for your LM program. The source code stays the same; only the runtime parameters change.

### The Compile Loop

```
try many variants → score each → enforce constraints → pick the best
```

That's the entire idea. The compiler:

1. Defines a **search space** of tunable parameters.
2. Asks an optimizer backend to **propose candidate variants**.
3. **Executes** each variant against training/validation data.
4. **Scores** the results using weighted metrics.
5. **Rejects** any variant that violates hard constraints.
6. **Selects** the highest-scoring valid variant.
7. **Emits** a compiled artifact — a deployable, versioned configuration.

### What Gets Optimized

| Tunable | Example Values | IR Kind |
|---|---|---|
| Instructions | variant phrasings for a predict step | `Instruction` |
| Few-shot examples | 0–6 demos, different selections | `FewShotCount`, `FewShotSelection` |
| Model choice | `gpt-4.1-mini`, `gpt-4.1` | `Model` |
| Temperature | 0.0 – 0.7 | `Temperature` |
| Retrieval topK | 3 – 10 documents | `RetrievalTopK` |

> **Why This Is Different From DSPy:** DSPy optimizes instructions and few-shot examples. Model choice, temperature, and retrieval topK as first-class programmatic tunables are .NET-native extensions. The constraint model is entirely original — DSPy has no constraint system.

---

## 2. Compiler Architecture

### Ownership Boundary

The compiler and the optimizer backend have a strict ownership separation:

| Compiler Owns | Backend Owns |
|---|---|
| Search-space construction | Candidate proposal |
| Objective definition | *(nothing else)* |
| Constraint enforcement | |
| Trial execution orchestration | |
| Selection of best valid variant | |
| Compile report generation | |
| Artifact emission | |

This separation is **mandatory**. The backend is a pluggable proposal engine. The compiler is the orchestrator.

### Core Types

```csharp
public interface IProgramCompiler
{
    Task<CompileReport> CompileAsync(
        CompileSpec spec,
        CancellationToken cancellationToken = default);
}

public interface IOptimizerBackend
{
    ValueTask<CandidateProposal> ProposeNextAsync(
        SearchSpaceDescriptor searchSpace,
        ObjectiveDescriptor objective,
        IReadOnlyList<ConstraintDescriptor> constraints,
        IReadOnlyList<TrialResultDescriptor> priorTrials,
        CancellationToken cancellationToken = default);
}
```

### Compile Loop State Machine

```
┌─────────────────────────────────────────────────────┐
│                  ProgramCompiler                    │
│                                                     │
│  ┌───────────┐                                      │
│  │  Extract   │  Build SearchSpaceDescriptor from    │
│  │  Search    │  CompileSpec.Optimize(...) block     │
│  │  Space     │                                      │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐    ┌──────────────────┐              │
│  │  Propose   │◄──│ IOptimizerBackend │              │
│  │  Candidate │    └──────────────────┘              │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐                                      │
│  │  Apply     │  program with { Temp = 0.3,          │
│  │  Variant   │    Model = "gpt-4.1-mini", ... }     │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐                                      │
│  │  Execute   │  Run on training/validation data     │
│  │  Trial     │  Collect traces & metrics            │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐                                      │
│  │  Score     │  Weighted metric aggregation          │
│  │  Results   │                                      │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐                                      │
│  │  Check     │  Hard: reject if violated            │
│  │ Constraints│  Soft: record preference             │
│  └─────┬─────┘                                      │
│        │                                             │
│        ▼                                             │
│  ┌───────────┐        ┌──────────┐                  │
│  │  Update    │──yes──▶│ Best     │                  │
│  │  Best?     │        │ Variant  │                  │
│  └─────┬─────┘        └──────────┘                  │
│        │ no / continue                               │
│        ▼                                             │
│  ┌───────────┐                                      │
│  │  Budget    │──exhausted──▶ Emit Report + Artifact │
│  │  Check     │                                      │
│  └─────┬─────┘                                      │
│        │ remaining                                   │
│        └──────────▶ (back to Propose)                │
└─────────────────────────────────────────────────────┘
```

The loop runs until the **trial budget** is exhausted (maximum number of trials, or optionally maximum elapsed time).

### Selection Semantics (Mandatory Order)

1. Reject candidates that violate hard constraints.
2. Rank remaining candidates by objective score. Weighted metric aggregation uses `System.Numerics.Tensors.TensorPrimitives` for SIMD-accelerated `Sum`, `Average`, and `CosineSimilarity` operations — critical when scoring thousands of trials over high-dimensional metric vectors.
3. Break ties deterministically: lower cost → lower latency → lexicographically smaller variant ID.
4. Select the best valid candidate.
5. If no valid candidate exists, emit a **failure report** and no approved artifact.

---

## 3. CompileSpec — The Compile Configuration

`CompileSpec` is the user-facing configuration object that drives a compile run. It uses a fluent builder API.

### Complete Fluent API

```csharp
var compileSpec = CompileSpec
    .For<SupportTriageProgram>()

    // --- Data ---
    .WithTrainingSet("data/support-triage-train.jsonl")
    .WithValidationSet("data/support-triage-val.jsonl")

    // --- Tunables ---
    .Optimize(search =>
    {
        search.Instructions(step: "triage");
        search.FewShotExamples(step: "triage", min: 0, max: 6);
        search.RetrievalTopK(step: "retrieve-kb", min: 3, max: 10);
        search.RetrievalTopK(step: "retrieve-policy", min: 2, max: 6);
        search.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]);
        search.Temperature(step: "triage", min: 0.0, max: 0.7);
    })

    // --- Objective ---
    .ScoreWith(Metrics.Weighted(
        ("routing_accuracy", 0.35),
        ("severity_accuracy", 0.25),
        ("groundedness", 0.20),
        ("policy_pass_rate", 0.20)))

    // --- Constraints ---
    .Constrain(rules =>
    {
        rules.Require("policy_pass_rate == 1.0");       // hard
        rules.Require("p95_latency_ms <= 2500");         // hard
        rules.Require("avg_cost_usd <= 0.03");           // hard
        rules.Prefer("avg_latency_ms <= 1500");          // soft
    })

    // --- Optimizer Backend ---
    .UseOptimizer(Optimizers.RandomSearch());
```

### Method Reference

| Method | Purpose |
|---|---|
| `For<TProgram>()` | Binds spec to a program type |
| `WithTrainingSet(path)` | JSONL file for trial execution |
| `WithValidationSet(path)` | JSONL file for final validation of best variant |
| `Optimize(Action<SearchBuilder>)` | Defines the search space via tunable declarations |
| `ScoreWith(ObjectiveDescriptor)` | Weighted metric aggregation for ranking |
| `Constrain(Action<ConstraintBuilder>)` | Hard and soft constraints |
| `UseOptimizer(IOptimizerBackend)` | Pluggable backend for candidate proposal |

### Tunable Types

| Search Builder Method | IR `ParameterKind` | Value Domain |
|---|---|---|
| `Instructions(step)` | `Instruction` | Variant strings (auto-generated or user-supplied) |
| `FewShotExamples(step, min, max)` | `FewShotCount` / `FewShotSelection` | Integer range, subset selection |
| `Model(step, allowed)` | `Model` | Categorical set of model IDs |
| `Temperature(step, min, max)` | `Temperature` | Continuous range `[min, max]` |
| `RetrievalTopK(step, min, max)` | `RetrievalTopK` | Integer range `[min, max]` |

### Constraint Types

| Builder Method | Severity | Behavior |
|---|---|---|
| `rules.Require(expr)` | **Hard** | Variant is **rejected** if violated |
| `rules.Prefer(expr)` | **Soft** | Recorded as preference; may influence tie-breaking |

---

## 4. Variant Generation

### Records + `with` Expressions

The framework uses C# records and `with` expressions to create safe, immutable program variants. The compiler **never mutates** the original program — it creates copies with modified parameters.

```csharp
// The base variant (authored defaults)
var baseVariant = new VariantDescriptor(
    VariantId: "triage-v0",
    ProgramId: "support-triage",
    SelectedParameters: FrozenDictionary<string, object>.Empty);

// A candidate variant with different settings
var candidate = baseVariant with
{
    VariantId: "triage-v42",
    SelectedParameters: new Dictionary<string, object>
    {
        ["triage.Temperature"] = 0.3,
        ["triage.Model"] = "gpt-4.1-mini",
        ["triage.FewShotCount"] = 4,
        ["retrieve-kb.TopK"] = 7,
        ["retrieve-policy.TopK"] = 3
    }.ToFrozenDictionary()
};
```

**Why records?** The C# compiler guarantees all fields are copied. There is no risk of forgetting a field — unlike Python's `dataclasses.replace()`.

### Search Space Storage

The search space is stored as a `FrozenDictionary<string, TunableParameterDescriptor>` — immutable, cache-line-optimized, ~3× faster than `Dictionary` for the read-heavy lookup pattern during candidate generation.

### Generating Variants from a Search Space

```csharp
// Conceptual: the optimizer backend samples from the search space
public CandidateProposal SampleRandom(SearchSpaceDescriptor space, Random rng)
{
    var parameters = new Dictionary<string, object>();

    foreach (var tunable in space.Parameters)
    {
        object value = tunable.ParameterKind switch
        {
            ParameterKind.Temperature =>
                rng.NextDouble() * (tunable.MaxValue - tunable.MinValue) + tunable.MinValue,

            ParameterKind.RetrievalTopK or ParameterKind.FewShotCount =>
                rng.Next((int)tunable.MinValue, (int)tunable.MaxValue + 1),

            ParameterKind.Model =>
                tunable.AllowedValues[rng.Next(tunable.AllowedValues.Count)],

            ParameterKind.Instruction =>
                tunable.AllowedValues[rng.Next(tunable.AllowedValues.Count)],

            _ => throw new NotSupportedException($"Unknown tunable: {tunable.ParameterKind}")
        };

        parameters[$"{tunable.StepId}.{tunable.Name}"] = value;
    }

    return new CandidateProposal(parameters.ToFrozenDictionary());
}
```

With 2 models × 7 few-shot counts × 8 topK values × 5 topK values × temperature sampling, the combinatorial space easily exceeds thousands of candidates. The optimizer backend samples from this space according to its strategy.

### How Each Tunable Maps to a Record Property

When a candidate is applied to a program, each tunable key maps to a specific modification of the program's runtime configuration:

| Key Pattern | Modification |
|---|---|
| `{stepId}.Temperature` | Sets the temperature for that step's `IChatClient` call |
| `{stepId}.Model` | Selects which keyed `IChatClient` registration to use |
| `{stepId}.FewShotCount` | Controls how many demonstration examples are injected into the prompt |
| `{stepId}.TopK` | Sets the retrieval step's document count |
| `{stepId}.Instruction` | Replaces the step's instruction text in the prompt |

The variant's `SelectedParameters` dictionary is a `FrozenDictionary<string, object>` — once created, it cannot be modified. This guarantees that trial execution cannot accidentally corrupt a candidate's configuration, and that two threads executing the same variant see identical parameters.

---

## 5. Constraint System

> **Why This Is Different From DSPy:** DSPy has **no constraint system**. You optimize a metric and hope for the best. The LMP constraint system lets you say "the answer must always pass policy review" as a hard rule that rejects any variant that violates it, no matter how high its accuracy score. This is a **critical enterprise requirement**.

### Hard Constraints

Hard constraints **must** be satisfied. If a candidate violates any hard constraint, it is invalid and cannot be selected — even if it has the highest score.

```csharp
rules.Require("policy_pass_rate == 1.0");
rules.Require("p95_latency_ms <= 2500");
rules.Require("avg_cost_usd <= 0.03");
```

### Soft Constraints

Soft constraints express preferences. They are recorded but do not reject a candidate. In MVP they may influence tie-breaking; the architecture preserves room for weighted soft-constraint scoring.

```csharp
rules.Prefer("avg_latency_ms <= 1500");
```

### Typed Constraint Model

Constraint strings lower into a typed internal model:

```csharp
public sealed record ConstraintDescriptor(
    string Id,
    string MetricName,
    ConstraintOperator Operator,
    double Threshold,
    ConstraintSeverity Severity,
    ConstraintScope Scope,
    string? Message = null);

public enum ConstraintOperator
{
    Equal,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

public enum ConstraintSeverity { Hard, Soft }

public enum ConstraintScope { Compile, Trial, Program }
```

### CallerArgumentExpression Diagnostics

The `Require` and `Prefer` methods use `[CallerArgumentExpression]` to auto-capture the constraint expression as a string at zero runtime cost:

```csharp
public void Require(
    string expression,
    [CallerArgumentExpression(nameof(expression))] string? sourceText = null)
{
    // sourceText automatically contains the literal text,
    // e.g., "policy_pass_rate == 1.0"
    // Used in compile reports and diagnostics
    _constraints.Add(ParseConstraint(expression, ConstraintSeverity.Hard, sourceText));
}
```

### Constraint Timing

Constraints can be evaluated at two phases:

**Static / Pre-Execution Constraints** — checked before a trial runs:
- Parameter range violations (e.g., temperature outside `[0, 1]`).
- Incompatible parameter combinations (e.g., model X doesn't support temperature > 0).
- Invalid candidate shapes.

MVP support: yes, but narrow in scope. Most validation happens post-execution.

**Runtime / Post-Execution Constraints** — checked after metrics are collected:
- Latency, cost, policy pass rate, groundedness threshold.
- These are the primary constraint category in MVP. Required.

### ConstraintEvaluator Implementation

```csharp
public sealed class ConstraintEvaluator
{
    public ConstraintResult Evaluate(
        ConstraintDescriptor constraint,
        IReadOnlyDictionary<string, double> metrics)
    {
        if (!metrics.TryGetValue(constraint.MetricName, out var actual))
        {
            return new ConstraintResult(
                constraint.Id, Passed: false,
                Message: $"Metric '{constraint.MetricName}' not found in trial results.");
        }

        bool passed = constraint.Operator switch
        {
            ConstraintOperator.Equal              => Math.Abs(actual - constraint.Threshold) < 1e-9,
            ConstraintOperator.LessThan           => actual < constraint.Threshold,
            ConstraintOperator.LessThanOrEqual    => actual <= constraint.Threshold,
            ConstraintOperator.GreaterThan        => actual > constraint.Threshold,
            ConstraintOperator.GreaterThanOrEqual => actual >= constraint.Threshold,
            _ => throw new ArgumentOutOfRangeException()
        };

        return new ConstraintResult(
            constraint.Id,
            Passed: passed,
            Message: passed
                ? null
                : $"Constraint violated: {constraint.MetricName} was {actual}, "
                  + $"required {constraint.Operator} {constraint.Threshold}");
    }

    public bool AllHardConstraintsPassed(
        IReadOnlyList<ConstraintDescriptor> constraints,
        IReadOnlyDictionary<string, double> metrics)
    {
        return constraints
            .Where(c => c.Severity == ConstraintSeverity.Hard)
            .All(c => Evaluate(c, metrics).Passed);
    }
}

public sealed record ConstraintResult(
    string ConstraintId,
    bool Passed,
    string? Message = null);
```

---

## 6. Optimizer Backends

### 6.1 RandomSearch (MVP)

The MVP backend is **intentionally simple**. Its purpose is to validate the compiler architecture — search space extraction, trial execution, scoring, constraint enforcement, selection, and artifact emission — not to prove state-of-the-art search.

```csharp
public sealed class RandomSearchBackend : IOptimizerBackend
{
    private readonly Random _rng;

    public RandomSearchBackend(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public ValueTask<CandidateProposal> ProposeNextAsync(
        SearchSpaceDescriptor searchSpace,
        ObjectiveDescriptor objective,
        IReadOnlyList<ConstraintDescriptor> constraints,
        IReadOnlyList<TrialResultDescriptor> priorTrials,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object>();

        foreach (var tunable in searchSpace.Parameters)
        {
            object value = tunable.ParameterKind switch
            {
                ParameterKind.Temperature =>
                    Math.Round(
                        _rng.NextDouble() * (tunable.MaxValue!.Value - tunable.MinValue!.Value)
                        + tunable.MinValue.Value, 2),

                ParameterKind.FewShotCount or ParameterKind.RetrievalTopK =>
                    _rng.Next((int)tunable.MinValue!.Value, (int)tunable.MaxValue!.Value + 1),

                ParameterKind.Model or ParameterKind.Instruction =>
                    tunable.AllowedValues![_rng.Next(tunable.AllowedValues.Count)],

                _ => throw new NotSupportedException(
                    $"Unsupported tunable kind: {tunable.ParameterKind}")
            };

            parameters[$"{tunable.StepId}.{tunable.Name}"] = value;
        }

        var variantId = $"variant-{priorTrials.Count + 1:D4}";
        var proposal = new CandidateProposal(variantId, parameters.ToFrozenDictionary());

        return ValueTask.FromResult(proposal);
    }
}
```

This backend ignores `priorTrials` — it does not learn from past results. That is the explicit point: the MVP backend is boring so the architecture can be validated first.

The MVP must prove the full pipeline end-to-end:
- Search space extraction works.
- Candidates can be applied to programs.
- Trial execution produces metrics.
- Constraints can invalidate candidates.
- Objective scoring can rank valid candidates.
- One best valid variant can be selected.
- A compiled artifact can be emitted.

Once the architecture is validated, sophisticated backends slot in behind the same `IOptimizerBackend` interface.

### 6.2 Future: AutoML Backend

`Microsoft.ML.AutoML` v0.23.0 provides SMAC-based Bayesian search over numeric, categorical, and ordinal parameters — a genuine fit for hyperparameter optimization.

**Interface contract:** The `IOptimizerBackend` interface remains identical. The AutoML backend would consume `priorTrials` to update its surrogate model and propose informed candidates.

```
Compiler orchestration
  + AutoML proposal engine (SMAC / Bayesian)
  + same constraint enforcement, scoring, selection
```

### 6.3 MVP: Predicate-Based Constraint Pruning

For MVP, static constraint pruning uses **simple lambda predicates** — no external solver dependency. The compiler evaluates candidate feasibility using C# predicates before executing a trial:

```csharp
// MVP: predicate-based feasibility check
Func<CandidateProposal, bool> feasibilityCheck = candidate =>
    candidate.Temperature >= 0.0 && candidate.Temperature <= 1.0 &&
    candidate.TopK >= 1 && candidate.TopK <= 20 &&
    !(candidate.Model == "gpt-4.1-mini" && candidate.Temperature > 0.5);

// Before executing a trial:
if (!feasibilityCheck(candidate))
{
    // Skip trial entirely — no API call
    continue;
}
```

This is fast, zero-dependency, and sufficient for MVP-scale search spaces.

### 6.4 Post-MVP: Z3-Assisted Pruning

Post-MVP, Z3 can replace or augment predicate pruning as a **constraint co-processor** — it prunes infeasible candidates *before* they are executed, saving LM API costs, and can reason about more complex constraint interactions (e.g., model-specific parameter ranges, cross-step constraints).

```
Candidate proposed by backend
  → Z3 checks static feasibility (parameter ranges, incompatible combinations)
  → if infeasible: skip trial entirely (no API call)
  → if feasible: proceed to trial execution
```

**Cost savings:** If 30% of random candidates are statically infeasible, Z3 pruning eliminates 30% of LM API calls — which dominate compile cost.

Z3 should **not** replace the compiler, the runtime, or the search backend. Its role is strictly: feasibility checking, candidate pruning, and optional candidate repair.

---

## 7. CompileReport

The `CompileReport` is the structured output of a compile run. It provides full transparency into what happened.

### CompileReport Type Definition

```csharp
public sealed record CompileReport(
    string ProgramId,
    string? BestVariantId,
    int TrialsExecuted,
    int ValidTrials,
    int RejectedTrials,
    IReadOnlyDictionary<string, double>? BestMetrics,
    IReadOnlyList<TrialResultDescriptor> AllTrials,
    IReadOnlyDictionary<string, int> RejectionsByConstraint,
    BestCandidateSummary? BestValid,
    BestCandidateSummary? BestInvalid,
    TimeSpan ElapsedTime);

public sealed record BestCandidateSummary(
    string VariantId,
    double ObjectiveScore,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<string>? ViolatedConstraints = null);
```

### Canonical Report Format

```
══════════════════════════════════════════════
  LMP Compile Report — support-triage
══════════════════════════════════════════════

Trials executed:   84
Valid trials:      51
Rejected trials:   33

Rejected by:
  policy_pass_rate:  12
  p95_latency_ms:    14
  avg_cost_usd:       7

Best invalid candidate:
  variant:   triage-v12
  score:     0.94
  violated:  p95_latency_ms <= 2500

Best valid candidate:
  variant:   triage-v17
  score:     0.91
  metrics:
    routing_accuracy:   0.93
    severity_accuracy:  0.88
    groundedness:       0.91
    policy_pass_rate:   1.00
  parameters:
    triage.Model:           gpt-4.1-mini
    triage.Temperature:     0.25
    triage.FewShotCount:    4
    retrieve-kb.TopK:       7
    retrieve-policy.TopK:   3

Elapsed: 00:12:34
══════════════════════════════════════════════
```

If no valid candidate exists, the report states this explicitly and no artifact is emitted.

---

## 8. Performance Considerations

### Resilience Pipeline for LM API Calls

The compiler wraps LM API calls in a `ResiliencePipeline` from `Microsoft.Extensions.Resilience` that combines retry (with exponential backoff + jitter), circuit breaker, and timeout. This replaces the previous `TokenBucketRateLimiter` approach with a composable, production-grade resilience strategy.

```csharp
services.AddResiliencePipeline("compiler-lm-calls", builder =>
{
    builder
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(15)
        })
        .AddTimeout(TimeSpan.FromSeconds(90));
});

// Before each LM API call within a trial:
var pipeline = provider
    .GetRequiredService<ResiliencePipelineProvider<string>>()
    .GetPipeline("compiler-lm-calls");

var response = await pipeline.ExecuteAsync(
    async ct => await chatClient.GetResponseAsync(messages, options, ct),
    cancellationToken);
```

> **Why ResiliencePipeline over TokenBucketRateLimiter?** Rate limiting alone doesn't handle transient failures or cascading outages. The `ResiliencePipeline` composes retry, circuit breaking, and timeout in a single declarative builder, matching the same pattern used by the runtime (§8.4 in runtime-execution.md). Provider-side rate limiting (HTTP 429) is handled by the retry strategy's backoff; client-side concurrency is governed by `MaxDegreeOfParallelism` on the Dataflow blocks.

### Response Deduplication with HybridCache

`HybridCache` (.NET 10) provides combined L1 (in-memory) + L2 (distributed) caching. If two trials send the identical prompt to the same model, the second trial gets a cached response — no duplicate API call.

**Tag-based eviction:** Cache entries are tagged by trial ID and step name using `HybridCacheEntryOptions.Tags`. This enables targeted eviction — e.g., evict all cached responses for a specific trial without flushing the entire cache. When a trial is rejected by constraints, its cached responses can be evicted by tag to free memory.

```csharp
var tags = new[] { $"trial:{trialId}", $"step:{stepName}" };
var response = await cache.GetOrCreateAsync(
    key: $"{model}:{promptHash}",
    factory: async ct => await chatClient.CompleteAsync(prompt, ct),
    options: new HybridCacheEntryOptions { Tags = tags },
    cancellationToken: ct);

// Evict all cache entries for a specific trial
await cache.RemoveByTagAsync($"trial:{trialId}", cancellationToken);
```

### Parallel Trial Execution

Trials with no dependencies can run concurrently using `Parallel.ForEachAsync` (.NET 6+). .NET has no GIL — both LM API calls and CPU-bound scoring run on separate threads with true parallelism. This is a significant advantage over Python-based frameworks where `asyncio` is constrained by the GIL for CPU-bound work between API calls.

The degree of parallelism is controlled by `MaxDegreeOfParallelism`, working in concert with the resilience pipeline's concurrency limiter for API safety:

```csharp
// Execute batch of trials with controlled concurrency
var results = new ConcurrentBag<TrialResult>();
await Parallel.ForEachAsync(
    candidates,
    new ParallelOptions
    {
        MaxDegreeOfParallelism = Math.Min(candidates.Count, Environment.ProcessorCount * 2),
        CancellationToken = cancellationToken
    },
    async (candidate, ct) =>
    {
        var result = await ExecuteTrialAsync(candidate, data, ct);
        results.Add(result);
    });
```

### Cancellation

Every async method in the compile pipeline accepts `CancellationToken`. Long-running compiles can be cancelled gracefully via Ctrl+C or programmatic cancellation.

### BackgroundService for Long-Running Compiles

Production compile jobs run as a `BackgroundService` with proper lifecycle management:

```csharp
public sealed class CompileHostedService : BackgroundService
{
    private readonly IProgramCompiler _compiler;
    private readonly CompileSpec _spec;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var report = await _compiler.CompileAsync(_spec, stoppingToken);
        // emit artifact, log report
    }
}
```

---

## 9. End-to-End Compile Example

```csharp
using LMP.Compilation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 1. Define the compile spec
var spec = CompileSpec
    .For<SupportTriageProgram>()
    .WithTrainingSet("data/support-triage-train.jsonl")
    .WithValidationSet("data/support-triage-val.jsonl")
    .Optimize(search =>
    {
        search.Instructions(step: "triage");
        search.FewShotExamples(step: "triage", min: 0, max: 6);
        search.RetrievalTopK(step: "retrieve-kb", min: 3, max: 10);
        search.RetrievalTopK(step: "retrieve-policy", min: 2, max: 6);
        search.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]);
        search.Temperature(step: "triage", min: 0.0, max: 0.7);
    })
    .ScoreWith(Metrics.Weighted(
        ("routing_accuracy", 0.35),
        ("severity_accuracy", 0.25),
        ("groundedness", 0.20),
        ("policy_pass_rate", 0.20)))
    .Constrain(rules =>
    {
        rules.Require("policy_pass_rate == 1.0");
        rules.Require("p95_latency_ms <= 2500");
        rules.Require("avg_cost_usd <= 0.03");
    })
    .UseOptimizer(Optimizers.RandomSearch(seed: 42));

// 2. Run the compiler
var compiler = serviceProvider.GetRequiredService<IProgramCompiler>();
var report = await compiler.CompileAsync(spec, cancellationToken);

// 3. Inspect the report
Console.WriteLine($"Trials: {report.TrialsExecuted}");
Console.WriteLine($"Valid:  {report.ValidTrials}");
Console.WriteLine($"Best:   {report.BestVariantId ?? "NONE"}");

if (report.BestValid is { } best)
{
    Console.WriteLine($"Score:  {best.ObjectiveScore:F4}");
    foreach (var (metric, value) in best.Metrics)
        Console.WriteLine($"  {metric}: {value:F4}");
}
else
{
    Console.WriteLine("ERROR: No valid candidate found. Review constraints.");
}

// 4. Save the compiled artifact
if (report.BestVariantId is not null)
{
    var artifactPath = $"artifacts/{report.ProgramId}-{report.BestVariantId}.json";
    await ArtifactSerializer.SaveAsync(report, artifactPath);
    Console.WriteLine($"Artifact saved: {artifactPath}");
}
```

### Expected Output

```
[compile] Starting compilation for support-triage
[compile] Search space: 6 tunables, ~2800 combinations
[compile] Backend: RandomSearch (seed=42)
[compile] Budget: 84 trials

[trial  1/84] variant-0001  score=0.72  REJECTED (policy_pass_rate=0.85)
[trial  2/84] variant-0002  score=0.68  REJECTED (avg_cost_usd=0.041)
[trial  3/84] variant-0003  score=0.81  VALID    ★ new best
[trial  4/84] variant-0004  score=0.79  VALID
...
[trial 42/84] variant-0042  score=0.91  VALID    ★ new best
...
[trial 71/84] variant-0071  score=0.94  REJECTED (p95_latency_ms=2710)
...
[trial 84/84] variant-0084  score=0.85  VALID

══════════════════════════════════════════════
  LMP Compile Report — support-triage
══════════════════════════════════════════════
Trials executed:   84
Valid trials:      51
Rejected trials:   33

Best valid candidate:
  variant:   variant-0042
  score:     0.91

Artifact saved: artifacts/support-triage-variant-0042.json
```

> **Why This Is Different From DSPy:** DSPy's `compile()` returns a modified `Module` object in memory. LMP emits a versioned, serializable, AOT-safe artifact with provenance metadata, constraint validation results, and hot-swap support via `AssemblyLoadContext`. The compile report provides enterprise-grade explainability — every rejected candidate is accounted for, with the specific constraint that caused rejection.
