# LMP Framework — Public API Specification

> **Derived from:** `spec.org` §5 (Mental Model), §6 (Canonical Developer Experience), §12 (Public API Shape)
>
> **Audience:** Implementing developers. Every type, method, and pattern in this document is implementation-ready.

---

## 1. API Design Philosophy

### "The API Is the Product"

LMP's competitive advantage is _developer experience_. The public API surface is what users touch — it must feel like authoring software contracts, not wrestling with prompt strings. Every public type earns its place by answering a question a developer would actually ask: "How do I define what the LM should do?", "How do I compose multi-step logic?", "How do I optimize?".

### Principles

| Principle | Rationale |
|---|---|
| **Minimize public surface** | Every public type is a maintenance commitment. Internal types can evolve freely; public ones cannot. Expose the smallest set that enables the full authoring → compile → deploy story. |
| **Maximize discoverability** | A developer with IntelliSense and no docs should be able to author a complete program. Attribute names, method names, and parameter names are self-documenting. |
| **Attributes for authoring, fluent builders for configuration** | Signatures and programs are _authored_ — attributes are the natural C# idiom. Compile specs are _configuration_ — fluent builders give chainable, readable setup. |
| **CancellationToken everywhere** | Every async method accepts an optional `CancellationToken`. Non-negotiable for production .NET. |

---

## 2. Package Structure

### LMP.Abstractions

Shared contracts consumed by all other packages. **Zero Roslyn dependency.**

| Visibility | Types |
|---|---|
| **Public** | `LmpSignatureAttribute`, `InputAttribute`, `OutputAttribute`, `BindFromAttribute`, `LmpProgramAttribute`, `LmpProgram<TIn, TOut>`, `ProgramGraph`, `Step` (static factory), `IDocumentRetriever`, `ICompiledArtifactLoader`, `CompiledArtifact` |
| **Internal** | Descriptor record types (`SignatureDescriptor`, `FieldDescriptor`, etc.), IR invariants, hash utilities |

### LMP.Runtime

Execution engine. Depends on `LMP.Abstractions`.

| Visibility | Types |
|---|---|
| **Public** | `IServiceCollection` extension methods (`AddLmpPrograms`), runtime context interfaces |
| **Internal** | Graph executor, step dispatchers, prompt assembler, trace collector, structured-output parser |

### LMP.Compiler

Optimization pipeline. Depends on `LMP.Abstractions`, `LMP.Runtime`, `LMP.Evaluation`.

| Visibility | Types |
|---|---|
| **Public** | `CompileSpec`, `IProgramCompiler`, `CompileReport`, `IOptimizerBackend`, `Metrics` (static factory), `Optimizers` (static factory) |
| **Internal** | Search-space extractor, trial runner, constraint evaluator, candidate ranker, artifact emitter |

### LMP.Evaluation

Scoring integration. Depends on `LMP.Abstractions`.

| Visibility | Types |
|---|---|
| **Public** | Evaluator integration wrappers, `IEvaluator` adapter interface |
| **Internal** | Score aggregation, dataset loaders |

> **Implementation Note:** `LMP.Roslyn` (analyzers + source generators) is a separate package that depends on `LMP.Abstractions` only. It is _not_ part of the public runtime API and is omitted from this document.

---

## 3. Complete Type Reference

### 3.1 Authoring Attributes

#### `LmpSignatureAttribute`

```csharp
namespace LMP;

/// <summary>
/// Marks a partial class as an LM signature — a typed contract
/// defining inputs, outputs, and instructions for a single LM interaction.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LmpSignatureAttribute : Attribute
{
    /// <summary>
    /// Optional human-readable name. Defaults to the class name if omitted.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// System-level instructions sent to the LM. Must not be empty.
    /// Analyzer LMP002 fires if this is missing or blank.
    /// </summary>
    public required string Instructions { get; set; }
}
```

**Usage:**

```csharp
[LmpSignature(Instructions = "Classify the severity of a support ticket.")]
public partial class ClassifyTicket
{
    [Input(Description = "Raw ticket text")]
    public required string TicketText { get; init; }

    [Output(Description = "Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }
}
```

**Why This Exists:** Signatures are the smallest authorable unit. The attribute triggers the source generator to emit a `SignatureDescriptor` at build time, enabling compile-time validation (field descriptions present, output types supported) without any runtime reflection.

---

#### `InputAttribute`

```csharp
namespace LMP;

/// <summary>
/// Marks a property as an input field of an LM signature.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class InputAttribute : Attribute
{
    /// <summary>
    /// Human-readable description included in the LM prompt.
    /// Analyzer LMP001 fires if missing.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Whether the field must be provided at runtime. Defaults to true.
    /// </summary>
    public bool IsRequired { get; set; } = true;
}
```

#### `OutputAttribute`

```csharp
namespace LMP;

/// <summary>
/// Marks a property as an output field of an LM signature.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OutputAttribute : Attribute
{
    /// <summary>
    /// Human-readable description guiding the LM's output.
    /// Analyzer LMP001 fires if missing.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Whether the field must be present in the LM response. Defaults to true.
    /// </summary>
    public bool IsRequired { get; set; } = true;
}
```

**Why This Exists:** Field descriptions are not optional documentation — they become part of the prompt. `IsRequired` informs structured-output parsing: optional fields use lenient deserialization.

---

#### `BindFromAttribute`

```csharp
namespace LMP;

/// <summary>
/// Explicitly binds a step input property to a specific upstream output.
/// Used when convention-based auto-binding (Tier 1) cannot resolve the source,
/// or when the developer wants to override the default convention.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class BindFromAttribute : Attribute
{
    /// <summary>
    /// The binding expression identifying the source.
    /// Uses dot notation: "stepName.PropertyName" (e.g., "retrieve-kb.Documents").
    /// Use "input.PropertyName" to bind from the program input.
    /// </summary>
    public required string Source { get; set; }

    public BindFromAttribute(string source) => Source = source;
}
```

**Usage:**

```csharp
[LmpSignature(Instructions = "Classify the severity of a support ticket.")]
public partial class ClassifyTicket
{
    [Input(Description = "Raw ticket text")]
    [BindFrom("input.TicketText")]
    public required string TicketText { get; init; }

    [Input(Description = "Relevant knowledge base snippets")]
    [BindFrom("retrieve-kb.Documents")]
    public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

    [Output(Description = "Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }
}
```

**Why This Exists:** `[BindFrom]` is Tier 2 of the three-tier binding model. Convention-based auto-binding (Tier 1) matches properties by name and type automatically. When names don't align or the binding is ambiguous, `[BindFrom]` provides explicit, compile-time-verified binding with zero runtime overhead — the source generator emits direct property assignment code. Diagnostic LMP007 fires as an informational hint when an expression-tree binding (Tier 4) is used where a `[BindFrom]` attribute would suffice.

**Three-Tier Binding Model Summary:**

| Tier | Mechanism | Resolution | Overhead |
|------|-----------|-----------|----------|
| Tier 1 | Convention-based auto-binding | Matching names/types between upstream outputs and downstream inputs | Zero — generated code |
| Tier 2 | `[BindFrom]` attribute | Explicit source expression on the property | Zero — generated code |
| Tier 3 | C# 14 interceptor-based lambda binding | Intercepted `bind:` lambdas lowered to generated code at compile time | Zero — generated code |
| Tier 4 | Expression tree binding | `.Compile()` at runtime | Runtime cost — fallback only |

---

#### `LmpProgramAttribute`

```csharp
namespace LMP;

/// <summary>
/// Marks a class as an LM program — a composable, compilable unit of LM logic.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LmpProgramAttribute : Attribute
{
    /// <summary>
    /// Stable program identifier used in artifacts, traces, and CLI commands.
    /// Must be a compile-time constant.
    /// </summary>
    public required string Name { get; set; }

    public LmpProgramAttribute(string name) => Name = name;
}
```

**Usage:**

```csharp
[LmpProgram("support-triage")]
public partial class SupportTriageProgram : LmpProgram<TicketInput, TriageResult>
{
    // ...
}
```

**Why This Exists:** The `Name` string is the stable identity used in compiled artifacts, CLI commands (`dotnet lmp compile --program support-triage`), and runtime traces. Analyzer LMP004 enforces that this is a deterministic compile-time constant.

---

### 3.2 Base Types

#### `LmpProgram<TIn, TOut>`

```csharp
namespace LMP;

/// <summary>
/// Base class for all LM programs. Subclass this to define a
/// directed graph of steps that together solve a business task.
/// </summary>
/// <typeparam name="TIn">Program input record type.</typeparam>
/// <typeparam name="TOut">Program output record type.</typeparam>
public abstract class LmpProgram<TIn, TOut>
{
    /// <summary>
    /// Defines the program's step graph. Called once during initialization.
    /// The returned graph is the authored logical structure that the runtime executes.
    /// </summary>
    public abstract ProgramGraph Build();

    /// <summary>
    /// Executes the program against a live input.
    /// </summary>
    public virtual Task<TOut> RunAsync(TIn input, CancellationToken ct = default);
}
```

**Why This Exists:** The base class gives the framework a single extension point. `Build()` returns a declarative graph — enabling the compiler to inspect, optimize, and rewrite the graph without executing it. `RunAsync` is virtual so the runtime can inject compiled artifact overrides.

> **Implementation Note:** The runtime resolves `LmpProgram<TIn, TOut>` from DI. The generated partial class wires constructor-injected services (e.g., `IChatClient`, `IDocumentRetriever`). Developers never call `new` on a program.

---

#### `ProgramGraph`

```csharp
namespace LMP;

/// <summary>
/// A directed acyclic graph of steps defining control flow and data flow.
/// Constructed via the fluent API returned by <see cref="Graph.StartWith"/>.
/// </summary>
public sealed class ProgramGraph
{
    // Internal: list of steps, edges, return projection
    // Public surface is construction-only via fluent builder.
}

/// <summary>
/// Entry point for building a <see cref="ProgramGraph"/> with fluent syntax.
/// </summary>
public static class Graph
{
    /// <summary>Begins a graph with the given step.</summary>
    public static ProgramGraphBuilder StartWith(StepDefinition step);
}

public sealed class ProgramGraphBuilder
{
    /// <summary>Appends a step that executes after the previous step.</summary>
    public ProgramGraphBuilder Then(StepDefinition step);

    /// <summary>Defines the final output projection from the execution context.</summary>
    public ProgramGraph Return<TOut>(Func<IProgramContext, TOut> projection);
}
```

**Usage** (inside `Build()`):

```csharp
return Graph
    .StartWith(retrieve)
    .Then(triage)
    .Then(groundednessCheck)
    .Return(ctx => new TriageResult
    {
        Severity = ctx.Latest<TriageTicket>().Severity
    });
```

---

#### `Step` (Static Factory)

```csharp
namespace LMP;

/// <summary>
/// Factory methods for creating program step definitions.
/// Each method returns a <see cref="StepDefinition"/> for use in graph building.
/// </summary>
public static class Step
{
    /// <summary>
    /// Creates a predict step that binds input data to a signature and calls the LM.
    /// </summary>
    /// <typeparam name="TSignature">The signature type decorated with [LmpSignature].</typeparam>
    /// <param name="name">Stable step identifier (must be a compile-time constant).</param>
    /// <param name="bind">Projection that builds the signature input from program input and context.</param>
    public static StepDefinition Predict<TSignature>(
        string name,
        Func<dynamic, IProgramContext, TSignature> bind);

    /// <summary>
    /// Creates a retrieval step that queries a document store.
    /// </summary>
    /// <param name="name">Stable step identifier.</param>
    /// <param name="from">Projection that extracts the query string from program input.</param>
    /// <param name="topK">Maximum number of documents to retrieve.</param>
    public static StepDefinition Retrieve(
        string name,
        Func<dynamic, string> from,
        int topK = 5);

    /// <summary>
    /// Creates an evaluation step that scores a prior step's output.
    /// </summary>
    /// <param name="name">Stable step identifier.</param>
    /// <param name="after">The step whose output is evaluated.</param>
    /// <param name="evaluator">The evaluator instance to run.</param>
    public static StepDefinition Evaluate(
        string name,
        StepDefinition after,
        object evaluator);

    /// <summary>
    /// Creates a conditional branch step.
    /// </summary>
    /// <param name="name">Stable step identifier.</param>
    /// <param name="condition">Predicate evaluated at runtime against the context.</param>
    /// <param name="then">Step to execute if the condition is true.</param>
    public static StepDefinition If(
        string name,
        Func<IProgramContext, bool> condition,
        StepDefinition then);

    /// <summary>
    /// Creates a repair step that re-runs a signature with evaluation feedback injected.
    /// </summary>
    /// <typeparam name="TSignature">The same signature type as the original predict step.</typeparam>
    /// <param name="name">Stable step identifier.</param>
    /// <param name="usingFeedbackFrom">Evaluation steps whose feedback is included in the repair prompt.</param>
    public static StepDefinition Repair<TSignature>(
        string name,
        params ReadOnlySpan<StepDefinition> usingFeedbackFrom);
}
```

**Why This Exists:** Static factory methods ensure a single, discoverable entry point for all step types. IntelliSense shows `Step.` and the developer sees every available step kind. Composition over inheritance. The `Repair` method uses `params ReadOnlySpan<StepDefinition>` for zero-allocation variadic calls — callers pass any number of feedback steps without allocating an array on the heap.

---

### 3.3 Compilation Types

#### `CompileSpec`

```csharp
namespace LMP.Compilation;

/// <summary>
/// Fluent builder that defines what to optimize, how to score, and what constraints apply.
/// </summary>
public sealed class CompileSpec
{
    /// <summary>Begins a compile spec targeting a specific program type.</summary>
    public static CompileSpec For<TProgram>() where TProgram : class;

    /// <summary>Path to the JSONL training dataset.</summary>
    public CompileSpec WithTrainingSet(string path);

    /// <summary>Path to the JSONL validation dataset.</summary>
    public CompileSpec WithValidationSet(string path);

    /// <summary>Defines the tunables the optimizer may vary.</summary>
    public CompileSpec Optimize(Action<SearchSpaceBuilder> configure);

    /// <summary>Defines the scoring objective.</summary>
    public CompileSpec ScoreWith(WeightedObjective objective);

    /// <summary>Defines hard constraints that candidates must satisfy.</summary>
    public CompileSpec Constrain(Action<ConstraintBuilder> configure);

    /// <summary>Selects the optimizer backend strategy.</summary>
    public CompileSpec UseOptimizer(IOptimizerBackend backend);
}

public sealed class SearchSpaceBuilder
{
    public void Instructions(string step);
    public void FewShotExamples(string step, int min, int max);
    public void RetrievalTopK(string step, int min, int max);
    public void Model(string step, string[] allowed);
    public void Temperature(string step, double min, double max);
}

public sealed class ConstraintBuilder
{
    /// <summary>
    /// Adds a hard constraint as a strongly-typed predicate.
    /// The predicate receives trial metrics and returns true if the constraint is satisfied.
    /// </summary>
    public void Require(Func<TrialMetrics, bool> predicate, string description);
}
```

**Full usage chain:**

```csharp
var spec = CompileSpec
    .For<SupportTriageProgram>()
    .WithTrainingSet("data/train.jsonl")
    .WithValidationSet("data/val.jsonl")
    .Optimize(s =>
    {
        s.Instructions(step: "triage");
        s.FewShotExamples(step: "triage", min: 0, max: 6);
        s.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]);
        s.Temperature(step: "triage", min: 0.0, max: 0.7);
    })
    .ScoreWith(Metrics.Weighted(
        ("routing_accuracy", 0.35),
        ("severity_accuracy", 0.25),
        ("groundedness", 0.20),
        ("policy_pass_rate", 0.20)))
    .Constrain(rules =>
    {
        rules.Require(m => m.PolicyPassRate == 1.0, "Policy pass rate must be 100%");
        rules.Require(m => m.P95LatencyMs <= 2500, "P95 latency must not exceed 2500ms");
        rules.Require(m => m.AvgCostUsd <= 0.03, "Average cost must not exceed $0.03");
    })
    .UseOptimizer(Optimizers.RandomSearch());
```

> **Implementation Note:** `Metrics.Weighted` accepts `params ReadOnlySpan<(string, double)>` for zero-allocation variadic metric definitions. Internally, metric aggregation (weighted average, sum) uses `System.Numerics.Tensors.TensorPrimitives` for SIMD-accelerated computation — `TensorPrimitives.Sum`, `TensorPrimitives.Average`, and `TensorPrimitives.CosineSimilarity` for embedding-based metrics. This matters during compilation where thousands of trial scores are aggregated.

---

#### `IProgramCompiler`

```csharp
namespace LMP.Compilation;

/// <summary>
/// Runs candidate trials over a program and chooses the best valid variant.
/// </summary>
public interface IProgramCompiler
{
    /// <summary>
    /// Executes the full optimization pipeline: search-space extraction,
    /// candidate proposal, trial execution, constraint enforcement, selection,
    /// and artifact emission.
    /// </summary>
    Task<CompileReport> CompileAsync(
        CompileSpec spec,
        CancellationToken cancellationToken = default);
}
```

---

#### `CompileReport`

```csharp
namespace LMP.Compilation;

/// <summary>
/// The result of a compilation run. Contains all trials, the selected variant,
/// metrics, and constraint evaluation results.
/// </summary>
public sealed record CompileReport
{
    /// <summary>All trials executed during compilation.</summary>
    public required IReadOnlyList<TrialResult> Trials { get; init; }

    /// <summary>The best scoring variant that satisfied all hard constraints, or null if none.</summary>
    public CompiledArtifact? BestArtifact { get; init; }

    /// <summary>Per-trial constraint evaluation results.</summary>
    public required IReadOnlyList<ConstraintResult> ConstraintResults { get; init; }

    /// <summary>True if at least one valid candidate was found and an artifact was emitted.</summary>
    public bool Approved => BestArtifact is not null;
}

public sealed record TrialResult(
    string VariantId,
    double ObjectiveScore,
    IReadOnlyDictionary<string, double> Metrics,
    bool SatisfiesAllConstraints);

public sealed record ConstraintResult(
    string ConstraintExpression,
    string MetricName,
    double ActualValue,
    double Threshold,
    bool Passed);
```

**Why This Exists:** The compile report is the developer's window into what the optimizer tried and why it chose (or rejected) each candidate.

---

#### `IOptimizerBackend`

```csharp
namespace LMP.Compilation;

/// <summary>
/// Proposes candidate parameter configurations. Does NOT own trial execution,
/// evaluation, constraint enforcement, or artifact generation.
/// </summary>
public interface IOptimizerBackend
{
    /// <summary>
    /// Proposes the next candidate given the search space, objectives,
    /// constraints, and all prior trial results.
    /// </summary>
    ValueTask<CandidateProposal> ProposeNextAsync(
        SearchSpaceDescriptor searchSpace,
        ObjectiveDescriptor objective,
        IReadOnlyList<ConstraintDescriptor> constraints,
        IReadOnlyList<TrialResultDescriptor> priorTrials,
        CancellationToken cancellationToken = default);
}
```

> **Implementation Note:** The MVP backend is `Optimizers.RandomSearch()` — a bounded random sampler. This is intentionally simple. The architecture must prove that search-space extraction, trial execution, scoring, constraint enforcement, and artifact emission all work end-to-end before investing in sophisticated search strategies.

---

### 3.4 Artifact Types

#### `ICompiledArtifactLoader`

```csharp
namespace LMP;

/// <summary>
/// Persists and retrieves compiled artifacts.
/// </summary>
public interface ICompiledArtifactLoader
{
    /// <summary>Loads an artifact from the given path and validates compatibility.</summary>
    Task<CompiledArtifact> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>Saves a compiled artifact as AOT-safe JSON.</summary>
    Task SaveAsync(CompiledArtifact artifact, string path, CancellationToken ct = default);
}
```

#### `CompiledArtifact`

```csharp
namespace LMP;

/// <summary>
/// A versioned, serializable representation of an optimized program variant.
/// Produced by compilation, consumed by the runtime to apply selected parameters.
/// </summary>
public sealed record CompiledArtifact
{
    public required string Program { get; init; }
    public required string CompiledVersion { get; init; }
    public required string VariantId { get; init; }
    public required string BaseProgramHash { get; init; }
    public required IReadOnlyDictionary<string, object> SelectedParameters { get; init; }
    public required IReadOnlyDictionary<string, double> ValidationMetrics { get; init; }
    public required bool Approved { get; init; }
}
```

**Why This Exists:** The artifact is the deployable output. It captures _which_ parameters were selected, _why_ (metrics), and _whether_ it passed constraints (approved). The runtime loads the artifact and applies `SelectedParameters` to override base program defaults — enabling hot-swap deployment.

---

### 3.5 Runtime Types

#### `IDocumentRetriever`

```csharp
namespace LMP;

/// <summary>
/// Minimal retrieval abstraction for Retrieve steps.
/// Implement this to connect your vector store, search index, or any document source.
/// </summary>
public interface IDocumentRetriever
{
    /// <summary>
    /// Retrieves the top-K most relevant documents for the given query.
    /// </summary>
    Task<IReadOnlyList<string>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken ct = default);
}
```

**Why This Exists:** The framework must not couple to any specific vector database. This single-method interface is the minimal contract for `Step.Retrieve`. Implementations can wrap Azure AI Search, Qdrant, Pinecone, or a simple in-memory list.

---

## 4. DI Registration Pattern

The framework uses standard `Microsoft.Extensions.DependencyInjection` patterns.

```csharp
using LMP;
using LMP.Compilation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

var services = new ServiceCollection();

// --- Infrastructure ---
services.AddLogging();
services.AddOpenTelemetry();

// --- LM Clients (Keyed DI for per-step model routing) ---
services.AddKeyedSingleton<IChatClient>("triage", (sp, _) =>
    new ChatClientBuilder(
            new OpenAIChatClient("gpt-4.1-mini",
                sp.GetRequiredService<OpenAIClient>()))
        .UseFunctionInvocation()
        .UseOpenTelemetry()
        .Build());

services.AddKeyedSingleton<IChatClient>("repair", (sp, _) =>
    new ChatClientBuilder(
            new OpenAIChatClient("gpt-4.1",
                sp.GetRequiredService<OpenAIClient>()))
        .UseOpenTelemetry()
        .Build());

// --- Document Retrieval ---
services.AddSingleton<IDocumentRetriever, AzureSearchRetriever>();

// --- LMP Programs ---
services.AddLmpPrograms()
    .AddProgram<SupportTriageProgram>();

// --- Compiler (only needed during optimization, not in production) ---
services.AddSingleton<IProgramCompiler, LmpCompiler>();
services.AddSingleton<ICompiledArtifactLoader, JsonArtifactLoader>();

var provider = services.BuildServiceProvider();
```

> **Implementation Note:** `AddLmpPrograms()` is an extension method on `IServiceCollection` that registers the runtime graph executor and trace infrastructure. `AddProgram<T>()` registers the program type as a singleton with its generated metadata wired in.

---

## 5. Canonical Usage Walkthrough

### Step 1: Define the Signature

```csharp
[LmpSignature(
    Instructions = """
    You are a senior enterprise support triage assistant.
    Classify the issue severity, determine the owning team, and draft a
    grounded customer reply using only the provided evidence and policy context.
    If the evidence is insufficient, say so explicitly.
    """)]
public partial class TriageTicket
{
    [Input(Description = "Raw customer issue or support ticket text")]
    public required string TicketText { get; init; }

    [Input(Description = "Customer plan tier such as Free, Pro, Enterprise")]
    public required string AccountTier { get; init; }

    [Input(Description = "Relevant knowledge base snippets")]
    public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

    [Input(Description = "Relevant support or compliance policy snippets")]
    public required IReadOnlyList<string> PolicySnippets { get; init; }

    [Output(Description = "Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }

    [Output(Description = "Owning team name")]
    public required string RouteToTeam { get; init; }

    [Output(Description = "Grounded customer reply draft")]
    public required string DraftReply { get; init; }

    [Output(Description = "Reasoning for severity and routing")]
    public required string Rationale { get; init; }

    [Output(Description = "True if escalation to a human is required")]
    public required bool Escalate { get; init; }
}
```

**What Happens Here:** The `partial` keyword is required. At build time, the source generator emits a companion partial class containing a `static partial SignatureDescriptor Descriptor { get; }` property with all field metadata. The partial property pattern (C# 14) replaces the previous `CreateDescriptor()` static method — it is more idiomatic, discoverable via IntelliSense, and cannot be accidentally shadowed. Analyzers verify every field has a `Description` and output types are supported.

### Step 2: Define the Program

```csharp
[LmpProgram("support-triage")]
public partial class SupportTriageProgram : LmpProgram<TicketInput, TriageResult>
{
    public override ProgramGraph Build()
    {
        var retrieveKb = Step.Retrieve(
            name: "retrieve-kb", from: input => input.TicketText, topK: 5);

        var retrievePolicy = Step.Retrieve(
            name: "retrieve-policy", from: input => input.TicketText, topK: 3);

        var triage = Step.Predict<TriageTicket>(
            name: "triage",
            bind: (input, ctx) => new TriageTicket
            {
                TicketText = input.TicketText,
                AccountTier = input.AccountTier,
                KnowledgeSnippets = ctx.OutputOf(retrieveKb).Documents,
                PolicySnippets = ctx.OutputOf(retrievePolicy).Documents
            });

        var groundedness = Step.Evaluate(
            name: "groundedness-check", after: triage,
            evaluator: new GroundednessEvaluator());

        var policy = Step.Evaluate(
            name: "policy-check", after: triage,
            evaluator: new CustomPolicyEvaluator("support-policy"));

        var repair = Step.If(
            name: "repair-if-needed",
            condition: ctx =>
                ctx.ScoreOf(groundedness) < 0.90 || !ctx.Passed(policy),
            then: Step.Repair<TriageTicket>(
                name: "repair-triage",
                usingFeedbackFrom: [groundedness, policy]));

        return Graph
            .StartWith(retrieveKb)
            .Then(retrievePolicy)
            .Then(triage)
            .Then(groundedness)
            .Then(policy)
            .Then(repair)
            .Return(ctx => new TriageResult
            {
                Severity = ctx.Latest<TriageTicket>().Severity,
                RouteToTeam = ctx.Latest<TriageTicket>().RouteToTeam,
                DraftReply = ctx.Latest<TriageTicket>().DraftReply,
                Escalate = ctx.Latest<TriageTicket>().Escalate,
                GroundednessScore = ctx.ScoreOf(groundedness),
                PolicyPassed = ctx.Passed(policy)
            });
    }
}
```

**What Happens Here:** `Build()` constructs a declarative graph — no LM calls happen here. The framework calls `Build()` once at initialization, inspects the graph for cycles (analyzer LMP006), and caches the topology. `ctx.OutputOf()`, `ctx.ScoreOf()`, and `ctx.Passed()` are evaluated lazily during `RunAsync`.

### Step 3: Register DI and Run

```csharp
var services = new ServiceCollection();
services.AddKeyedSingleton<IChatClient>("triage", (sp, _) =>
    new ChatClientBuilder(new OpenAIChatClient("gpt-4.1-mini",
        sp.GetRequiredService<OpenAIClient>()))
        .UseFunctionInvocation()
        .UseOpenTelemetry()
        .Build());

services.AddSingleton<IDocumentRetriever, AzureSearchRetriever>();
services.AddLmpPrograms().AddProgram<SupportTriageProgram>();

var provider = services.BuildServiceProvider();
var program = provider.GetRequiredService<SupportTriageProgram>();

var result = await program.RunAsync(new TicketInput(
    TicketText: "SSO login intermittently fails for 300+ users in our EU tenant.",
    AccountTier: "Enterprise"));
```

**What Happens Here:** The runtime topologically sorts the graph, executes each step in order, binds data between steps via `IProgramContext`, calls `IChatClient` for predict steps, calls `IDocumentRetriever` for retrieve steps, and assembles the final `TriageResult` from the return projection.

### Step 4: Compile

```csharp
var compiler = provider.GetRequiredService<IProgramCompiler>();

var report = await compiler.CompileAsync(CompileSpec
    .For<SupportTriageProgram>()
    .WithTrainingSet("data/train.jsonl")
    .WithValidationSet("data/val.jsonl")
    .Optimize(s =>
    {
        s.Instructions(step: "triage");
        s.FewShotExamples(step: "triage", min: 0, max: 6);
        s.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]);
    })
    .ScoreWith(Metrics.Weighted(("routing_accuracy", 0.35), ("groundedness", 0.30),
        ("severity_accuracy", 0.20), ("policy_pass_rate", 0.15)))
    .Constrain(rules => rules.Require(m => m.PolicyPassRate == 1.0, "Policy pass rate must be 100%"))
    .UseOptimizer(Optimizers.RandomSearch()));
```

**What Happens Here:** The compiler extracts the search space, asks the optimizer backend for candidates, runs each as a trial against training data, scores with the evaluation pipeline, enforces constraints, ranks valid candidates, and emits a `CompileReport`.

### Step 5: Deploy

```csharp
if (report.Approved)
{
    var loader = provider.GetRequiredService<ICompiledArtifactLoader>();
    await loader.SaveAsync(report.BestArtifact!, "artifacts/support-triage.json");
}
```

At startup in production, load the artifact and the runtime applies the optimized parameters automatically:

```csharp
var loader = provider.GetRequiredService<ICompiledArtifactLoader>();
var artifact = await loader.LoadAsync("artifacts/support-triage.json");
// Runtime applies artifact.SelectedParameters to the program at initialization.
```

---

## 6. Error Handling Patterns

### Exceptions

| Exception | When | Thrown By |
|---|---|---|
| `ArgumentException` | Invalid step name, missing required field, null input | `Step.*` factory methods, `RunAsync` |
| `InvalidOperationException` | Graph cycle detected, duplicate step names, calling `ctx.OutputOf` for a step that hasn't executed | `ProgramGraph` construction, `IProgramContext` |
| `LmpCompilationException` | Compile spec references a step that doesn't exist, dataset file not found | `IProgramCompiler.CompileAsync` |
| `JsonException` | Structured output parsing fails (LM returns malformed JSON) | Runtime predict step executor |
| `OperationCanceledException` | `CancellationToken` is cancelled | All async methods |

### CancellationToken Support

Every async method in the public API accepts an optional `CancellationToken`:

- `LmpProgram<TIn, TOut>.RunAsync(input, ct)`
- `IProgramCompiler.CompileAsync(spec, ct)`
- `ICompiledArtifactLoader.LoadAsync(path, ct)` / `SaveAsync(artifact, path, ct)`
- `IDocumentRetriever.RetrieveAsync(query, topK, ct)`
- `IOptimizerBackend.ProposeNextAsync(..., ct)`

Cancellation propagates through the step graph: if a token is cancelled mid-execution, the current step throws `OperationCanceledException` and no subsequent steps execute.

### Constraint Violations in CompileReport

Constraint violations do **not** throw exceptions. They are data, not errors:

```csharp
var report = await compiler.CompileAsync(spec);

if (!report.Approved)
{
    // No valid candidate found — all violated at least one hard constraint.
    foreach (var violation in report.ConstraintResults.Where(c => !c.Passed))
    {
        Console.WriteLine(
            $"VIOLATION: {violation.ConstraintExpression} — " +
            $"actual {violation.MetricName}={violation.ActualValue}, " +
            $"threshold={violation.Threshold}");
    }
}
```

> **Implementation Note:** When no valid candidate exists, `CompileReport.BestArtifact` is `null` and `Approved` is `false`. The report still contains all `Trials` and `ConstraintResults` so the developer can diagnose _why_ no candidate passed. The compiler must never silently emit an artifact that violates a hard constraint.
