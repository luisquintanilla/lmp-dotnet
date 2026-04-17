# Unified Optimization API — Specification

> **Status:** Design (Phase A–H)
> **Packages:** `LMP.Abstractions`, `LMP.Core`, `LMP.Optimizers`
> **See also:** [optimization-pipeline.md](../01-architecture/optimization-pipeline.md)

---

## `IOptimizer` — Algorithm Contract

```csharp
// src/LMP.Abstractions/IOptimizer.cs
// Breaking A.1: CompileAsync<TModule> → OptimizeAsync(OptimizationContext)
public interface IOptimizer
{
    /// <summary>Runs one optimization pass, mutating <paramref name="ctx"/> in-place.</summary>
    Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default);
}
```

### Backward-compat extension (LMP.Core)

```csharp
public static class OptimizerExtensions
{
    [Obsolete("Use OptimizeAsync(OptimizationContext) via OptimizationPipeline.")]
    public static async Task<TModule> CompileAsync<TModule>(
        this IOptimizer optimizer, TModule module, IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric, CompileOptions? options = null,
        CancellationToken ct = default) where TModule : LmpModule
    { ... }
}
```

---

## `OptimizationContext` — Pipeline Carrier

```csharp
public sealed class OptimizationContext
{
    public static OptimizationContext For(LmpModule module, IReadOnlyList<Example> trainSet,
        Metric metric, IReadOnlyList<Example>? devSet = null) { ... }

    // Required
    public required IOptimizationTarget Target { get; set; }
    public required IReadOnlyList<Example> TrainSet { get; set; }
    public required Metric Metric { get; set; }

    // Passed between stages
    public IReadOnlyList<Example> DevSet { get; set; } = [];
    public TypedParameterSpace SearchSpace { get; set; } = TypedParameterSpace.Empty;
    public TrialHistory TrialHistory { get; } = new();
    public CostBudget Budget { get; set; } = CostBudget.Unlimited;
    public ParetoFrontier? ParetoFrontier { get; set; }
    public ReflectionLog ReflectionLog { get; set; } = ReflectionLog.Empty;

    // Observability
    public ActivitySource? ActivitySource { get; set; }
    public ILogger? Logger { get; set; }
    public IProgress<OptimizationProgress>? Progress { get; set; }

    // Extensibility bag (typed tags)
    public IDictionary<string, object> Bag { get; } = new Dictionary<string, object>();
}
```

---

## `IOptimizationTarget` — Vertical Seam

```csharp
public interface IOptimizationTarget
{
    TargetShape Shape { get; }     // SingleTurn | MultiTurn

    // Execute and return trace alongside output (not a side-channel)
    Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default);

    TypedParameterSpace GetParameterSpace();
    TargetState GetState();
    void ApplyState(TargetState state);

    // Immutable clone for parallel trial evaluation
    IOptimizationTarget WithParameters(ParameterAssignment assignment);

    // Service locator for optional capabilities (e.g., IChatClient, IRetriever)
    TService? GetService<TService>() where TService : class;

    // Write .g.cs artifact — default is no-op (only ModuleTarget implements)
    Task WriteArtifactAsync(CompileOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

---

## `OptimizationPipeline` — Tier 2 Builder

```csharp
public sealed class OptimizationPipeline : IOptimizer
{
    // Start from a module
    public static OptimizationPipeline For(LmpModule module) { ... }

    // Pre-configured axis pipelines
    public static OptimizationPipeline ForInstructions(LmpModule module) { ... }
    public static OptimizationPipeline ForTools(LmpModule module) { ... }
    public static OptimizationPipeline Auto(LmpModule module, Goal goal = Goal.Accuracy) { ... }

    // Fluent builder
    public OptimizationPipeline Use(IOptimizer step) { ... }
    public OptimizationPipeline WithBudget(Action<CostBudget.Builder> configure) { ... }
    public OptimizationPipeline UseTelemetry(ActivitySource activitySource) { ... }
    public OptimizationPipeline UseProgress(IProgress<OptimizationProgress> progress) { ... }

    // Run
    public Task<OptimizationResult> OptimizeAsync(
        IReadOnlyList<Example> trainSet,
        IReadOnlyList<Example> devSet,
        Metric metric,
        CancellationToken ct = default) { ... }

    // IOptimizer
    public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default) { ... }
}
```

---

## `OptimizationResult` — What You Get Back

```csharp
public sealed record OptimizationResult
{
    public required IOptimizationTarget Target { get; init; }
    public required float BaselineScore { get; init; }
    public required float OptimizedScore { get; init; }
    public required IReadOnlyList<Trial> Trials { get; init; }

    // Persist optimized state as a .g.cs artifact
    public Task WriteArtifactAsync(CompileOptions? options, CancellationToken ct = default) { ... }
}
```

---

## `LmpModuleOptimizationExtensions` — Convenience Methods

```csharp
public static class LmpModuleOptimizationExtensions
{
    // Tier 2: get a pipeline builder from any module
    public static OptimizationPipeline AsOptimizationPipeline(this LmpModule module) { ... }

    // Tier 4 shorthand for a module
    public static Task<OptimizationResult> OptimizeAsync(
        this LmpModule module,
        IReadOnlyList<Example> trainSet,
        IReadOnlyList<Example> devSet,
        Metric metric,
        Goal goal = Goal.Accuracy,
        CancellationToken ct = default) { ... }
}
```

---

## `Lmp.Optimize` — Tier 4 Façade

```csharp
public static class Lmp
{
    public static class Optimize
    {
        // Takes any target (module, ChatClient, agent, Func<,>)
        // Returns the same shape, only better
        public static Task<OptimizationResult> AutoAsync(
            object target,
            IReadOnlyList<Example> trainSet,
            IReadOnlyList<Example>? devSet = null,
            Metric? metric = null,              // null → safe no-op + diagnostic
            Goal goal = Goal.Accuracy,
            CancellationToken ct = default) { ... }
    }
}
```

---

## `TypedParameterSpace` — The Unifier (Phase C)

```csharp
public abstract record ParameterKind;
public record Categorical(int Count) : ParameterKind;
public record Integer(int Min, int Max) : ParameterKind;
public record Continuous(double Min, double Max, Scale Scale = Scale.Linear) : ParameterKind;
public record StringValued(IStringGenerator Generator) : ParameterKind;   // GEPA feeds this
public record Subset(IReadOnlyList<object> Pool, int MinSize, int MaxSize) : ParameterKind;
    // Pool items: AITool (tools), string (model names), Example (demos), object (any)
public record Composite(IReadOnlyList<(string Name, ParameterKind Kind)> Members) : ParameterKind;

public sealed class TypedParameterSpace
{
    public static TypedParameterSpace Empty { get; } = new();
    public IReadOnlyList<(string Name, ParameterKind Kind)> Parameters { get; }
    public TypedParameterSpace With(string name, ParameterKind kind) { ... }
}

public sealed class ParameterAssignment
{
    public object? Get(string name) { ... }
    public T Get<T>(string name) { ... }
    public ParameterAssignment With(string name, object value) { ... }
}
```

---

## `ISearchStrategy` — Replaces `ISampler` (Phase C, Breaking C.1)

```csharp
// NEW (Phase C)
public interface ISearchStrategy
{
    ParameterAssignment Propose(TypedParameterSpace space);
    void Update(ParameterAssignment assignment, float score, TrialCost cost);
}

// OLD — marked Obsolete; LegacyCategoricalAdapter wraps for one version window
[Obsolete("Use ISearchStrategy. See LegacyCategoricalAdapter for migration.")]
public interface ISampler
{
    Dictionary<string, int> Propose();
    void Update(Dictionary<string, int> config, float score, TrialCost cost);
}
```

---

## `CostBudget` — Multi-Dimensional Budget

```csharp
public sealed record CostBudget
{
    public static CostBudget Unlimited { get; } = new();

    public long? MaxTokens { get; init; }         // → TrialCost.TotalTokens
    public int? MaxTurns { get; init; }           // → TrialCost.ApiCalls
    public TimeSpan? MaxWallClock { get; init; }  // → TrialCost.ElapsedMilliseconds
    public Func<TrialCost, bool>? Custom { get; init; }

    public sealed class Builder
    {
        public Builder MaxTokens(long n) { ... }
        public Builder MaxTurns(int n) { ... }
        public Builder MaxSeconds(double s) { ... }
        public Builder Custom(Func<TrialCost, bool> predicate) { ... }
        public CostBudget Build() { ... }
    }
}
```

Dollar amounts via `Custom`: `cost => cost.InputTokens * 0.01/1000 + cost.OutputTokens * 0.06/1000 > maxDollars`.

---

## `Goal` Enum

```csharp
public enum Goal
{
    Accuracy,   // Z3Feasibility → BootstrapFewShot → GEPA → MIPROv2 → BayesianCalibration
    Speed,      // BootstrapFewShot → RouteLLM → MultiFidelity
    Cost,       // BootstrapFewShot → MIPROv2 (CostAwareSampler) → RouteLLM
    Balanced    // Z3Feasibility → BootstrapFewShot → GEPA → RouteLLM (Pareto)
}
```

---

## `MetricVector` — Cost as a Metric Dimension (Phase H)

```csharp
public readonly struct MetricVector
{
    public float Score { get; init; }
    public long Tokens { get; init; }
    public double LatencyMs { get; init; }
    public int Turns { get; init; }
    public ImmutableDictionary<string, float> Custom { get; init; }
}

// Non-breaking IMetric upgrade (existing Metric.Create() still works via default method)
public interface IMetric
{
    MetricVector Evaluate(Example example, object prediction);
    float Score(Example e, object p) => Evaluate(e, p).Score;  // backward compat
}
```

---

## Breaking Changes Summary

| Phase | Change | Migration |
|-------|--------|-----------|
| A | `IOptimizer.CompileAsync<TModule>` → `OptimizeAsync(OptimizationContext)` | Extension method `OptimizerExtensions.CompileAsync<TModule>()` preserves call sites |
| C | `ISampler` → `ISearchStrategy` | `[Obsolete]` + `LegacyCategoricalAdapter` for one version window |
| E | `ReActAgent(IEnumerable<AIFunction>)` → `IEnumerable<AITool>` | `AIFunction IS-A AITool`; call sites unchanged |
