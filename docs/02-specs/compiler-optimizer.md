# Optimizer Specification

> **Status:** MVP Implementation Reference — aligned with `system-architecture.md` Layer 3
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
| Instructions | `predictor.Instructions` | MIPROv2 (post-MVP) |

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
public override IReadOnlyList<PredictorMetadata> GetPredictors()
    => [
        new("_classify", _classify),
        new("_draft", _draft),
    ];
```

This is how BootstrapFewShot knows which predictors exist and can fill each one's `Demos` collection.

---

## 3. Evaluator

The Evaluator runs a module on a dataset and scores results. It is the foundation — every optimizer uses it internally.

### API

```csharp
public static class Evaluator
{
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

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
        AverageScore: scores.Average(),
        MinScore: scores.Min(),
        MaxScore: scores.Max(),
        Count: scores.Length);
}
```

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

Metrics are simple functions. Two flavors:

**1. Inline lambda — for simple cases:**

```csharp
Func<Example, object, float> accuracy =
    (example, output) => example["category"] == ((ClassifyTicket)output).Category ? 1f : 0f;
```

**2. `M.E.AI.Evaluation` evaluators — for LM-judged quality:**

Don't rebuild evaluators. `Microsoft.Extensions.AI.Evaluation` provides production-quality evaluators: `RelevancyEvaluator`, `TruthfulnessEvaluator`, `CoherenceEvaluator`, `GroundednessEvaluator`, `CompletenessEvaluator`.

```csharp
// Wrap an M.E.AI evaluator as a metric function
var relevance = new RelevancyEvaluator(judgeChatClient);

Func<Example, object, float> metric = async (example, output) =>
{
    var result = await relevance.EvaluateAsync(
        new ChatMessage(ChatRole.User, example["question"]),
        new ChatMessage(ChatRole.Assistant, output.ToString()));
    return result.Score ?? 0f;
};
```

> **Key principle:** LMP wraps M.E.AI evaluators as metric functions. It does not reimplement scoring, rubrics, or LM-as-judge patterns.

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

### Algorithm (from DSPy's `bootstrap.py`, ~250 LOC)

```
1. teacher = Clone(module)                     // deep copy — student's demos are independent
2. for each example in trainSet (via Parallel.ForEachAsync):
   a. Enable tracing on teacher
   b. output = await teacher.ForwardAsync(example.WithInputs())
   c. score  = metric(example, output)
   d. if score >= metricThreshold:
      e. Extract traces: list of (predictor_name, input, output) from this execution
      f. Add to successful_traces[predictor_name]
3. for each predictor in student.GetPredictors():
   a. demos = successful_traces[predictor.Name].Take(maxDemos)
   b. predictor.Demos = demos
4. return student
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
    var teacher = module.Clone<TModule>();
    var successfulTraces = new ConcurrentDictionary<string, ConcurrentBag<Demo>>();

    // Initialize trace bags for each predictor
    foreach (var pred in teacher.GetPredictors())
        successfulTraces[pred.Name] = [];

    // Run teacher on training examples, collect successful traces
    await Parallel.ForEachAsync(
        trainSet,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxConcurrency,
            CancellationToken = cancellationToken
        },
        async (example, ct) =>
        {
            teacher.EnableTracing();
            try
            {
                var output = await teacher.ForwardAsync(example.WithInputs(), ct);
                var score = metric(example, output);

                if (score >= _metricThreshold)
                {
                    // Collect traces from this successful execution
                    foreach (var trace in teacher.CollectTraces())
                    {
                        successfulTraces[trace.PredictorName].Add(
                            new Demo(trace.Input, trace.Output));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Skip failed examples — DSPy does the same */ }
        });

    // Fill student predictors with successful demos
    foreach (var pred in module.GetPredictors())
    {
        if (successfulTraces.TryGetValue(pred.Name, out var traces))
        {
            pred.Instance.Demos = [.. traces.Take(_maxDemos)];
        }
    }

    return module;
}
```

### Key Design Points

| Point | Detail |
|---|---|
| Teacher/student split | Teacher generates traces; student receives demos. Same pattern as DSPy. |
| Tracing | `LmpModule.EnableTracing()` records `(predictor, input, output)` tuples per call. `CollectTraces()` retrieves them. |
| Parallel execution | `Parallel.ForEachAsync` — true parallelism, no GIL. Each training example runs independently. |
| Failed examples | Swallowed (not thrown). DSPy's bootstrap does the same — a few failures in the training set are expected. |
| `Clone()` | Source-generated deep copy of module + all predictors. Uses `with` expressions on records. |

### Demo Type

```csharp
public sealed record Demo(object Input, object Output);
```

Demos are stored per-predictor. When the source-generated `PromptBuilder` assembles `ChatMessage[]`, it includes demos as user/assistant message pairs before the current input.

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
1. Split trainSet into train/validation (e.g., 80/20) OR accept separate valSet
2. candidates = []
3. for i in 1..numTrials:
   a. shuffled = Shuffle(trainSplit)           // different random demo subset each time
   b. candidate = await BootstrapFewShot.CompileAsync(module.Clone(), shuffled, metric)
   c. candidates.Add(candidate)
4. Evaluate all candidates on validation set (via Task.WhenAll):
   a. scores = await Task.WhenAll(candidates.Select(c => Evaluator.EvaluateAsync(c, valSet, metric)))
5. Return candidate with highest average score
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
    var rng = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
    var (trainSplit, valSplit) = SplitDataset(trainSet, trainFraction: 0.8, rng);

    // Bootstrap N candidates with different random demo subsets
    var candidates = new List<TModule>(_numTrials);
    for (int i = 0; i < _numTrials; i++)
    {
        var shuffled = trainSplit.OrderBy(_ => rng.Next()).ToList();
        var bootstrap = new BootstrapFewShot(_maxDemos, metricThreshold: _metricThreshold);
        var candidate = await bootstrap.CompileAsync(
            module.Clone<TModule>(), shuffled, metric, cancellationToken);
        candidates.Add(candidate);
    }

    // Evaluate all candidates on validation set in parallel
    var evaluationTasks = candidates.Select(candidate =>
        Evaluator.EvaluateAsync(candidate, valSplit, metric,
            cancellationToken: cancellationToken));
    var results = await Task.WhenAll(evaluationTasks);

    // Return best performing candidate
    var bestIndex = 0;
    for (int i = 1; i < results.Length; i++)
    {
        if (results[i].AverageScore > results[bestIndex].AverageScore)
            bestIndex = i;
    }

    return candidates[bestIndex];
}
```

### Key Design Points

| Point | Detail |
|---|---|
| Random subsets | Each trial shuffles the training split differently, so each candidate sees different demo examples. |
| `Task.WhenAll` for evaluation | All N candidates evaluated in parallel — true parallelism. |
| No constraint model | Selection is purely by metric score. No Pareto frontiers, no constraint enforcement. |
| Deterministic seeding | Optional `seed` parameter for reproducible results. |

---

## 6. MIPROv2 (Post-MVP)

Bayesian optimization over both instructions and demo set selection. This is DSPy's most sophisticated optimizer.

### How DSPy's MIPROv2 Works

MIPROv2 has three phases:

**Phase 1 — Bootstrap demos:**
Run BootstrapFewShot to collect a pool of successful demos per predictor.

**Phase 2 — Propose instructions (via LM):**
Use an LM to generate N candidate instruction variants for each predictor. The LM sees the task description, field names, and example demos, then proposes diverse instruction phrasings.

**Phase 3 — Bayesian search:**
Search over a categorical space: *instruction index × demo subset index* per predictor. DSPy uses Optuna's `TPESampler` (Tree-structured Parzen Estimator) to propose configurations, evaluate them, and converge on the best.

### .NET Implementation Strategy

```
Phase 1: BootstrapFewShot (already built)
Phase 2: LM-generated instructions via IChatClient
Phase 3: TPE sampler — options:
  a. ML.NET AutoML tuners (Microsoft.ML.AutoML)
  b. Direct TPE implementation (~300 LOC for basic version)
  c. Optuna via Python interop (least desirable)
```

### Search Space

The search space is **categorical** — a discrete grid per predictor:

| Predictor | Dimension | Values |
|---|---|---|
| `_classify` | instruction index | 0..N (proposed instruction variants) |
| `_classify` | demo set index | 0..M (bootstrapped demo subsets) |
| `_draft` | instruction index | 0..N |
| `_draft` | demo set index | 0..M |

The TPE sampler proposes configurations from this grid, evaluates them, and uses the results to propose better configurations.

### Sketch

```csharp
public sealed class MIPROv2 : IOptimizer
{
    public async Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        // Phase 1: Bootstrap a pool of demos
        var demoPool = await BootstrapDemoPool(module, trainSet, metric, cancellationToken);

        // Phase 2: Generate instruction candidates via LM
        var instructionCandidates = await ProposeInstructions(module, demoPool, cancellationToken);

        // Phase 3: Bayesian search over (instruction, demo-set) per predictor
        var (trainSplit, valSplit) = SplitDataset(trainSet, 0.8);
        TModule bestCandidate = module;
        float bestScore = float.MinValue;

        for (int trial = 0; trial < _numTrials; trial++)
        {
            // TPE sampler proposes a configuration
            var config = _sampler.Propose(trial);

            // Apply configuration to module clone
            var candidate = module.Clone<TModule>();
            foreach (var pred in candidate.GetPredictors())
            {
                pred.Instance.Instructions = instructionCandidates[pred.Name][config[pred.Name].InstructionIndex];
                pred.Instance.Demos = demoPool[pred.Name][config[pred.Name].DemoSetIndex];
            }

            // Evaluate
            var result = await Evaluator.EvaluateAsync(candidate, valSplit, metric,
                cancellationToken: cancellationToken);

            // Report to sampler
            _sampler.Report(trial, result.AverageScore);

            if (result.AverageScore > bestScore)
            {
                bestScore = result.AverageScore;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }
}
```

### TPE Implementation Notes

DSPy uses Optuna's `TPESampler` — ~35K LOC of Python. A minimal TPE for categorical-only spaces is ~300 LOC:

1. For each hyperparameter, maintain two distributions: `l(x)` from top-performing trials, `g(x)` from the rest.
2. For categorical parameters, these are simply frequency counts.
3. Propose the value that maximizes `l(x) / g(x)`.
4. Gamma = 0.25 (top 25% are "good" trials) — DSPy's default.

ML.NET's AutoML tuners are a possible backend for production-quality Bayesian search without reimplementing TPE.

---

## 7. Tracing Infrastructure

Optimizers need to observe what happens inside a module during execution. The tracing system records `(predictor, input, output)` tuples.

### Trace Type

```csharp
public sealed record Trace(
    string PredictorName,
    object Input,
    object Output);
```

### How Tracing Works

1. Optimizer calls `module.EnableTracing()` before running on a training example.
2. Each `Predictor.PredictAsync()` call, when tracing is enabled, records a `Trace`.
3. After execution, optimizer calls `module.CollectTraces()` to retrieve recorded traces.
4. Traces from successful examples (metric ≥ threshold) become demos.

```csharp
// Inside Predictor<TInput, TOutput>.PredictAsync():
var output = await _chatClient.GetResponseAsync<TOutput>(messages, options, ct);

if (_tracingEnabled)
{
    _traces.Add(new Trace(Name, input, output));
}

return output;
```

Tracing is thread-local (per-execution) to avoid cross-contamination during parallel bootstrap runs.

---

## 8. Performance Considerations

### Parallel Execution — No GIL

.NET's `Parallel.ForEachAsync` and `Task.WhenAll` provide true parallelism. Both training set bootstrapping and candidate evaluation run across real OS threads. This is a structural advantage over Python's `asyncio` + GIL.

```csharp
// BootstrapFewShot: parallel training example execution
await Parallel.ForEachAsync(trainSet,
    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency },
    async (example, ct) => { /* run teacher, collect traces */ });

// BootstrapRandomSearch: parallel candidate evaluation
var results = await Task.WhenAll(
    candidates.Select(c => Evaluator.EvaluateAsync(c, valSet, metric)));
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

## 9. End-to-End Example

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
public class TicketTriageModule : LmpModule
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
var client = new ChatClientBuilder(new OpenAIChatClient("gpt-4o-mini"))
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

var module = new TicketTriageModule(client);
var trainSet = Example.LoadFromJsonl("train.jsonl");
var devSet   = Example.LoadFromJsonl("dev.jsonl");

// Step 1: Bootstrap few-shot demos
var bootstrap = new BootstrapFewShot(maxDemos: 4);
var optimized = await bootstrap.CompileAsync(module, trainSet,
    metric: (example, output) =>
        example["category"] == ((ClassifyTicket)output).Category ? 1f : 0f);

// Step 2: Evaluate
var result = await Evaluator.EvaluateAsync(optimized, devSet,
    metric: (example, output) =>
        example["category"] == ((ClassifyTicket)output).Category ? 1f : 0f);
Console.WriteLine($"Accuracy: {result.AverageScore:P1}");  // e.g., "Accuracy: 87.0%"

// Step 3: Save optimized state (demos are included)
await optimized.SaveAsync("triage-v1.json");

// Step 4: Load in production
var production = new TicketTriageModule(client);
await production.LoadAsync("triage-v1.json");
```

### Or use BootstrapRandomSearch for better results:

```csharp
var optimizer = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, seed: 42);
var best = await optimizer.CompileAsync(module, trainSet,
    metric: (example, output) =>
        example["category"] == ((ClassifyTicket)output).Category ? 1f : 0f);

var result = await Evaluator.EvaluateAsync(best, devSet,
    metric: (example, output) =>
        example["category"] == ((ClassifyTicket)output).Category ? 1f : 0f);
Console.WriteLine($"Best accuracy: {result.AverageScore:P1}");
```

---

## 10. DSPy → LMP Optimizer Mapping

| DSPy | LMP | Status | Notes |
|---|---|---|---|
| `dspy.Evaluate` | `Evaluator.EvaluateAsync()` | MVP | `Parallel.ForEachAsync` for throughput |
| `BootstrapFewShot` (~250 LOC) | `BootstrapFewShot` | MVP | Teacher traces → student demos |
| `BootstrapFewShotWithRandomSearch` | `BootstrapRandomSearch` | MVP | × N candidates, `Task.WhenAll` eval |
| `MIPROv2` (~35K LOC, Optuna TPE) | `MIPROv2` | Post-MVP | ML.NET AutoML or direct TPE |
| `dspy.Example` | `Example` | MVP | Dictionary-like with `WithInputs()` |
| Metric function | `Func<Example, object, float>` | MVP | Or wrap M.E.AI evaluators |

---

## 11. Post-MVP Extension Points

| Extension | Notes |
|---|---|
| **MIPROv2** | Bayesian search over instructions + demos. ML.NET AutoML tuners or direct TPE (~300 LOC). |
| **ML.NET AutoML** | Backend for categorical Bayesian optimization in MIPROv2. Package: `Microsoft.ML.AutoML`. |
| **Z3 constraint pruning** | Feasibility checking before trial execution — skip infeasible configurations. Post-MVP only. |
| **`dotnet lmp optimize` CLI** | CLI wrapper around `IOptimizer.CompileAsync()` for CI/CD pipelines. Layer 4 tooling. |
| **Aspire dashboard** | Visualization of optimization runs, per-trial metrics, demo selection. |

---

## 12. What's Intentionally Excluded

These concepts appeared in earlier drafts. They are dropped from the optimizer spec:

| Dropped | Why |
|---|---|
| Constraint model (hard/soft constraints) | DSPy has no constraint system. Metric score is sufficient for optimizer selection. |
| Pareto frontiers | Over-engineered for demo/instruction optimization. Single scalar metric works. |
| Z3 integration (MVP) | Post-MVP extension point only. Not needed for BootstrapFewShot or RandomSearch. |
| `IOptimizerBackend` interface | The old "proposal engine" abstraction is replaced by concrete optimizer classes that each implement `IOptimizer`. |
| `CompileSpec` fluent builder | Optimizers take `(module, trainSet, metric)` directly. No spec object needed. |
| `CompileReport` | Evaluator returns `EvaluationResult`. Optimizer returns a module. No compile report artifact. |
| `VariantDescriptor` / search space IR | No intermediate representation. Optimizers operate on modules directly. |
| Custom trial runners | `Parallel.ForEachAsync` is the trial runner. No abstraction needed. |
| Weighted multi-metric aggregation | Single `Func<Example, object, float>` metric. Compose multiple metrics in user code if needed. |
| Model / temperature / topK tuning | Configuration concerns — handled by `IChatClient` middleware, not the optimizer. |
