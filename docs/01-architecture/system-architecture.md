# LMP System Architecture

> **Source of Truth:** This document is derived from the Master Implementation Blueprint sections 5A, 6A, 7, 8, 9, 10, 10A, 11, and 2A. If any conflict arises, the blueprint wins.

---

## 1. Architecture Overview

### Layer Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                       CLI Layer (dotnet lmp)                     │
│              compile · run · eval · inspect-artifact             │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────┐   ┌─────────────────┐   ┌──────────────────┐   │
│  │  Compiler /  │   │  Evaluation     │   │   Artifact       │   │
│  │  Optimizer   │──>│  Layer          │   │   Layer          │   │
│  │  Layer       │<──│  (IEvaluator)   │   │   (JSON, hot-    │   │
│  │              │   └─────────────────┘   │    swap, reload) │   │
│  │  propose →   │                         │                  │   │
│  │  execute →   │────────────────────────>│  save / load     │   │
│  │  evaluate →  │                         └──────────────────┘   │
│  │  constrain → │                                ▲               │
│  │  select      │                                │ artifact      │
│  └──────┬───────┘                                │ application   │
│         │ trial execution                        │               │
│  ┌──────▼────────────────────────────────────────┴───────────┐   │
│  │                      Runtime Layer                        │   │
│  │   IChatClient · TPL Dataflow · Keyed DI · IAsyncEnumerable│   │
│  └──────────────────────────▲────────────────────────────────┘   │
│                             │ program descriptors                │
│  ┌──────────────────────────┴────────────────────────────────┐   │
│  │                   LM Program IR Layer                     │   │
│  │   ProgramDescriptor · StepDescriptor · SignatureDescriptor│   │
│  └──────────────────────────▲────────────────────────────────┘   │
│                             │ generated descriptors              │
│  ┌──────────────────────────┴────────────────────────────────┐   │
│  │              Roslyn Layer (Build Time)                     │   │
│  │ Source Generators · Interceptors · Analyzers · Code Fixes  │   │
│  └──────────────────────────▲────────────────────────────────┘   │
│                             │ attributed source                  │
│  ┌──────────────────────────┴────────────────────────────────┐   │
│  │                   Authoring Layer                          │   │
│  │   [LmpSignature] · [LmpProgram] · LmpProgram<TIn, TOut>  │   │
│  └───────────────────────────────────────────────────────────┘   │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

### Layer Summaries

| Layer | Responsibility |
|-------|---------------|
| **Authoring** | The developer-facing C# programming model. Developers write `[LmpSignature]` records and `[LmpProgram]` classes using typed attributes, base classes, and a fluent graph-building API. |
| **Roslyn** | Build-time validation and code generation. Source generators discover attributed types and emit `.g.cs` descriptor files. C# 14 interceptors read lambda binding syntax trees at compile time (Tier 3 binding). Analyzers surface diagnostics (LMP001–007) in the IDE. Code fixes offer quick-repair actions. |
| **LM Program IR** | The canonical internal representation that all other layers consume. A set of immutable record types (`ProgramDescriptor`, `StepDescriptor`, etc.) that are independent of Roslyn syntax and runtime object identity. |
| **Runtime** | Compiles IR to TPL Dataflow blocks (`TransformBlock`, `ActionBlock`, `BroadcastBlock`, `JoinBlock`) for step execution. Invokes `IChatClient` through M.E.AI middleware pipeline and applies compiled artifacts. |
| **Evaluation** | Scores model outputs using `IEvaluator` implementations. Feeds metric results back into the compiler's selection loop. Integrates with `Microsoft.Extensions.AI.Evaluation`. |
| **Compiler / Optimizer** | Orchestrates optimization trials: proposes candidate variants, executes them, evaluates results, enforces constraints, selects the best valid variant, and emits a compiled artifact. |
| **Artifact** | Persists the compiled variant as a versioned JSON file. Supports hot-swap via `AssemblyLoadContext` and auto-reload via `IOptionsMonitor`. |

### What Runs When

```
BUILD TIME            COMPILE TIME               RUNTIME               DEPLOY TIME
───────────────────   ─────────────────────────   ───────────────────   ──────────────
Author C# source      dotnet lmp compile          Program.RunAsync()    Load artifact
  ↓                     ↓                           ↓                    ↓
Roslyn parses         Extract search space        Bind step inputs      Validate hash
  ↓                     ↓                           ↓                    ↓
Analyzers check       Propose candidate (backend) Call IChatClient      Apply params
  ↓                     ↓                           ↓                    ↓
Generators emit       Execute trial (runtime)     Emit OTEL traces      Hot-swap via
 .g.cs descriptors      ↓                           ↓                   ALC + IOptions
  ↓                   Evaluate (IEvaluator)        Return TOut            Monitor
IR constructed          ↓
                      Check constraints
                        ↓
                      Select best valid → emit artifact JSON
```

---

## 2. Layer-by-Layer Architecture

### 2.1 Authoring Layer (Build Time)

Developers define two kinds of types:

1. **`[LmpSignature]` records** — typed LM task contracts with `[Input]` and `[Output]` fields.
2. **`[LmpProgram]` classes** — directed graphs of steps that extend `LmpProgram<TIn, TOut>`.

```csharp
[LmpSignature]
public sealed record TriageTicket
{
    public required string Instructions { get; init; } = """
        You are a senior enterprise support triage assistant.
        Classify the issue severity and determine the owning team.
        """;

    [Input("Raw customer support ticket text")]
    public required string TicketText { get; init; }

    [Input("Customer plan tier such as Free, Pro, Enterprise")]
    public required string AccountTier { get; init; }

    [Output("Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }

    [Output("Owning team name")]
    public required string RouteToTeam { get; init; }

    [Output("True if escalation to a human is required")]
    public required bool Escalate { get; init; }
}
```

**What attributes trigger:** The `[LmpSignature]` attribute marks the type for source-generator discovery. During compilation, the Roslyn source generator scans the semantic model for every type decorated with this attribute and emits a corresponding `SignatureDescriptor` in a generated `.g.cs` file.

**Generated files and their contents:** For the type above, the generator produces `TriageTicket.g.cs`:

```csharp
// <auto-generated />
namespace Demo.Generated;

file static class TriageTicket_Descriptor
{
    public static readonly SignatureDescriptor Instance = new(
        Id: "triageticket",
        Name: "TriageTicket",
        Instructions: """
            You are a senior enterprise support triage assistant.
            Classify the issue severity and determine the owning team.
            """,
        Inputs: new[]
        {
            new FieldDescriptor("TicketText", "Input", "System.String",
                "Raw customer support ticket text", Required: true),
            new FieldDescriptor("AccountTier", "Input", "System.String",
                "Customer plan tier such as Free, Pro, Enterprise", Required: true),
        },
        Outputs: new[]
        {
            new FieldDescriptor("Severity", "Output", "System.String",
                "Severity: Low, Medium, High, Critical", Required: true),
            new FieldDescriptor("RouteToTeam", "Output", "System.String",
                "Owning team name", Required: true),
            new FieldDescriptor("Escalate", "Output", "System.Boolean",
                "True if escalation to a human is required", Required: true),
        });
}
```

> **Implementation Note:** The generated class uses the `file` access modifier so it never leaks into public API. This follows the same pattern as `System.Text.Json` source generation. All metadata is embedded as C# constants — no separate `.json` manifest files are emitted because source generators cannot reliably produce non-`.cs` output. C# 13 partial properties replace `static abstract CreateDescriptor()` — each signature type exposes `static partial SignatureDescriptor Descriptor { get; }`, and the source generator emits the implementation half. C# 14 extension members and field-backed properties further simplify generated descriptor code.

### 2.2 Roslyn Layer (Build Time)

#### Source Generator Architecture

The source generator implements `IIncrementalGenerator` — Roslyn's modern pipeline API that caches intermediate results and re-runs only when inputs change.

```
Roslyn Compilation
  ↓
SyntaxProvider.ForAttributeWithMetadataName("LmpSignatureAttribute")
  ↓  filters to candidate types
TransformEach → extract field metadata from semantic model
  ↓
RegisterSourceOutput → emit .g.cs with SignatureDescriptor
```

> **Implementation Note:** `IIncrementalGenerator` replaces the older `ISourceGenerator`. It uses a pull-based pipeline (`ForAttributeWithMetadataName` → `Select` → `Collect` → `RegisterSourceOutput`) and Roslyn automatically skips re-generation when the input type has not changed.

#### Interceptor-Based Binding (C# 14)

C# 14 interceptors are now **stable** and used for Tier 3 lambda binding. An interceptor source generator reads lambda syntax trees at compile time (e.g., `step.BindInput(ctx => ctx.OutputOf(otherStep).Field)`) and rewrites them into direct binding descriptors — avoiding runtime expression-tree overhead while preserving a natural authoring syntax.

#### Analyzer Architecture

Each **`DiagnosticAnalyzer`** registers a `SyntaxKind` or symbol action, inspects the semantic model, and reports a `Diagnostic` when a rule is violated.

| ID | Rule | What It Checks |
|----|------|---------------|
| **LMP001** | Missing field description | An `[Input]` or `[Output]` field has no description string. |
| **LMP002** | Missing/empty instructions | A `[LmpSignature]` type has a blank or absent `Instructions` property. |
| **LMP003** | Duplicate step name | Two steps in one `[LmpProgram]` share the same name string. |
| **LMP004** | Non-deterministic step name | A step name is computed at runtime (not a constant) — breaks trace/artifact stability. |
| **LMP005** | Unsupported output type | An output field uses a type the framework cannot serialize (e.g., `Stream`). |
| **LMP006** | Invalid graph cycle | The program graph contains a cycle, which MVP does not support. |

**Complete analyzer example for LMP001:**

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingFieldDescriptionAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor Rule = new(
        id: "LMP001",
        title: "Missing field description",
        messageFormat: "Field '{0}' is missing a description",
        category: "LMP.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor>
        SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeProperty,
            SymbolKind.Property);
    }

    private static void AnalyzeProperty(
        SymbolAnalysisContext ctx)
    {
        var prop = (IPropertySymbol)ctx.Symbol;
        var attr = prop.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "InputAttribute"
                                   or "OutputAttribute");
        if (attr is null) return;

        // Check that the constructor arg (description) is non-empty
        if (attr.ConstructorArguments.Length == 0
            || string.IsNullOrWhiteSpace(
                   attr.ConstructorArguments[0].Value as string))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Rule, prop.Locations[0], prop.Name));
        }
    }
}
```

#### CodeFixProvider Architecture

A `CodeFixProvider` registers for specific diagnostic IDs, computes a replacement `SyntaxNode`, and offers a "lightbulb" fix in the IDE. MVP code fixes include: add a placeholder description string, suggest extracting a constant for a step name.

### 2.3 LM Program IR Layer

The **IR** is the single canonical internal representation that the runtime, compiler, and artifact layers all consume. It is independent of Roslyn syntax trees and runtime object identity.

#### Construction Flow

```
Source generators emit .g.cs
  ↓
Generated code instantiates descriptors at class-load time
  ↓
Registration helpers register descriptors into DI container
  ↓
Runtime / Compiler resolve descriptors from DI
```

#### Complete Type Definitions

```csharp
// ── Fields ──
public sealed record FieldDescriptor(
    string Name,
    string Direction,      // "Input" or "Output"
    string ClrTypeName,
    string Description,
    bool Required = true,
    IReadOnlyDictionary<string, string>? Metadata = null);

// ── Signatures ──
public sealed record SignatureDescriptor(
    string Id,
    string Name,
    string Instructions,
    IReadOnlyList<FieldDescriptor> Inputs,
    IReadOnlyList<FieldDescriptor> Outputs,
    string? SourceTypeName = null,
    string? AssemblyName = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

// ── Steps ──
public sealed record StepDescriptor(
    string Id,
    string Name,
    string Kind,            // "Predict" | "Retrieve" | "Evaluate" | "If" | "Repair"
    IReadOnlyList<BindingDescriptor> Bindings,
    string? SignatureId = null,
    string? EvaluatorId = null,
    IReadOnlyList<TunableParameterDescriptor>? TunableParameters = null,
    ConditionExpressionDescriptor? ConditionExpressionDescriptor = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

// ── Programs ──
public sealed record ProgramDescriptor(
    string Id,
    string Name,
    string InputTypeName,
    string OutputTypeName,
    IReadOnlyList<StepDescriptor> Steps,
    IReadOnlyList<EdgeDescriptor> Edges,
    IReadOnlyDictionary<string, string>? Metadata = null);

// ── Edges ──
public sealed record EdgeDescriptor(
    string FromStepId,
    string ToStepId,
    string EdgeKind);       // "Sequence" | "ConditionalTrue" | "ConditionalFalse"

// ── Tunables ──
public sealed record TunableParameterDescriptor(
    string Id,
    string StepId,
    string ParameterKind,   // "Instruction" | "FewShotCount" | "Temperature" | etc.
    string Name,
    double? MinValue = null,
    double? MaxValue = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? DefaultValue = null);

// ── Evaluation ──
public sealed record EvaluationAttachmentDescriptor(
    string Id,
    string Name,
    string Scope,           // "Step" | "Program"
    IReadOnlyList<string> MetricNames);

// ── Variants & Trials ──
public sealed record VariantDescriptor(
    string VariantId,
    string ProgramId,
    IReadOnlyDictionary<string, string> SelectedParameters,
    IReadOnlyDictionary<string, string>? TrialMetadata = null);

public sealed record TrialResultDescriptor(
    string VariantId,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<ConstraintResultDescriptor> ConstraintResults,
    bool Succeeded,
    double ElapsedMs);

// ── Constraints ──
public sealed record ConstraintDescriptor(
    string Id,
    string MetricName,
    ConstraintOperator Operator,
    double Threshold,
    ConstraintSeverity Severity,
    ConstraintScope Scope,
    string? Message = null);

public enum ConstraintOperator
    { Equal, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual }
public enum ConstraintSeverity { Hard, Soft }
public enum ConstraintScope    { Compile, Trial, Program }
```

> **Implementation Note:** All IR types are `sealed record` — this gives you structural equality, `with`-expression variant generation, and immutability. These records are stored in `LMP.Abstractions` so every layer can reference them without circular dependencies.

### 2.4 Runtime Layer

#### Execution Engine

The runtime compiles a `ProgramDescriptor` down to TPL Dataflow blocks — `TransformBlock<TIn, TOut>` for Predict/Retrieve steps, `ActionBlock<T>` for Evaluate steps, `BroadcastBlock<T>` for fan-out, and `JoinBlock<T1, T2>` for convergence — linked by `Edges`. Step outputs flow through the dataflow mesh via a shared **`StepContext`**.

#### Three-Tier Binding Model

Data flows between steps using a tiered binding model resolved at the earliest possible phase:

| Tier | Mechanism | Resolution Time | Description |
|------|-----------|----------------|-------------|
| **Tier 1** | Convention-based auto-binding | Build time (source-gen detected) | Matching names/types between step outputs and inputs are bound automatically. |
| **Tier 2** | `[BindFrom]` attribute-based | Build time (source-gen reads at compile time) | Explicit `[BindFrom("stepName", "fieldName")]` attributes on input properties. |
| **Tier 3** | C# 14 interceptor-based lambda binding | Build time (interceptor reads lambda syntax tree at compile time) | Developer writes `ctx => ctx.OutputOf(step).Field`; an interceptor rewrites it to a static binding descriptor. |
| **Tier 4** | Expression trees | Runtime only | Fallback for dynamic graphs where binding targets are computed at runtime. |

> **Implementation Note:** Tiers 1–3 are fully resolved at compile time with zero runtime reflection. Tier 4 (expression trees) is a runtime-only fallback intended for advanced scenarios such as dynamically composed program graphs. The source generator and interceptor pipeline emit `BindingDescriptor` records for Tiers 1–3.

#### Sequence Diagram — 3-Step Program Execution

```
 Developer              Runtime Engine          IChatClient        OTEL
    │                        │                       │               │
    │  RunAsync(input)       │                       │               │
    ├───────────────────────>│                       │               │
    │                        │ Activity.Start("retrieve")           │
    │                        ├──────────────────────────────────────>│
    │                        │ query retriever                      │
    │                        ├───────────────────>│                  │
    │                        │<──────────────────-│ docs             │
    │                        │ Activity.Stop()                      │
    │                        ├──────────────────────────────────────>│
    │                        │                                      │
    │                        │ Activity.Start("predict")            │
    │                        ├──────────────────────────────────────>│
    │                        │ assemble prompt from descriptor      │
    │                        │ + variant params + context            │
    │                        │ CompleteAsync(messages)               │
    │                        ├───────────────────>│                  │
    │                        │<──────────────────-│ structured output│
    │                        │ parse → store in StepContext          │
    │                        │ Activity.Stop()                      │
    │                        ├──────────────────────────────────────>│
    │                        │                                      │
    │                        │ Activity.Start("evaluate")           │
    │                        ├──────────────────────────────────────>│
    │                        │ run IEvaluator on predict output     │
    │                        │ store scores in StepContext           │
    │                        │ Activity.Stop()                      │
    │                        ├──────────────────────────────────────>│
    │                        │                                      │
    │  <── TOut ─────────────│                                      │
```

#### Keyed DI for Multi-Model Routing

**.NET 10 Keyed DI** lets each step resolve a different model client by name:

```csharp
services.AddKeyedSingleton<IChatClient>("triage",
    new ChatClientBuilder()
        .UseOpenTelemetry()          // M.E.AI built-in
        .UseDistributedCache()       // M.E.AI built-in
        .UseLogging()                // M.E.AI built-in
        .UseLmpStepContext()         // LMP: enriches Activity.Current with step name, trial ID, program name
        .UseLmpCostTracking()        // LMP: accumulates token cost per trial
        .UseChatCompletions("gpt-4o")
        .Build());
services.AddKeyedSingleton<IChatClient>("eval",
    new ChatClientBuilder()
        .UseOpenTelemetry()
        .UseDistributedCache()
        .UseLogging()
        .UseLmpStepContext()
        .UseLmpCostTracking()
        .UseChatCompletions("gpt-4o-mini")
        .Build());
```

At runtime, the engine resolves `[FromKeyedServices("triage")] IChatClient` for the predict step and `"eval"` for the evaluation step.

#### Observability

OpenTelemetry tracing, distributed caching, and logging are handled by **M.E.AI built-in middleware** (`UseOpenTelemetry()`, `UseDistributedCache()`, `UseLogging()`). LMP provides only two thin middleware layers:

- **`UseLmpStepContext()`** — enriches `Activity.Current` with step name, trial ID, and program name (tags: `lmp.step.name`, `lmp.step.kind`, `lmp.program.name`, `lmp.trial.id`).
- **`UseLmpCostTracking()`** — accumulates token cost per trial (tags: `lmp.tokens.input`, `lmp.tokens.completion`, `lmp.cost.usd`).

A **`Meter`** emits `lmp.predict.latency_ms` and `lmp.cost.total_usd` histograms.

#### Streaming

Predict steps can return **`IAsyncEnumerable<StreamingChatCompletionUpdate>`**, enabling token-by-token streaming with `await foreach`. The runtime buffers the full response for context propagation while forwarding chunks to the caller.

### 2.5 Evaluation Layer

The evaluation layer integrates with **`Microsoft.Extensions.AI.Evaluation`** which provides production-grade evaluators (groundedness, coherence, fluency, relevance).

#### IEvaluator Abstraction

```csharp
public interface IEvaluator
{
    string Name { get; }
    IReadOnlyList<string> MetricNames { get; }

    ValueTask<EvaluationResult> EvaluateAsync(
        EvaluationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record EvaluationResult(
    IReadOnlyDictionary<string, double> Scores,
    bool Passed,
    string? Diagnostics = null);
```

#### Concrete Evaluator Example

```csharp
public sealed class RoutingAccuracyEvaluator : IEvaluator
{
    public string Name => "RoutingAccuracy";
    public IReadOnlyList<string> MetricNames => ["routing_accuracy"];

    public ValueTask<EvaluationResult> EvaluateAsync(
        EvaluationContext context, CancellationToken ct)
    {
        var predicted = context.GetOutput<string>("RouteToTeam");
        var expected  = context.GetExpected<string>("expectedRouteToTeam");

        double score = string.Equals(predicted, expected,
            StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        return ValueTask.FromResult(new EvaluationResult(
            Scores: new Dictionary<string, double> { ["routing_accuracy"] = score },
            Passed: score >= 1.0));
    }
}
```

#### Feeding Into the Compile Loop

After every trial execution, the compiler collects all `EvaluationResult` objects, aggregates metric scores, and checks them against each `ConstraintDescriptor`. The aggregated metrics form the `TrialResultDescriptor.Metrics` dictionary that drives selection. Metric aggregation (cosine similarity for embedding retrieval, sum, average) uses **`System.Numerics.Tensors.TensorPrimitives`** for SIMD-accelerated computation.

### 2.6 Compiler / Optimizer Layer

The compiler is the orchestration engine. The **optimizer backend** is a pluggable candidate-proposal strategy — it does **not** own trial execution, evaluation, constraint checking, or artifact emission.

#### IOptimizerBackend Interface

```csharp
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

#### Compile Loop — Complete Pseudocode

```csharp
public async Task<CompileReport> CompileAsync(
    CompileSpec spec, CancellationToken ct)
{
    // 1. Build search space from IR
    var searchSpace = ExtractSearchSpace(spec.Program);
    var objective   = spec.Objective;
    var constraints = spec.Constraints;

    var trialResults = new List<TrialResultDescriptor>();

    for (int trial = 0; trial < spec.MaxTrials; trial++)
    {
        ct.ThrowIfCancellationRequested();

        // 2. PROPOSE — ask backend for next candidate
        var candidate = await _backend.ProposeNextAsync(
            searchSpace, objective, constraints,
            trialResults, ct);

        // 3. Generate variant using records + with expression
        //    e.g., variant = baseConfig with { Temperature = 0.3 }
        var variant = ApplyCandidate(spec.Program, candidate);

        // 4. EXECUTE — run the program variant on training data
        //    Microsoft.Extensions.Resilience: composable retry + circuit breaker + timeout for LM calls
        await _resiliencePipeline.ExecuteAsync(ct);
        var traces = await _runtime.ExecuteOnDatasetAsync(
            variant, spec.TrainSet, ct);

        //    HybridCache deduplicates identical LM calls across trials
        //    .NET 10 tag-based eviction: tag by trial-{id}, step-{name}; RemoveByTagAsync() between trials
        //    InMemoryExporter makes traces available in-process

        // 5. EVALUATE — score the outputs
        var metrics = await _evaluator.AggregateAsync(
            traces, spec.Evaluators, ct);

        // 6. CONSTRAIN — check hard constraints
        var constraintResults = CheckConstraints(
            constraints, metrics);
        bool valid = constraintResults
            .Where(c => c.Severity == ConstraintSeverity.Hard)
            .All(c => c.Passed);

        // 7. Record trial result
        trialResults.Add(new TrialResultDescriptor(
            VariantId: variant.VariantId,
            Metrics: metrics,
            ConstraintResults: constraintResults,
            Succeeded: valid,
            ElapsedMs: sw.ElapsedMilliseconds));
    }

    // 8. SELECT — pick best valid variant
    var best = trialResults
        .Where(t => t.Succeeded)
        .OrderByDescending(t => ComputeObjective(t, objective))
        .ThenBy(t => t.Metrics.GetValueOrDefault("avg_cost_usd"))
        .ThenBy(t => t.Metrics.GetValueOrDefault("p95_latency_ms"))
        .ThenBy(t => t.VariantId)   // deterministic tie-break
        .FirstOrDefault();

    // 9. Emit artifact or failure report
    return BuildReport(trialResults, best);
}
```

#### Key Infrastructure

| Concern | .NET Primitive | Role in Compile Loop |
|---------|---------------|---------------------|
| **Immutable variants** | `record` + `with` expression | Generates candidate configs without mutation |
| **LM call resilience** | `Microsoft.Extensions.Resilience` | Composable retry + circuit breaker + timeout for LM calls during compile loop |
| **Response dedup** | `HybridCache` (L1 + L2) | Same prompt → cached response across trials. .NET 10 tag-based eviction: tag by `trial-{id}` and `step-{name}`, `RemoveByTagAsync()` between trials |
| **Trace feedback** | `InMemoryExporter` | Compiler reads runtime traces programmatically |
| **Budget control** | `MaxTrials` config | Bounds optimization cost and time |

### 2.7 Artifact Layer

A **compiled artifact** is a versioned JSON file produced by the compiler and consumed by the runtime at deploy time.

#### Artifact JSON Schema

```json
{
  "programId": "support-triage",
  "compiledVersion": "1.0.0",
  "variantId": "triage-v17",
  "baseProgramHash": "a1b2c3d4e5f6...",
  "selectedParameters": {
    "predict.instruction": "You are a senior support triage ...",
    "predict.fewShotCount": "3",
    "predict.temperature": "0.2",
    "predict.model": "gpt-4o",
    "retrieve.topK": "5"
  },
  "validationMetrics": {
    "routing_accuracy": 0.91,
    "severity_accuracy": 0.88,
    "groundedness": 0.95,
    "policy_pass_rate": 1.0
  },
  "provenance": {
    "compiledAtUtc": "2025-01-15T10:30:00Z",
    "trialsExecuted": 84,
    "validTrials": 51,
    "datasetHash": "f7e8d9c0..."
  },
  "approvalState": "pending"
}
```

#### Polymorphic Serialization

Steps are serialized using `[JsonDerivedType]` discriminators and a source-generated `JsonSerializerContext` for AOT safety:

```csharp
[JsonDerivedType(typeof(PredictStepConfig), "$type", "predict")]
[JsonDerivedType(typeof(RetrieveStepConfig), "$type", "retrieve")]
[JsonDerivedType(typeof(EvaluateStepConfig), "$type", "evaluate")]
public abstract record StepConfig;
```

> **Implementation Note:** `JsonSerializerContext` eliminates all reflection at serialization time, making artifacts compatible with Native AOT. This is critical for fast cold-start deployments.

#### Hot-Swap with AssemblyLoadContext

```csharp
// Load v2, unload v1 — zero downtime
var alc = new AssemblyLoadContext("artifact-v2", isCollectible: true);
// ... load assembly, resolve types ...
// When done: alc.Unload(); — memory reclaimed by GC
```

#### Auto-Reload with IOptionsMonitor

```csharp
services.Configure<ArtifactOptions>(
    Configuration.GetSection("LMP:Artifact"));

// Runtime watches for file changes via IChangeToken
public class ArtifactWatcher(IOptionsMonitor<ArtifactOptions> monitor)
{
    public ArtifactWatcher()
    {
        monitor.OnChange(opts => ReloadArtifact(opts.Path));
    }
}
```

Artifact hashing uses `XxHash128` for fast provenance checks and `SHA256` for verification.

---

## 3. Data Flow

```
AUTHOR            BUILD             IR              RUNTIME
 C# source ──────> Roslyn ─────> Descriptors ──────> Execute
 attributes        analyzers      records            IChatClient
 LmpProgram        generators     (immutable)        StepContext
                   .g.cs files                       Activities
                                                        │
                                                        ▼
                                          TRACES (InMemoryExporter)
                                                        │
                                                        ▼
                                                   COMPILER
                                              propose candidates
                                              execute trials
                                              evaluate & constrain
                                              select best valid
                                                        │
                                                        ▼
                                                   ARTIFACT
                                              JSON + hash + metrics
                                                        │
                                                        ▼
                                                    DEPLOY
                                              load artifact
                                              apply params
                                              hot-swap via ALC
```

| Transition | Data Crossing | Format |
|-----------|---------------|--------|
| Author → Build | Attributed C# source | Roslyn `SyntaxTree` + `SemanticModel` |
| Build → IR | Generated descriptors | C# `record` instances compiled into assembly |
| IR → Runtime | `ProgramDescriptor`, `SignatureDescriptor` | In-memory immutable objects via DI |
| Runtime → Traces | Step execution telemetry | `Activity` spans + `InMemoryExporter` |
| Traces → Compiler | Trial metrics and constraint inputs | `TrialResultDescriptor` records |
| Compiler → Artifact | Selected variant + metrics + provenance | JSON file on disk |
| Artifact → Deploy | Loaded configuration | Deserialized records applied to runtime via `IOptions` |

---

## 4. Dependency Graph

```
LMP.Abstractions          ← shared contracts, IR types, attributes
  │                          NuGet: (none framework-external)
  │
  ├──> LMP.Roslyn           ← source generators, analyzers, code fixes
  │      NuGet: Microsoft.CodeAnalysis.CSharp
  │
  ├──> LMP.Runtime          ← execution engine, context, tracing
  │      NuGet: Microsoft.Extensions.AI
  │             Microsoft.Extensions.DependencyInjection
  │             System.Threading.Tasks.Dataflow (TPL Dataflow)
  │             System.Diagnostics.DiagnosticSource (OTEL)
  │
  ├──> LMP.Evaluation       ← evaluator abstractions, built-in evaluators
  │      NuGet: Microsoft.Extensions.AI.Evaluation
  │
  └──> LMP.Compiler         ← compile loop, optimizer backends, artifact I/O
         depends on: LMP.Abstractions, LMP.Runtime, LMP.Evaluation
         NuGet: Microsoft.Extensions.Resilience
                Microsoft.Extensions.Caching.Hybrid
                System.Numerics.Tensors
                System.IO.Hashing

LMP.Cli                    ← dotnet tool entry point
  depends on: LMP.Runtime, LMP.Compiler
  NuGet: System.CommandLine
         Microsoft.Extensions.Hosting

LMP.Samples.SupportTriage  ← sample app (public APIs only)
  depends on: LMP.Abstractions (+ LMP.Roslyn as analyzer/generator reference)
```

> **Implementation Note:** `LMP.Abstractions` must contain **zero** Roslyn code — it is the leaf dependency that all other packages reference. `LMP.Roslyn` depends on Abstractions but never on Runtime internals. Sample projects depend only on public APIs so they serve as integration tests for the developer experience.

---

## 5. Cross-Cutting Concerns

### DI Registration

All layers compose via `IServiceCollection`. A single `AddLmp()` extension method wires everything:

```csharp
services.AddLmp(lmp =>
{
    lmp.AddRuntime();
    lmp.AddCompiler(opts => opts.MaxTrials = 50);
    lmp.AddEvaluation();
    lmp.AddKeyedChatClient("triage", chatClient);
});
```

Generated registration helpers (emitted by source generators) call `services.AddSingleton<SignatureDescriptor>(...)` for each discovered signature.

### Configuration

Compile options, model settings, and artifact paths are bound via **`IOptions<T>`** / **`IOptionsMonitor<T>`**:

```csharp
services.Configure<CompileOptions>(config.GetSection("LMP:Compile"));
services.Configure<ArtifactOptions>(config.GetSection("LMP:Artifact"));
```

### Logging

All layers use **`ILogger<T>`** with structured logging. Log messages include step name, variant ID, and correlation IDs so log aggregators can filter by compilation run or runtime execution.

### Observability

```
ActivitySource "LMP.Runtime"    → per-step spans with model/token/cost tags
Meter "LMP.Metrics"             → latency histograms, cost counters
InMemoryExporter                → compiler reads traces in-process
                                  (closed feedback loop)
```

Traces export to any OpenTelemetry-compatible backend (Jaeger, Azure Monitor, Aspire 13.1 GA dashboard at `localhost:18888`).

### C# 13 / .NET 10 Primitives

| Primitive | Usage |
|-----------|-------|
| **`params ReadOnlySpan<T>`** (C# 13) | All variadic APIs: `AddSteps(params ReadOnlySpan<StepDescriptor>)`, `AddConstraints(...)`, `AddEvaluators(...)` — zero-alloc argument passing. |
| **`System.Threading.Lock`** (.NET 10) | All synchronization points use `System.Threading.Lock` instead of `object` for type-safe, optimized locking. |
| **Partial properties** (C# 13) | `static partial SignatureDescriptor Descriptor { get; }` — source generators emit the implementation half. |

---

## 6. Extension Points

| Extension Point | Interface | Purpose |
|----------------|-----------|---------|
| **Custom optimizer** | `IOptimizerBackend` | Plug in AutoML, Bayesian, or learned search strategies. MVP constraints are lambda predicates (`Func<TrialResultDescriptor, bool>`); Z3-assisted constraint solving is an optional advanced backend post-MVP. |
| **Custom retriever** | `IDocumentRetriever` | Swap in any vector store, search index, or document source for Retrieve steps. |
| **Custom evaluator** | `IEvaluator` | Add domain-specific scoring (e.g., PII detection, compliance checks) alongside built-in evaluators. |
| **Custom step types** | *(Future)* `IStepHandler` | Register new step kinds beyond Predict/Retrieve/Evaluate/If/Repair. Not exposed in MVP but the sealed step-kind hierarchy leaves room for extension. |
| **Aspire hosting** | *(Post-MVP)* `LMP.Aspire.Hosting` | `AddLmpCompiler()` integrates the compile loop as an Aspire resource with dashboard telemetry. |

All extension points are registered through DI:

```csharp
services.AddSingleton<IOptimizerBackend, MyBayesianBackend>();
services.AddSingleton<IDocumentRetriever, ElasticSearchRetriever>();
services.AddSingleton<IEvaluator, PiiDetectionEvaluator>();
```

> **Implementation Note:** The `IOptimizerBackend` receives the full search space, objective, constraints, and prior trial history on every call. This means backends can implement stateful strategies (e.g., Bayesian priors updated after each trial) without the compiler needing to know about the strategy internals. Start with the built-in `BoundedRandomBackend` for MVP and swap in richer backends post-MVP.

---

## 7. Convergence 6: Runtime Execution Is Already Solved

TPL Dataflow + `IChatClient` middleware + `params ReadOnlySpan<T>` + `System.Threading.Lock` + `Microsoft.Extensions.Resilience` + `HybridCache` tag-based eviction = a runtime and compile loop built entirely from **production-grade .NET primitives with zero custom infrastructure**.

| Primitive | Replaces |
|-----------|----------|
| **TPL Dataflow** (`TransformBlock`, `ActionBlock`, `BroadcastBlock`, `JoinBlock`) | Custom graph executor |
| **M.E.AI middleware** (`UseOpenTelemetry()`, `UseDistributedCache()`, `UseLogging()`) | Manual OTEL/caching/logging wrapping |
| **`UseLmpStepContext()` + `UseLmpCostTracking()`** | Only two thin LMP-specific middleware layers |
| **`params ReadOnlySpan<T>`** (C# 13) | Heap-allocated `params T[]` for variadic APIs |
| **`System.Threading.Lock`** (.NET 10) | `lock (object)` with weaker type safety |
| **`Microsoft.Extensions.Resilience`** | Raw `TokenBucketRateLimiter` for LM call protection |
| **`HybridCache` tag-based eviction** (.NET 10) | Manual cache invalidation between trials |
| **`TensorPrimitives`** | Manual loops for cosine similarity, sum, average |

**Python comparison:** Each of these requires a separate library, separate configuration, and custom glue code. The .NET runtime provides all of them as composable, tested, supported primitives — the LMP framework layers only domain-specific semantics on top.

---

## 8. Convergence 7: Build Integration Is Already Solved

LMP uses a **Three-Layer Build Architecture** that mirrors how battle-tested .NET frameworks (EF Core, Razor, Blazor, Protobuf) integrate with the `dotnet build` pipeline. Each layer uses a different .NET build primitive for the right job.

### Layer 1: Source Generator (Inside Roslyn — during `dotnet build`)

**When it runs:** Inside the C# compiler (`csc.exe`), during the `CoreCompile` MSBuild target.

**What it does:**
- Discovers `[LmpSignature]` and `[LmpProgram]` attributed types
- Validates signatures: field descriptions, output types, binding completeness
- Emits `SignatureDescriptor`, `ProgramDescriptor`, binding code
- Reports diagnostics as compiler warnings/errors (LMP001–LMP007)

**What it cannot do:**
- Access the filesystem
- Run external tools
- Emit non-C# files (JSON, IR)

**Why this layer exists:** Real-time IDE feedback. Red squiggles appear as you type, before you ever hit "Build."

### Layer 2: MSBuild Targets (After CoreCompile — during `dotnet build`)

**When it runs:** After the C# compiler finishes, before the build completes. Hooks into `AfterCompile` and `PrepareForPublish` MSBuild targets.

**What it does:**

| Target | Trigger | Purpose |
|--------|---------|---------|
| `LmpEmitIr` | `AfterCompile` | Reads compiled assembly, extracts `ProgramDescriptor` instances, emits IR JSON to `obj/lmp/` |
| `LmpValidateGraph` | `AfterCompile` (after `LmpEmitIr`) | Validates IR completeness: all bindings resolved, no dangling step references |
| `LmpEmbedArtifact` | `PrepareForPublish` | During `dotnet publish`, copies compiled artifact to output directory |

**What it cannot do:**
- Call LM APIs (that costs money)
- Run optimization trials (that's non-deterministic)

**Why this layer exists:** `dotnet build` catches ALL structural errors — missing bindings, type mismatches, invalid graphs. Without this layer, developers must run `dotnet lmp compile` (which calls LM APIs and takes minutes) just to discover a typo in a step name.

**Precedent: EF Core** uses the exact same pattern:
- Source: [EF Core MSBuild Targets](https://github.com/dotnet/efcore/blob/main/src/EFCore.Tasks/buildTransitive/Microsoft.EntityFrameworkCore.Tasks.targets)
- Source: [OptimizeDbContext MSBuild Task](https://github.com/dotnet/efcore/blob/main/src/EFCore.Tasks/Tasks/OptimizeDbContext.cs)
- `CoreCompileDependsOn` → `_EFPrepareForCompile` (pre-compile)
- `TargetsTriggeredByCompilation` → `_EFGenerateFilesAfterBuild` (post-compile)

### Layer 3: CLI Tool (Explicit — `dotnet lmp compile`)

**When it runs:** Only when the developer explicitly runs `dotnet lmp compile`. Never implicitly.

**What it does:**
- Runs optimization trials against live LM APIs
- Evaluates candidates against training/dev data
- Selects winning configuration (prompts, models, parameters)
- Emits compiled artifact (JSON)

**Why it's explicit:** This layer calls LM APIs (costs real dollars per trial), is non-deterministic (different runs produce different results), and takes minutes to hours. It must NEVER run during `dotnet build`.

**Precedent: EF Core** separates the same way:
- `dotnet build` → compiles DbContext classes (implicit, free, deterministic)
- `dotnet ef dbcontext optimize` → generates compiled models (explicit, expensive)
- `dotnet ef migrations add` → generates migrations (explicit, human judgment)

### The Three Layers Compared

| | Layer 1: Source Gen | Layer 2: MSBuild Targets | Layer 3: CLI Tool |
|---|---|---|---|
| **Runs during** | `dotnet build` (inside compiler) | `dotnet build` (after compiler) | `dotnet lmp compile` (manual) |
| **Cost** | Free | Free | $$$ (LM API calls) |
| **Deterministic** | Yes | Yes | No |
| **IDE feedback** | Real-time (red squiggles) | Build output (warnings/errors) | Terminal output |
| **Outputs** | `.g.cs` files | IR JSON, validation results | Compiled artifact JSON |
| **Can access filesystem** | No | Yes | Yes |
| **Can call LM APIs** | No | No | Yes |
| **Implementation** | `IIncrementalGenerator` | `Microsoft.Build.Utilities.Task` | `System.CommandLine` |

### MSBuild Integration via NuGet

MSBuild targets ship inside the LMP NuGet package using the standard `buildTransitive/` folder convention. When a project references `LMP.Runtime`, the targets are automatically imported — zero manual configuration.

```
LMP.Runtime.nupkg
├── lib/net10.0/
│   └── LMP.Runtime.dll
├── analyzers/dotnet/cs/
│   └── LMP.SourceGenerators.dll
├── buildTransitive/
│   ├── LMP.Runtime.props          # Default properties
│   └── LMP.Runtime.targets        # LmpEmitIr, LmpValidateGraph, LmpEmbedArtifact
└── tools/net10.0/
    └── LMP.Tasks.dll              # MSBuild task assembly
```

Source: [MSBuild .props and .targets in a package](https://learn.microsoft.com/nuget/concepts/msbuild-props-and-targets)
Source: [Reference an MSBuild Project SDK](https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk)
