# Runtime Execution Specification

> **Source of Truth:** `spec.org` §10 (Runtime Responsibilities)
> **Status:** Draft · MVP Scope
> **Audience:** Implementer — a junior developer should be able to build the runtime engine from this document alone.

---

## 1. What the Runtime Does (Plain Language)

The runtime is the part of the framework that **actually runs your AI program**. Everything before this — signatures, source generators, program descriptors — produces a *description* of what the program should do. The runtime takes that description and *does* it.

Concretely, the runtime:

1. **Receives a program graph** (the `ProgramDescriptor` from the IR layer) that declares steps and edges.
2. **Compiles the IR graph to a TPL Dataflow pipeline** (`TransformBlock`, `ActionBlock`, `JoinBlock`) and executes it.
3. **Calls language models** through `IChatClient` (from `Microsoft.Extensions.AI`) for Predict steps.
4. **Calls retrieval providers** through `IDocumentRetriever` for Retrieve steps.
5. **Runs evaluators** through `IEvaluator` for Evaluate steps.
6. **Evaluates branching conditions** for If steps, and **re-executes predictions with feedback** for Repair steps.
7. **Emits OpenTelemetry traces** (`Activity` + `Meter`) for every step so you can debug, monitor, and feed data back into the compiler.
8. **Propagates cancellation** so that `Ctrl+C` or a timeout stops execution cleanly.

> **Why This Matters:** Without a well-defined runtime, your programs are just metadata. The runtime is where cost is incurred, latency is measured, and business value is produced. Every design decision here directly impacts production reliability and observability.

---

## 2. Execution Engine Architecture

### 2.1 Core Components

| Component | Responsibility |
|---|---|
| `ProgramExecutor` | Main orchestrator. Takes a `ProgramDescriptor` + input, returns output + trace. |
| `StepExecutor` | Dispatches to per-kind execution logic (`PredictStepExecutor`, etc.). |
| `ExecutionContext` | Mutable bag that carries step outputs, evaluator scores, and trace data between steps. |
| `StepContext` | Per-step view of the `ExecutionContext` — provides `OutputOf`, `ScoreOf`, `Latest<T>`. |

### 2.2 How the Engine Executes the DAG — TPL Dataflow Compilation

The program graph is a directed acyclic graph (DAG). Edges are typed: `Sequence`, `ConditionalTrue`, `ConditionalFalse`. Rather than walking the graph step-by-step, the `ProgramExecutor` **compiles** the IR into a TPL Dataflow pipeline at the start of each execution:

1. Computes a **topological sort** of all `StepDescriptor` nodes using the `EdgeDescriptor` list.
2. Creates a TPL Dataflow block for each step:
   - **`TransformBlock<StepContext, StepResult>`** — for steps that produce output consumed by downstream steps (Predict, Retrieve).
   - **`ActionBlock<StepContext>`** — for terminal steps with no downstream consumers (Evaluate at the end of a pipeline).
   - **`JoinBlock<T1, T2>`** — for fan-in points where a step receives outputs from multiple upstream steps (e.g., a Predict step consuming both `retrieve-kb` and `retrieve-policy` outputs).
3. Links blocks according to the `EdgeDescriptor` edges, with `DataflowLinkOptions { PropagateCompletion = true }`.
4. For `ConditionalTrue` / `ConditionalFalse` edges, uses a `Predicate<StepResult>` link filter so that `If` steps gate downstream blocks.
5. Posts the program input into the entry block(s) and awaits completion of the terminal block(s).
6. After completion, assembles the final program output from the terminal block results and returns a `RuntimeTraceDescriptor`.

> **Why TPL Dataflow, not a custom graph executor?** TPL Dataflow is a mature, battle-tested .NET library (`System.Threading.Tasks.Dataflow`) that handles buffering, back-pressure, bounded parallelism, and completion propagation out of the box. It maps naturally to the DAG structure of the IR — each step becomes a block, each edge becomes a link. This eliminates the need to write custom scheduling, work-stealing, or cancellation-propagation logic.

#### Binding Resolution at Runtime

Step input bindings are resolved using a four-tier model. The first three tiers are compiled away by the source generator and incur zero runtime overhead:

| Tier | Runtime Behavior |
|------|-----------------|
| **Tier 1** — Convention-based | Generated code: direct property assignment emitted by the Binding Generator. No runtime lookup. |
| **Tier 2** — `[BindFrom]` attribute | Generated code: direct property assignment emitted by the Binding Generator from the attribute's source expression. No runtime lookup. |
| **Tier 3** — C# 14 interceptor | Generated code: the interceptor replaces the `bind:` lambda at the call site with a generated method. No `.Compile()` call at runtime. |
| **Tier 4** — Expression tree fallback | Runtime `.Compile()`: the expression tree is compiled to a delegate on first execution and cached. This is the only tier with runtime overhead. |

The runtime's `StepContext.ResolveBindings()` method checks the `StepDescriptor.BindingKind` discriminator to determine which path to take. For Tiers 1–3, it invokes the generated binding method directly. For Tier 4, it compiles the expression tree once and caches the resulting delegate.

### 2.3 Sequence Diagram — 5-Step Triage Execution

```
 User           ProgramExecutor     StepExecutor     IChatClient    IDocRetriever   IEvaluator
  │                   │                  │                │               │              │
  │  RunAsync(input)  │                  │                │               │              │
  │──────────────────>│                  │                │               │              │
  │                   │  topological     │                │               │              │
  │                   │  sort steps      │                │               │              │
  │                   │                  │                │               │              │
  │                   │ ─── Step 1: retrieve-kb ─────────────────────────>│              │
  │                   │                  │                │  Query(text)  │              │
  │                   │                  │                │<──────────────│              │
  │                   │                  │                │  5 docs       │              │
  │                   │  ctx.Store(docs) │                │               │              │
  │                   │                  │                │               │              │
  │                   │ ─── Step 2: triage (Predict) ───>│               │              │
  │                   │                  │ assemble prompt│               │              │
  │                   │                  │───────────────>│               │              │
  │                   │                  │  GetResponse() │               │              │
  │                   │                  │<───────────────│               │              │
  │                   │                  │ parse output   │               │              │
  │                   │  ctx.Store(out)  │                │               │              │
  │                   │                  │                │               │              │
  │                   │ ─── Step 3: groundedness-check ──────────────────────────────────>│
  │                   │                  │                │               │   Evaluate() │
  │                   │                  │                │               │   score=0.96 │
  │                   │  ctx.StoreScore()│                │               │              │
  │                   │                  │                │               │              │
  │                   │ ─── Step 4: if(score<0.8) ──>│   │               │              │
  │                   │                  │ eval condition │               │              │
  │                   │                  │ result=false   │               │              │
  │                   │  skip repair     │                │               │              │
  │                   │                  │                │               │              │
  │                   │ ─── Step 5: (repair skipped) │   │               │              │
  │                   │                  │                │               │              │
  │  <── Result ──────│                  │                │               │              │
  │  + RuntimeTrace   │                  │                │               │              │
```

### 2.4 ProgramExecutor Implementation

```csharp
public sealed class ProgramExecutor
{
    private readonly IServiceProvider _services;
    private readonly StepExecutorFactory _stepExecutorFactory;
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;

    public ProgramExecutor(
        IServiceProvider services,
        StepExecutorFactory stepExecutorFactory)
    {
        _services = services;
        _stepExecutorFactory = stepExecutorFactory;
        _activitySource = new ActivitySource("LMP.Runtime");
        _meter = new Meter("LMP.Runtime");
    }

    public async Task<ProgramResult<TOutput>> RunAsync<TInput, TOutput>(
        ProgramDescriptor program,
        TInput input,
        CompiledArtifact? artifact = null,
        CancellationToken cancellationToken = default)
    {
        using var programActivity = _activitySource.StartActivity(
            $"program.{program.Name}",
            ActivityKind.Internal);

        programActivity?.SetTag("program.id", program.Id);
        programActivity?.SetTag("program.name", program.Name);

        var ctx = new ExecutionContext(program, input!, artifact);
        var sortedSteps = TopologicalSort(program.Steps, program.Edges);
        var traceSteps = new List<RuntimeTraceStepDescriptor>();

        foreach (var step in sortedSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ctx.IsStepReachable(step.Id))
            {
                continue;
            }

            var stepContext = new StepContext(ctx, step);
            var executor = _stepExecutorFactory.Create(step.Kind);

            using var stepActivity = _activitySource.StartActivity(
                $"step.{step.Name}",
                ActivityKind.Internal);

            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                await executor.ExecuteAsync(stepContext, cancellationToken);
                var traceStep = stepContext.BuildTraceRecord(startedAt);
                traceSteps.Add(traceStep);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stepActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw new StepExecutionException(step.Id, step.Name, ex);
            }
        }

        var output = ctx.GetProgramOutput<TOutput>();
        var trace = new RuntimeTraceDescriptor(
            ProgramId: program.Id,
            VariantId: artifact?.VariantId ?? "uncompiled",
            Steps: traceSteps,
            TotalLatencyMs: traceSteps.Sum(s =>
                (s.EndedAtUtc - s.StartedAtUtc).TotalMilliseconds));

        return new ProgramResult<TOutput>(output, trace);
    }

    private static IReadOnlyList<StepDescriptor> TopologicalSort(
        IReadOnlyList<StepDescriptor> steps,
        IReadOnlyList<EdgeDescriptor> edges)
    {
        var adjacency = edges
            .GroupBy(e => e.FromStepId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToStepId).ToList());

        var inDegree = steps.ToDictionary(s => s.Id, _ => 0);
        foreach (var edge in edges)
            inDegree[edge.ToStepId]++;

        var queue = new Queue<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var stepMap = steps.ToDictionary(s => s.Id);
        var result = new List<StepDescriptor>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(stepMap[current]);

            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (--inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }
        }

        if (result.Count != steps.Count)
            throw new InvalidOperationException("Program graph contains a cycle.");

        return result;
    }
}
```

---

## 3. Step Kind Implementations

### 3.1 PredictStep

The Predict step is the core LM invocation step. It assembles a prompt, calls a language model, and parses the response into a typed C# object.

**Execution flow:**

1. Resolve `IChatClient` from DI using the step's model binding as a keyed service key.
2. Assemble the prompt from the signature's instructions, input field values, and any few-shot examples from the compiled artifact.
3. Call `IChatClient.GetResponseAsync()` with the assembled `ChatMessage` list.
4. Parse the response text into the output type using `System.Text.Json` or `TypeConverter`.
5. Validate the parsed output using `DataAnnotations`.
6. Store the output in the `ExecutionContext`.
7. Emit an `Activity` with rich telemetry tags.

```csharp
public sealed class PredictStepExecutor : IStepExecutor
{
    private readonly IServiceProvider _services;
    private readonly PromptAssembler _promptAssembler;
    private readonly OutputParser _outputParser;
    private readonly ActivitySource _activitySource;
    private readonly Histogram<double> _latencyHistogram;
    private readonly Counter<long> _tokenCounter;

    public PredictStepExecutor(
        IServiceProvider services,
        PromptAssembler promptAssembler,
        OutputParser outputParser,
        Meter meter)
    {
        _services = services;
        _promptAssembler = promptAssembler;
        _outputParser = outputParser;
        _activitySource = new ActivitySource("LMP.Runtime.Predict");
        _latencyHistogram = meter.CreateHistogram<double>(
            "lm.predict.latency_ms", "ms", "LM call latency");
        _tokenCounter = meter.CreateCounter<long>(
            "lm.predict.tokens", "tokens", "Tokens consumed");
    }

    public async Task ExecuteAsync(
        StepContext ctx,
        CancellationToken cancellationToken)
    {
        var step = ctx.Step;
        var signatureId = step.SignatureId
            ?? throw new InvalidOperationException(
                $"Predict step '{step.Name}' has no signature binding.");

        // 1. Resolve IChatClient via Keyed DI
        var modelKey = ctx.GetModelBinding();
        var chatClient = _services.GetRequiredKeyedService<IChatClient>(modelKey);

        // 2. Assemble prompt
        var messages = _promptAssembler.Assemble(ctx);

        // 3. Call the model
        using var activity = _activitySource.StartActivity(
            $"predict.{step.Name}");
        activity?.SetTag("lm.model", modelKey);

        var sw = Stopwatch.StartNew();
        ChatResponse response;

        try
        {
            response = await chatClient.GetResponseAsync(
                messages, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw new PredictException(step.Name, modelKey, ex);
        }

        sw.Stop();

        // 4. Parse response into typed output
        var outputType = ctx.GetOutputClrType();
        var parsed = _outputParser.Parse(
            response.Text ?? "", outputType, step.Name);

        // 5. Store output
        ctx.SetOutput(parsed);

        // 6. Emit telemetry
        var inputTokens = response.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Usage?.OutputTokenCount ?? 0;
        var costUsd = EstimateCost(modelKey, inputTokens, outputTokens);

        activity?.SetTag("lm.tokens.input", inputTokens);
        activity?.SetTag("lm.tokens.output", outputTokens);
        activity?.SetTag("lm.cost.usd", costUsd);
        activity?.SetTag("lm.latency.ms", sw.ElapsedMilliseconds);

        _latencyHistogram.Record(sw.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("step", step.Name));
        _tokenCounter.Add(inputTokens + outputTokens,
            new KeyValuePair<string, object?>("direction", "total"));
    }

    private static double EstimateCost(
        string model, int inputTokens, int outputTokens)
    {
        // Placeholder — replace with a model-specific pricing table
        var inputCostPer1K = 0.005;
        var outputCostPer1K = 0.015;
        return (inputTokens / 1000.0 * inputCostPer1K) +
               (outputTokens / 1000.0 * outputCostPer1K);
    }
}
```

**Error handling:** If `GetResponseAsync` throws a transient error (e.g., HTTP 429), the `IChatClient` middleware pipeline handles it. M.E.AI provides **built-in middleware** — `UseOpenTelemetry()`, `UseDistributedCache()`, `UseLogging()` — that the host registers on the `ChatClientBuilder`. LMP adds only two custom middleware: **`UseLmpStepContext()`** (attaches the current `StepContext` to the `ChatOptions` so downstream middleware can read step metadata) and **`UseLmpCostTracking()`** (accumulates per-step token/cost metrics for the compile report). Retries and circuit breakers are handled by a `ResiliencePipeline` (see §8.4). If parsing fails, a `PredictParsingException` is thrown with the raw LM text attached for debugging.

### 3.2 RetrieveStep

```csharp
public sealed class RetrieveStepExecutor : IStepExecutor
{
    private readonly IServiceProvider _services;
    private readonly ActivitySource _activitySource;

    public RetrieveStepExecutor(IServiceProvider services)
    {
        _services = services;
        _activitySource = new ActivitySource("LMP.Runtime.Retrieve");
    }

    public async Task ExecuteAsync(
        StepContext ctx, CancellationToken cancellationToken)
    {
        var query = ctx.ResolveInputBinding<string>("query");
        var topK = ctx.GetTunableInt("topK", defaultValue: 5);

        var retriever = _services.GetRequiredService<IDocumentRetriever>();

        using var activity = _activitySource.StartActivity(
            $"retrieve.{ctx.Step.Name}");
        var sw = Stopwatch.StartNew();

        var documents = await retriever.RetrieveAsync(
            query, topK, cancellationToken);

        sw.Stop();

        ctx.SetOutput(documents);

        activity?.SetTag("documents.count", documents.Count);
        activity?.SetTag("latency.ms", sw.ElapsedMilliseconds);
    }
}
```

### 3.3 EvaluateStep

```csharp
public sealed class EvaluateStepExecutor : IStepExecutor
{
    private readonly IServiceProvider _services;
    private readonly ActivitySource _activitySource;

    public EvaluateStepExecutor(IServiceProvider services)
    {
        _services = services;
        _activitySource = new ActivitySource("LMP.Runtime.Evaluate");
    }

    public async Task ExecuteAsync(
        StepContext ctx, CancellationToken cancellationToken)
    {
        var evaluatorId = ctx.Step.EvaluatorId
            ?? throw new InvalidOperationException(
                $"Evaluate step '{ctx.Step.Name}' has no evaluator binding.");

        var evaluator = _services.GetRequiredKeyedService<IEvaluator>(evaluatorId);
        var targetOutput = ctx.ResolveInputBinding<object>("target");

        using var activity = _activitySource.StartActivity(
            $"evaluate.{ctx.Step.Name}");

        var result = await evaluator.EvaluateAsync(
            targetOutput, ctx, cancellationToken);

        ctx.SetScore(ctx.Step.Id, result.Score);
        ctx.SetPassFail(ctx.Step.Id, result.Passed);

        activity?.SetTag("evaluator.name", evaluatorId);
        activity?.SetTag("score", result.Score);
    }
}
```

### 3.4 IfStep

The If step evaluates a condition against the current execution context and controls graph reachability.

```csharp
public sealed class IfStepExecutor : IStepExecutor
{
    private readonly ActivitySource _activitySource;

    public IfStepExecutor()
    {
        _activitySource = new ActivitySource("LMP.Runtime.If");
    }

    public Task ExecuteAsync(
        StepContext ctx, CancellationToken cancellationToken)
    {
        var conditionDescriptor = ctx.Step.ConditionExpressionDescriptor
            ?? throw new InvalidOperationException(
                $"If step '{ctx.Step.Name}' has no condition expression.");

        // The condition is compiled from the authored binding at
        // build time into a Func<StepContext, bool> stored in the descriptor.
        // Convention (Tier 1), attribute (Tier 2), and interceptor (Tier 3) bindings
        // are generated code with zero overhead. Expression-tree bindings (Tier 4)
        // use .Compile() as a runtime-only fallback.
        var conditionResult = conditionDescriptor.Evaluate(ctx);

        using var activity = _activitySource.StartActivity(
            $"if.{ctx.Step.Name}");
        activity?.SetTag("condition.result", conditionResult);

        // Mark downstream edges as reachable or unreachable
        ctx.SetBranchResult(ctx.Step.Id, conditionResult);

        return Task.CompletedTask;
    }
}
```

> **Why This Matters:** The If step does not execute child steps directly. It sets a flag in the `ExecutionContext` that the `ProgramExecutor` checks via `IsStepReachable()` before executing subsequent steps. This keeps graph traversal in one place.

### 3.5 RepairStep

The Repair step re-invokes a prediction with failure feedback injected into the prompt. It is used when an evaluation score falls below a threshold.

```csharp
public sealed class RepairStepExecutor : IStepExecutor
{
    private readonly PredictStepExecutor _predictExecutor;
    private readonly PromptAssembler _promptAssembler;
    private readonly ActivitySource _activitySource;

    private const int MaxRepairAttempts = 3;

    public RepairStepExecutor(
        PredictStepExecutor predictExecutor,
        PromptAssembler promptAssembler)
    {
        _predictExecutor = predictExecutor;
        _promptAssembler = promptAssembler;
        _activitySource = new ActivitySource("LMP.Runtime.Repair");
    }

    public async Task ExecuteAsync(
        StepContext ctx, CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity(
            $"repair.{ctx.Step.Name}");

        // Gather feedback from the failed evaluations
        var feedback = ctx.BuildRepairFeedback();

        // Inject feedback into the context so PromptAssembler includes it
        ctx.SetRepairFeedback(feedback);

        // Re-execute as a predict step (reuses same signature)
        await _predictExecutor.ExecuteAsync(ctx, cancellationToken);

        activity?.SetTag("repair.feedback_length", feedback.Length);
        activity?.SetTag("repair.attempt", ctx.GetRepairAttempt());
    }
}
```

The repair step's output **replaces** the original predict output in the context only if the repair succeeds. If repair fails (evaluator still fails after `MaxRepairAttempts`), the original output is preserved and the failure is recorded in the trace — it is never silently swallowed.

---

## 4. StepContext / ExecutionContext

### 4.1 Interface

```csharp
public interface IExecutionContext
{
    /// Get the output of a specific step by its ID.
    T OutputOf<T>(string stepId);

    /// Get the evaluator score for a specific step.
    double ScoreOf(string evaluatorStepId);

    /// Get the pass/fail result for a specific evaluator step.
    bool PassedOf(string evaluatorStepId);

    /// Get the most recently stored output of type T.
    T Latest<T>();

    /// Check if a step is reachable given If-step branching results.
    bool IsStepReachable(string stepId);

    /// The original program input.
    object ProgramInput { get; }

    /// The compiled artifact (null if running uncompiled).
    CompiledArtifact? Artifact { get; }
}
```

### 4.2 Implementation

```csharp
public sealed class ExecutionContext : IExecutionContext
{
    private readonly ConcurrentDictionary<string, object> _outputs = new();
    private readonly ConcurrentDictionary<string, double> _scores = new();
    private readonly ConcurrentDictionary<string, bool> _passFail = new();
    private readonly ConcurrentDictionary<string, bool> _branchResults = new();
    private readonly ConcurrentBag<(Type Type, object Value)> _outputHistory = new();
    private readonly ProgramDescriptor _program;

    public object ProgramInput { get; }
    public CompiledArtifact? Artifact { get; }

    public ExecutionContext(
        ProgramDescriptor program,
        object programInput,
        CompiledArtifact? artifact)
    {
        _program = program;
        ProgramInput = programInput;
        Artifact = artifact;
    }

    public T OutputOf<T>(string stepId)
    {
        if (!_outputs.TryGetValue(stepId, out var value))
            throw new KeyNotFoundException(
                $"No output recorded for step '{stepId}'.");
        return (T)value;
    }

    public double ScoreOf(string evaluatorStepId)
    {
        if (!_scores.TryGetValue(evaluatorStepId, out var score))
            throw new KeyNotFoundException(
                $"No score recorded for evaluator step '{evaluatorStepId}'.");
        return score;
    }

    public bool PassedOf(string evaluatorStepId)
    {
        return _passFail.TryGetValue(evaluatorStepId, out var passed) && passed;
    }

    public T Latest<T>()
    {
        // ConcurrentBag enumeration is safe; iterate reverse for latest
        foreach (var (type, value) in _outputHistory.Reverse())
        {
            if (type == typeof(T))
                return (T)value;
        }
        throw new InvalidOperationException(
            $"No output of type '{typeof(T).Name}' found in context.");
    }

    public void StoreOutput(string stepId, object output)
    {
        _outputs[stepId] = output;
        _outputHistory.Add((output.GetType(), output));
    }

    public void StoreScore(string stepId, double score)
    {
        _scores[stepId] = score;
    }

    public void StorePassFail(string stepId, bool passed)
    {
        _passFail[stepId] = passed;
    }

    public void SetBranchResult(string ifStepId, bool conditionResult)
    {
        _branchResults[ifStepId] = conditionResult;
    }

    public bool IsStepReachable(string stepId)
    {
        lock (_lock)
        {
            // Walk edges: if any incoming edge is ConditionalTrue/ConditionalFalse,
            // check whether the source If step's result matches.
            foreach (var edge in _program.Edges.Where(e => e.ToStepId == stepId))
            {
                if (edge.EdgeKind == EdgeKind.ConditionalTrue &&
                    _branchResults.TryGetValue(edge.FromStepId, out var result) &&
                    !result)
                    return false;

                if (edge.EdgeKind == EdgeKind.ConditionalFalse &&
                    _branchResults.TryGetValue(edge.FromStepId, out var result2) &&
                    result2)
                    return false;
            }
            return true;
        }
    }

    public T GetProgramOutput<T>()
    {
        // The last step's output is the program output
        return Latest<T>();
    }
}
```

> **Why This Matters:** Thread safety via `System.Threading.Lock` (.NET 9+) is required because the compiler may run multiple program evaluations concurrently, sharing the same DI container. `System.Threading.Lock` provides better performance than `lock (object)` — it uses a thinner representation and avoids the monitor's inflated object header overhead. Each `ExecutionContext` is per-invocation, but the lock protects against any future parallel step execution within a single invocation.

---

## 5. Observability Integration

### 5.1 ActivitySource (Distributed Tracing)

Every program execution creates one parent `Activity` for the program and one child `Activity` per step. This produces a trace tree that tools like Jaeger, Zipkin, or Azure Monitor can visualize.

```csharp
// Created once per ProgramExecutor instance
private static readonly ActivitySource ProgramActivitySource =
    new("LMP.Runtime.Program", "1.0.0");

// Per-step sources for filtering
private static readonly ActivitySource PredictActivitySource =
    new("LMP.Runtime.Predict", "1.0.0");
private static readonly ActivitySource RetrieveActivitySource =
    new("LMP.Runtime.Retrieve", "1.0.0");
private static readonly ActivitySource EvaluateActivitySource =
    new("LMP.Runtime.Evaluate", "1.0.0");
```

### 5.2 Meter (Metrics)

```csharp
private static readonly Meter RuntimeMeter = new("LMP.Runtime", "1.0.0");

// Histograms
private static readonly Histogram<double> PredictLatency =
    RuntimeMeter.CreateHistogram<double>(
        "lm.predict.latency_ms", "ms", "Predict step latency");
private static readonly Histogram<double> ProgramLatency =
    RuntimeMeter.CreateHistogram<double>(
        "lm.program.latency_ms", "ms", "Total program latency");

// Counters
private static readonly Counter<long> TokensConsumed =
    RuntimeMeter.CreateCounter<long>(
        "lm.tokens.total", "tokens", "Total tokens consumed");
private static readonly Counter<long> ProgramExecutions =
    RuntimeMeter.CreateCounter<long>(
        "lm.program.executions", "{executions}", "Program execution count");
```

### 5.3 InMemoryExporter for Compiler Feedback

The compiler needs to read runtime traces programmatically (not just export them to Jaeger). The `InMemoryExporter` from the OpenTelemetry SDK stores completed `Activity` objects in a `ConcurrentBag<Activity>` that the compiler reads after each trial.

```csharp
// In compiler setup:
var exportedActivities = new List<Activity>();

services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("LMP.Runtime.*")
        .AddInMemoryExporter(exportedActivities));

// After a trial run:
var predictActivities = exportedActivities
    .Where(a => a.Source.Name == "LMP.Runtime.Predict");

foreach (var activity in predictActivities)
{
    var tokens = activity.GetTagItem("lm.tokens.input");
    var cost = activity.GetTagItem("lm.cost.usd");
    var latency = activity.GetTagItem("lm.latency.ms");
    // Feed into constraint evaluation...
}
```

> **Why This Matters:** This is the closed feedback loop that makes compilation possible. The compiler runs the program, reads the traces via InMemoryExporter, evaluates constraints (cost ≤ $0.03, latency ≤ 2500ms), and decides whether a candidate variant is valid.

---

## 6. Prompt Assembly

### 6.1 How Prompts Are Built

The `PromptAssembler` turns a signature descriptor + step context into a `List<ChatMessage>` that `IChatClient` consumes.

```
┌─────────────────────────────────────────────────┐
│ System Message                                  │
│                                                 │
│  {signature.Instructions}                       │
│                                                 │
│  Respond with valid JSON matching this schema:  │
│  {output JSON schema}                           │
└─────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────┐
│ Few-Shot Example 1 (User)                       │
│  ticket_text: "..."                             │
│  account_tier: "Enterprise"                     │
├─────────────────────────────────────────────────┤
│ Few-Shot Example 1 (Assistant)                  │
│  { "severity": "high", "route_to": "billing" } │
├─────────────────────────────────────────────────┤
│ ...repeat for N few-shot examples...            │
└─────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────┐
│ Current Input (User)                            │
│  ticket_text: "{actual input}"                  │
│  account_tier: "{actual tier}"                  │
│                                                 │
│  [If repair: Previous attempt failed.           │
│   Feedback: {evaluator feedback}]               │
└─────────────────────────────────────────────────┘
```

### 6.2 Implementation

```csharp
public sealed class PromptAssembler
{
    public IList<ChatMessage> Assemble(StepContext ctx)
    {
        var signature = ctx.GetSignatureDescriptor();
        var messages = new List<ChatMessage>();

        // 1. System message: instructions + output schema
        var outputSchema = BuildJsonSchema(signature.Outputs);
        var systemText = $"""
            {signature.Instructions}

            You MUST respond with valid JSON matching this schema:
            {outputSchema}

            Do not include any text outside the JSON object.
            """;
        messages.Add(new ChatMessage(ChatRole.System, systemText));

        // 2. Few-shot examples (from compiled artifact or defaults)
        var examples = ctx.GetFewShotExamples();
        foreach (var example in examples)
        {
            var userFields = FormatFields(signature.Inputs, example.Inputs);
            messages.Add(new ChatMessage(ChatRole.User, userFields));

            var assistantJson = JsonSerializer.Serialize(
                example.ExpectedOutput, SerializerOptions);
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantJson));
        }

        // 3. Current input
        var inputFields = FormatFields(
            signature.Inputs, ctx.GetCurrentInputValues());

        // 4. Append repair feedback if present
        var repairFeedback = ctx.GetRepairFeedback();
        if (repairFeedback is not null)
        {
            inputFields += $"""

                [Previous attempt failed evaluation.]
                Feedback: {repairFeedback}
                Please correct your response.
                """;
        }

        messages.Add(new ChatMessage(ChatRole.User, inputFields));

        return messages;
    }

    private static string FormatFields(
        IReadOnlyList<FieldDescriptor> fields,
        IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder();
        foreach (var field in fields)
        {
            if (values.TryGetValue(field.Name, out var value))
                sb.AppendLine($"{field.Name}: {value}");
        }
        return sb.ToString();
    }

    private static string BuildJsonSchema(
        IReadOnlyList<FieldDescriptor> outputs)
    {
        // Build a simplified JSON schema from field descriptors
        var properties = new Dictionary<string, object>();
        foreach (var field in outputs)
        {
            properties[field.Name] = new
            {
                type = MapClrTypeToJsonType(field.ClrTypeName),
                description = field.Description
            };
        }
        return JsonSerializer.Serialize(
            new { type = "object", properties },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static string MapClrTypeToJsonType(string clrType) => clrType switch
    {
        "System.String" => "string",
        "System.Int32" or "System.Int64" => "integer",
        "System.Double" or "System.Single" => "number",
        "System.Boolean" => "boolean",
        _ => "string"
    };
}
```

---

## 7. Output Parsing

### 7.1 Parse Pipeline

LM responses are free-form text. The runtime must parse them into typed C# objects reliably.

```
 Raw LM Text
     │
     ▼
 ┌────────────────────┐
 │ Extract JSON block  │  Strip markdown fences, leading text, etc.
 └────────┬───────────┘
          ▼
 ┌────────────────────┐
 │ JsonSerializer      │  Deserialize into target CLR type
 │ .Deserialize<T>()   │
 └────────┬───────────┘
          ▼
 ┌────────────────────┐
 │ TypeConverter        │  Handle non-standard formats
 │ fallback             │  ("True"/"FALSE" → bool, etc.)
 └────────┬───────────┘
          ▼
 ┌────────────────────┐
 │ DataAnnotations     │  Validate [Required], [Range], etc.
 │ .TryValidate()      │
 └────────┬───────────┘
          ▼
      Typed T output
```

### 7.2 Implementation

```csharp
public sealed class OutputParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public object Parse(string rawText, Type outputType, string stepName)
    {
        // 1. Extract JSON from response (handles markdown fences)
        var json = ExtractJson(rawText);

        // 2. Attempt JSON deserialization
        object? result;
        try
        {
            result = JsonSerializer.Deserialize(json, outputType, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // 3. Fallback: try IParsable<T>.TryParse() (.NET 7+) for simple types
            if (TryParsableConvert(rawText.Trim(), outputType, out var parsed))
            {
                result = parsed;
            }
            // 4. Fallback: try TypeConverter for legacy types
            else if (TryTypeConverterParse(rawText, outputType, out var converted))
            {
                result = converted;
            }
            else
            {
                throw new OutputParsingException(stepName, rawText, outputType, ex);
            }
        }

        if (result is null)
            throw new OutputParsingException(
                stepName, rawText, outputType,
                new InvalidOperationException("Deserialization returned null."));

        // 5. Validate — prefer IValidatableObject if implemented, then DataAnnotations
        if (result is IValidatableObject validatable)
        {
            var selfErrors = validatable.Validate(new ValidationContext(result)).ToList();
            if (selfErrors.Count > 0)
                throw new OutputValidationException(stepName,
                    string.Join("; ", selfErrors.Select(v => v.ErrorMessage)), result);
        }
        else
        {
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(result);
            if (!Validator.TryValidateObject(
                result, validationContext, validationResults, validateAllProperties: true))
            {
                var errors = string.Join("; ",
                    validationResults.Select(v => v.ErrorMessage));
                throw new OutputValidationException(stepName, errors, result);
            }
        }

        return result;
    }

    // .NET 7+ AOT-safe code fence extraction
    [GeneratedRegex(@"^```(?:\w+)?\s*\n?(.*?)\n?```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();

        // Strip markdown code fences using [GeneratedRegex] (AOT-safe, pre-compiled)
        var match = JsonFenceRegex().Match(trimmed);
        if (match.Success)
            trimmed = match.Groups[1].Value.Trim();

        // Find JSON object boundaries
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return trimmed;
    }

    /// <summary>
    /// Attempts to parse using IParsable&lt;T&gt; (.NET 7+) — the modern pattern
    /// for string-to-type conversion using static abstract interface members.
    /// Prioritized over TypeConverter for .NET 7+ types.
    /// </summary>
    private static bool TryParsableConvert(
        string text, Type targetType, out object? result)
    {
        result = null;
        // Check if the type implements IParsable<T> via reflection
        var parsableInterface = targetType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IParsable<>));
        if (parsableInterface is null) return false;

        var tryParseMethod = parsableInterface.GetMethod("TryParse",
            [typeof(string), typeof(IFormatProvider), targetType.MakeByRefType()]);
        if (tryParseMethod is null) return false;

        var args = new object?[] { text, CultureInfo.InvariantCulture, null };
        var success = (bool)tryParseMethod.Invoke(null, args)!;
        if (success) result = args[2];
        return success;
    }

    private static bool TryTypeConverterParse(
        string text, Type targetType, out object? result)
    {
        result = null;
        var converter = TypeDescriptor.GetConverter(targetType);
        if (converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                result = converter.ConvertFromInvariantString(text.Trim());
                return true;
            }
            catch { return false; }
        }
        return false;
    }
}
```

> **Why This Matters:** LMs are unreliable formatters. The `ExtractJson` method uses `[GeneratedRegex]` (.NET 7+) for AOT-safe, pre-compiled pattern matching of markdown fences. The parse chain prioritizes: JSON → `IParsable<T>.TryParse()` (.NET 7+ static abstract members) → `TypeConverter` fallback. Validation prefers `IValidatableObject` self-validation when implemented, falling back to `DataAnnotations` for types that don't implement it.

---

## 8. Cancellation and Timeout

### 8.1 CancellationToken Threading

Every async method in the runtime accepts a `CancellationToken`. This token flows from the top-level `RunAsync` call through every step executor and into every `IChatClient` call.

```csharp
// Entry point — user or host provides cancellation
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;  // Prevent immediate process termination
    cts.Cancel();     // Signal cancellation to the runtime
};

var result = await program.RunAsync(input, cancellationToken: cts.Token);
```

### 8.2 Timeout Handling

Individual LM calls can be protected with a per-step timeout by linking a timeout `CancellationTokenSource` to the parent token:

```csharp
public async Task ExecuteWithTimeout(
    StepContext ctx,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var timeoutCts = CancellationTokenSource
        .CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(timeout);

    try
    {
        await ExecuteAsync(ctx, timeoutCts.Token);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // The timeout fired, not user cancellation
        throw new StepTimeoutException(
            ctx.Step.Name, timeout);
    }
}
```

### 8.3 Cancellation Contract

| Scenario | Behavior |
|---|---|
| User presses `Ctrl+C` | `OperationCanceledException` propagates up. Trace contains all completed steps. |
| Per-step timeout fires | `StepTimeoutException` thrown with step name and timeout duration. |
| LM provider error | Transient errors handled by the `ResiliencePipeline` (retry + circuit breaker). Non-transient errors wrap in `PredictException`. |
| Parent `CancellationToken` cancelled | All in-flight steps abort at next `ThrowIfCancellationRequested()` or async yield. |

> **Why This Matters:** In production, an unresponsive LM call must not hang your entire pipeline. The linked `CancellationTokenSource` pattern ensures that both user cancellation and per-step timeouts are handled through the same unified mechanism.

### 8.4 Resilience Pipeline for LM Calls

LM API calls are wrapped in a `ResiliencePipeline` from `Microsoft.Extensions.Resilience` that combines retry, circuit breaker, and timeout in a single composable pipeline. This replaces ad-hoc retry logic and provides structured resilience semantics.

```csharp
// Registered in DI via AddResiliencePipeline
services.AddResiliencePipeline("lm-calls", builder =>
{
    builder
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .HandleResult<ChatResponse>(r => r is null)
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(15)
        })
        .AddTimeout(TimeSpan.FromSeconds(60));
});

// In PredictStepExecutor — resolve and use
var pipeline = _services
    .GetRequiredService<ResiliencePipelineProvider<string>>()
    .GetPipeline("lm-calls");

var response = await pipeline.ExecuteAsync(
    async ct => await chatClient.GetResponseAsync(messages, options, ct),
    cancellationToken);
```

> **Why a pipeline, not raw rate limiting?** A `ResiliencePipeline` composes retry (with exponential backoff + jitter), circuit breaker (fail-fast when a provider is down), and timeout (bound individual calls) into a single unit. The runtime delegates resilience to this pipeline; it does not implement its own retry loops.

---

## Appendix: Key Types Summary

```csharp
public sealed record ProgramResult<TOutput>(
    TOutput Output,
    RuntimeTraceDescriptor Trace);

public sealed record RuntimeTraceDescriptor(
    string ProgramId,
    string VariantId,
    IReadOnlyList<RuntimeTraceStepDescriptor> Steps,
    double TotalLatencyMs);

public sealed record RuntimeTraceStepDescriptor(
    string StepId,
    string StepName,
    string Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string Outcome,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    double? CostUsd = null,
    IReadOnlyDictionary<string, double>? Scores = null,
    IReadOnlyList<string>? Diagnostics = null);

public class StepExecutionException(
    string stepId, string stepName, Exception inner)
    : Exception($"Step '{stepName}' ({stepId}) failed.", inner);

public class PredictException(
    string stepName, string model, Exception inner)
    : Exception($"Predict step '{stepName}' (model: {model}) failed.", inner);

public class OutputParsingException(
    string stepName, string rawText, Type targetType, Exception inner)
    : Exception($"Failed to parse output of step '{stepName}' " +
                $"into {targetType.Name}.", inner)
{
    public string RawText { get; } = rawText;
}

public class OutputValidationException(
    string stepName, string errors, object parsed)
    : Exception($"Output validation failed for step '{stepName}': {errors}")
{
    public object ParsedOutput { get; } = parsed;
}

public class StepTimeoutException(string stepName, TimeSpan timeout)
    : TimeoutException(
        $"Step '{stepName}' timed out after {timeout.TotalMilliseconds}ms.");
```
