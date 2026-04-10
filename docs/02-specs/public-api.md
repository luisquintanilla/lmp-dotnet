# LMP Framework — Public API Specification

> **Derived from:** `system-architecture.md` (v2 — rewritten from first principles)
>
> **Target:** .NET 10 / C# 14
>
> **Dependency:** `Microsoft.Extensions.AI` (`IChatClient`)
>
> **Audience:** Implementing developers. Every type, method, and pattern in this document is implementation-ready.

---

## 1. API Design Philosophy

### "The API Is the Product"

LMP's competitive advantage is _developer experience_. The public API surface is what users touch — it must feel like authoring software contracts, not wrestling with prompt strings. Every public type earns its place by answering a question a developer would actually ask: "How do I define what the LM should do?", "How do I compose multi-step logic?", "How do I optimize?".

### Principles

| Principle | Rationale |
|---|---|
| **Minimize public surface** | Every public type is a maintenance commitment. Expose the smallest set that enables the full author → optimize → deploy story. |
| **Maximize discoverability** | A developer with IntelliSense and no docs should be able to author a complete program. Attribute names, method names, and parameter names are self-documenting. |
| **Separate `TInput` / `TOutput` types** | Mirrors how `IChatClient.GetResponseAsync<T>()` actually works. Input is messages; `T` is the output type. They are naturally separate — don't fight the platform. |
| **Building blocks, not a framework** | Simple primitives that compose naturally via standard C# types. No directed graphs, no IR, no custom MSBuild SDKs. |
| **CancellationToken everywhere** | Every async method accepts an optional `CancellationToken`. Non-negotiable for production .NET. |

---

## 2. Package Structure

```
LMP.slnx
├── src/
│   ├── LMP.Abstractions/            # Interfaces, attributes, base types (no Roslyn dependency)
│   ├── LMP.Core/                    # Predictor<TIn,TOut>, runtime plumbing
│   ├── LMP.SourceGen/               # Roslyn IIncrementalGenerator (netstandard2.0)
│   ├── LMP.Modules/                 # ChainOfThought, BestOfN, Refine, ReActAgent
│   ├── LMP.Optimizers/              # Evaluator, Bootstrap*, MIPROv2, ISampler impls
│   ├── LMP.Cli/                     # CLI tool (dotnet lmp): inspect, optimize, eval, run
│   ├── LMP.Extensions.Evaluation/   # M.E.AI.Evaluation bridge
│   ├── LMP.Extensions.Z3/           # Z3 constraint-based demo selection
│   └── LMP.Aspire.Hosting/          # Aspire integration
│
├── test/
│   ├── LMP.Abstractions.Tests/
│   ├── LMP.Core.Tests/
│   ├── LMP.SourceGen.Tests/
│   ├── LMP.Modules.Tests/
│   ├── LMP.Optimizers.Tests/
│   ├── LMP.Cli.Tests/
│   ├── LMP.Extensions.Evaluation.Tests/
│   ├── LMP.Extensions.Z3.Tests/
│   └── LMP.Aspire.Hosting.Tests/
│
└── samples/
    └── LMP.Samples.TicketTriage/
```

### LMP.Abstractions

Shared contracts consumed by all other packages. **Zero Roslyn dependency.**

| Visibility | Types |
|---|---|
| **Public** | `LmpSignatureAttribute`, `PredictAttribute`, `LmpModule`, `LmpModule<TInput, TOutput>`, `IPredictor`, `ISampler`, `IOptimizer`, `IRetriever`, `ILmpRunner`, `Example`, `Example<TInput, TLabel>`, `Trace`, `TraceEntry`, `Metric`, `LmpAssert`, `LmpSuggest`, `LmpAssertionException`, `LmpMaxRetriesExceededException`, `ModuleState`, `PredictorState`, `DemoEntry` |
| **Internal** | Descriptor record types, hash utilities |

### LMP.Core

Runtime `Predictor<TInput, TOutput>` class. Depends on `LMP.Abstractions` + `Microsoft.Extensions.AI`.

| Visibility | Types |
|---|---|
| **Public** | `Predictor<TInput, TOutput>` |

### LMP.SourceGen

Roslyn `IIncrementalGenerator` — targets `netstandard2.0`. Ships as an analyzer. Emits `PromptBuilder`, `JsonSerializerContext`, `GetPredictors()`, `CloneCore()`, interceptors, and diagnostics LMP001–LMP003.

### LMP.Modules

Reasoning strategies. Depends on `LMP.Core`.

| Visibility | Types |
|---|---|
| **Public** | `ChainOfThought<TIn, TOut>`, `ChainOfThoughtResult<TOut>`, `BestOfN<TIn, TOut>`, `Refine<TIn, TOut>`, `ReActAgent<TIn, TOut>` |

### LMP.Optimizers

Optimization pipeline. Depends on `LMP.Abstractions`, `LMP.Core`.

| Visibility | Types |
|---|---|
| **Public** | `Evaluator`, `EvaluationResult`, `ExampleResult`, `BootstrapFewShot`, `BootstrapRandomSearch`, `MIPROv2`, `GEPA`, `CategoricalTpeSampler`, `SmacSampler`, `TraceAnalyzer`, `TrialResult`, `ParameterPosterior`, `ParetoFrontier` |

### LMP.Extensions.Evaluation

Bridges `Microsoft.Extensions.AI.Evaluation` into LMP's metric system.

| Visibility | Types |
|---|---|
| **Public** | `EvaluationBridge` |

### LMP.Extensions.Z3

Z3-based constraint optimization for demo selection.

| Visibility | Types |
|---|---|
| **Public** | `Z3ConstrainedDemoSelector` |

> **Implementation Note:** `LMP.SourceGen` contains the source generator (Roslyn `IIncrementalGenerator`). It targets `netstandard2.0` and is referenced as an analyzer by consumer projects. The generator runs automatically at `dotnet build`.

---

## 3. Type Design: Separate TInput / TOutput

This is a deliberate departure from DSPy, where a single `dspy.Signature` class mixes input and output fields. In LMP, input and output are separate C# types.

**Why:** M.E.AI's actual API is `chatClient.GetResponseAsync<T>()` where `T` is the output type and input is `ChatMessage[]`. They are naturally separate. Forcing them into one type fights the platform.

### Input Types — Plain C# Records

Input types require **no LMP attributes**. They are just data. Use standard `[Description]` from `System.ComponentModel` on constructor parameters:

```csharp
public record TicketInput(
    [Description("The raw ticket text")] string TicketText,
    [Description("Customer plan tier")] string AccountTier);
```

No `property:` prefix is needed on positional record parameters. The source generator reads input descriptions from three sources, in priority order:

1. `[Description]` on constructor parameters
2. `[Description]` on properties
3. XML doc comments (`/// <param name="X">...</param>`)

### Output Types — `partial record` with `[LmpSignature]`

```csharp
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}
```

The `partial` keyword is required. At build time, the source generator reads the type and emits:

- **`PromptBuilder<TIn, TOut>`** — assembles `ChatMessage[]` from instructions + demos + input fields
- **`JsonTypeInfo<TOut>`** — zero-reflection JSON serialization via `System.Text.Json` source gen (AOT-safe)
- **`GetPredictors()` on `LmpModule` subclasses** — zero-reflection predictor discovery
- **Diagnostics** — IDE red squiggles for missing descriptions, non-serializable output types

---

## 4. Complete Type Reference — LMP.Abstractions

### 4.1 `LmpSignatureAttribute`

```csharp
namespace LMP;

/// <summary>
/// Marks a partial record as an LM output type — a typed contract
/// defining instructions and output fields for a single LM interaction.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LmpSignatureAttribute(string instructions) : Attribute
{
    /// <summary>
    /// Task-level instructions sent to the LM. Describes what the LM should do.
    /// Analyzer LMP001 fires if this is missing or blank.
    /// </summary>
    public string Instructions { get; } = instructions;
}
```

**Design notes:**

- The constructor takes `instructions` as a positional parameter — `[LmpSignature("...")]` is the canonical usage.
- Placed on `partial record` **output types only**. Input types are plain records with no LMP attributes.
- The source generator reads this at build time; no runtime reflection needed.

---

### 4.2 `IPredictor` (non-generic interface)

Non-generic interface for optimizer enumeration. Exposes learnable state without requiring knowledge of `TInput`/`TOutput` at compile time.

```csharp
namespace LMP;

public interface IPredictor
{
    /// <summary>Predictor name, used in traces and artifact serialization.</summary>
    string Name { get; set; }

    /// <summary>Task instructions. Overridden by optimizers.</summary>
    string Instructions { get; set; }

    /// <summary>Few-shot demos as a non-generic IList. Concrete type is List&lt;(TInput, TOutput)&gt;.</summary>
    IList Demos { get; }

    /// <summary>Configuration overrides (temperature, max tokens, etc.).</summary>
    ChatOptions Config { get; set; }

    /// <summary>Captures learnable state for serialization.</summary>
    PredictorState GetState();

    /// <summary>Restores learnable state from a previously saved state.</summary>
    void LoadState(PredictorState state);

    /// <summary>Adds a demo using untyped input/output. Used by optimizers via Trace entries.</summary>
    void AddDemo(object input, object output);

    /// <summary>Creates an independent copy with separate learnable state.</summary>
    IPredictor Clone();
}
```

---

### 4.3 `Predictor<TInput, TOutput>`

The core primitive. Binds an input type to an output type. Contains learnable state that optimizers fill in. Lives in `LMP.Core`.

```csharp
namespace LMP;

/// <summary>
/// A typed LM call: takes TInput, returns TOutput via structured output.
/// Contains learnable parameters (demos, instructions) that optimizers tune.
/// </summary>
public class Predictor<TInput, TOutput> : IPredictor
    where TOutput : class
{
    /// <summary>Creates a predictor bound to the given chat client.</summary>
    public Predictor(IChatClient client);

    /// <summary>The chat client used for LM calls.</summary>
    public IChatClient Client { get; }

    /// <summary>Predictor name, used in traces and artifact serialization.</summary>
    public string Name { get; set; }

    /// <summary>Task instructions. Defaults to empty. Set by source gen or optimizers.</summary>
    public string Instructions { get; set; }

    /// <summary>
    /// Few-shot demonstration examples. Filled by optimizers.
    /// Each demo is an (input, output) pair included in the prompt.
    /// </summary>
    public List<(TInput Input, TOutput Output)> Demos { get; set; }

    /// <summary>
    /// Predictor-level configuration overrides (temperature, max tokens, etc.).
    /// Uses M.E.AI's ChatOptions — no custom config type.
    /// </summary>
    public ChatOptions Config { get; set; }

    /// <summary>
    /// Optional JSON serializer options. When set by source-generated code,
    /// enables AOT-safe serialization via JsonSerializerContext.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Executes a single LM call: builds prompt from instructions + demos + input,
    /// calls GetResponseAsync&lt;TOutput&gt;(), records trace, returns typed output.
    /// Retries on LmpAssertionException up to maxRetries times with error feedback.
    /// </summary>
    public virtual Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the prompt builder delegate. Called by source-generated interceptor code
    /// to wire type-specific prompt formatting before the first prediction.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void SetPromptBuilder(
        Func<string, TInput, IReadOnlyList<(TInput, TOutput)>?, string?, IList<ChatMessage>> builder);

    /// <summary>
    /// Builds the prompt messages for an LM call. Uses MessageBuilder if set
    /// (source-generated), otherwise falls back to a default implementation.
    /// </summary>
    protected virtual IList<ChatMessage> BuildMessages(TInput input, string? lastError);

    /// <summary>Captures learnable state for serialization.</summary>
    public PredictorState GetState();

    /// <summary>Restores learnable state from a previously saved state.</summary>
    public void LoadState(PredictorState state);

    /// <summary>Adds a demo using untyped input/output. Casts to (TInput, TOutput).</summary>
    public void AddDemo(object input, object output);

    /// <summary>Creates an independent copy with separate Demos, Config, and Instructions.</summary>
    public virtual IPredictor Clone();
}
```

**Usage:**

```csharp
var classifier = new Predictor<TicketInput, ClassifyTicket>(chatClient);

// Learnable state (filled by optimizers or set manually)
classifier.Demos =
[
    (new TicketInput("I was charged twice", "Pro"),
     new ClassifyTicket { Category = "billing", Urgency = 4 })
];
classifier.Config = new ChatOptions { Temperature = 0.7f };

// Predict — with optional trace for optimization and validation for retry
var trace = new Trace();
var result = await classifier.PredictAsync(
    new TicketInput("SSO fails", "Enterprise"),
    trace: trace,
    validate: r => LmpAssert.That(r, c => c.Urgency >= 1 && c.Urgency <= 5));
// result.Category == "technical", result.Urgency == 5
```

**Internals:** The source-generated `PromptBuilder<TInput, TOutput>` assembles `ChatMessage[]` → `IChatClient.GetResponseAsync<TOutput>()` using the source-generated `JsonTypeInfo<TOutput>`. The `SetPromptBuilder()` method is called by interceptor code to wire this before the first prediction. No runtime reflection.

---

### 4.4 `LmpModule`

Abstract base class for composable LM programs. Users override `ForwardAsync()` with their pipeline logic.

```csharp
namespace LMP;

/// <summary>
/// Base class for composable LM programs. Subclass this and override
/// ForwardAsync() to define multi-step LM logic.
/// </summary>
public abstract class LmpModule
{
    /// <summary>
    /// The chat client used by [Predict]-decorated partial methods.
    /// Set this in your constructor before calling any [Predict] methods.
    /// </summary>
    protected IChatClient? Client { get; set; }

    /// <summary>
    /// Active trace for recording predictor invocations during execution.
    /// Set by optimizers before running training examples.
    /// </summary>
    public Trace? Trace { get; set; }

    /// <summary>
    /// Defines the module's execution logic. Override this to compose
    /// predictors, assertions, and other modules.
    /// </summary>
    public abstract Task<object> ForwardAsync(object input, CancellationToken ct = default);

    /// <summary>
    /// Returns all Predictor instances in this module as (name, predictor) pairs.
    /// Source generator emits this — zero-reflection predictor discovery.
    /// </summary>
    public virtual IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [];

    /// <summary>
    /// Creates a deep copy with independent predictor state (Demos, Instructions).
    /// Shares the same IChatClient bindings.
    /// </summary>
    public TModule Clone<TModule>() where TModule : LmpModule;

    /// <summary>Override in source-generated code to clone all predictor fields.</summary>
    protected virtual LmpModule CloneCore();

    /// <summary>
    /// Serializes all learnable parameters to a JSON file. Atomic write (temp → rename).
    /// Uses source-generated JsonSerializerContext — AOT-compatible.
    /// </summary>
    public virtual Task SaveAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Loads learnable parameters from a previously saved JSON file.
    /// Predictors not found in the file are left unchanged.
    /// </summary>
    public virtual Task LoadAsync(string path, CancellationToken ct = default);
}
```

---

### 4.5 `LmpModule<TInput, TOutput>`

Strongly-typed base class for modules. Provides compile-time type safety and bridges to the untyped `ForwardAsync` automatically.

```csharp
namespace LMP;

public abstract class LmpModule<TInput, TOutput> : LmpModule
{
    /// <summary>Typed execution logic. Override this for type-safe composition.</summary>
    public abstract Task<TOutput> ForwardAsync(TInput input, CancellationToken ct = default);

    /// <summary>Sealed bridge: routes untyped calls to the typed overload.</summary>
    public sealed override async Task<object> ForwardAsync(object input, CancellationToken ct = default)
        => (object)(await ForwardAsync((TInput)input, ct))!;
}
```

**Usage:**

```csharp
public partial class TicketTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input, CancellationToken ct = default)
    {
        var classification = await _classify.PredictAsync(input, Trace);
        return await _draft.PredictAsync(classification, Trace);
    }
}
```

**Source generator emits:**

- `GetPredictors()` override that returns `[("_classify", _classify), ("_draft", _draft)]` — no runtime reflection.
- `CloneCore()` for deep cloning with independent predictor state.
- `JsonSerializerContext` for `SaveAsync` / `LoadAsync` — AOT-compatible serialization.

**Design notes:**

- DSPy's `Module.forward()` is the direct inspiration. Python's introspection discovers sub-modules by walking `__dict__`; LMP uses source gen instead.
- `ForwardAsync()` is where control flow lives — standard C# `if`, loops, `try/catch`. No declarative graph needed.
- `LmpModule<TInput, TOutput>` enables typed evaluation: `Evaluator.EvaluateAsync<TInput, TPredicted, TExpected>()` works directly with typed modules.

---

### 4.6 `Example` and `Example<TInput, TLabel>`

Training data types used by optimizers and evaluators. The non-generic `Example` base class enables optimizers to work with any module regardless of concrete types.

```csharp
namespace LMP;

/// <summary>
/// Non-generic base class for training/validation examples.
/// Optimizers and evaluators work with this type to remain agnostic of TInput/TLabel.
/// </summary>
public abstract record Example
{
    /// <summary>Returns the input portion as an untyped object.</summary>
    public abstract object WithInputs();

    /// <summary>Returns the label (ground truth) as an untyped object.</summary>
    public abstract object GetLabel();

    /// <summary>
    /// Loads typed examples from a JSONL file. Each line must be a JSON object
    /// with "input" and "label" properties.
    /// </summary>
    public static IReadOnlyList<Example<TInput, TLabel>> LoadFromJsonl<TInput, TLabel>(
        string path,
        JsonSerializerOptions? options = null);
}

/// <summary>
/// A single training/validation example pairing an input with its expected label.
/// </summary>
public sealed record Example<TInput, TLabel>(TInput Input, TLabel Label) : Example
{
    public override object WithInputs() => Input!;
    public override object GetLabel() => Label!;
}
```

**Usage:**

```csharp
// Construct programmatically
var trainSet = new List<Example<TicketInput, ClassifyTicket>>
{
    new(new TicketInput("I was charged twice", "Pro"),
        new ClassifyTicket { Category = "billing", Urgency = 4 }),
    new(new TicketInput("Can't reset password", "Free"),
        new ClassifyTicket { Category = "account", Urgency = 2 }),
};

// Or load from JSONL
var trainSet = Example.LoadFromJsonl<TicketInput, ClassifyTicket>("train.jsonl");
```

---

### 4.7 `Trace` and `TraceEntry`

Execution record captured during `PredictAsync` calls. Thread-safe — concurrent predictor calls (e.g., BestOfN) can record simultaneously.

```csharp
namespace LMP;

/// <summary>
/// Records predictor invocations during a ForwardAsync call.
/// Optimizers collect traces from successful examples and use them as few-shot demos.
/// </summary>
public sealed class Trace
{
    /// <summary>All recorded trace entries in invocation order.</summary>
    public IReadOnlyList<TraceEntry> Entries { get; }

    /// <summary>Records a predictor invocation with its input and output.</summary>
    public void Record(string predictorName, object input, object output);
}

/// <summary>A single predictor invocation record.</summary>
public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);
```

**How it's used:** During optimization, the module's `Trace` property is set before calling `ForwardAsync`. Each `PredictAsync` call records its input/output into the trace. When the metric passes, successful trace entries become few-shot `Demos` on the relevant predictors.

---

### 4.8 `LmpAssert` / `LmpSuggest`

Runtime assertions that integrate with the LM retry loop.

```csharp
namespace LMP;

/// <summary>
/// Hard assertion with retry/backtrack. If the predicate fails, throws
/// LmpAssertionException which triggers the predictor's retry loop.
/// </summary>
public static class LmpAssert
{
    /// <summary>
    /// Asserts that the result satisfies the predicate.
    /// Throws LmpAssertionException on failure, triggering retry.
    /// </summary>
    public static void That<T>(T result, Func<T, bool> predicate, string? message = null);
}

/// <summary>
/// Soft assertion — returns a boolean but never throws.
/// Useful for quality guardrails that shouldn't block execution.
/// </summary>
public static class LmpSuggest
{
    /// <summary>
    /// Suggests that the result should satisfy the predicate.
    /// Returns false on failure but never throws.
    /// </summary>
    public static bool That<T>(T result, Func<T, bool> predicate, string? message = null);
}

/// <summary>Thrown when LmpAssert.That fails.</summary>
public class LmpAssertionException : Exception
{
    public LmpAssertionException(string message, object? failedResult);
    public object? FailedResult { get; }
}

/// <summary>Thrown when a predictor exhausts its retry budget.</summary>
public class LmpMaxRetriesExceededException : Exception
{
    public LmpMaxRetriesExceededException(string predictorName, int maxRetries);
    public string PredictorName { get; }
    public int MaxRetries { get; }
}
```

**Usage:**

```csharp
var classification = await _classify.PredictAsync(input);
LmpAssert.That(classification, c => c.Urgency >= 1 && c.Urgency <= 5,
    "Urgency must be between 1 and 5");
LmpSuggest.That(classification, c => c.Category != "unknown",
    "Category should not be unknown");
```

**Design notes:**

- `LmpAssert` triggers retry/backtrack — the failed assertion message is injected into the next LM call so the model can self-correct. Mirrors DSPy's `dspy.Assert`.
- `LmpSuggest` is fire-and-forget — useful for telemetry and quality monitoring without blocking the pipeline. Mirrors DSPy's `dspy.Suggest`.

---

### 4.9 `IRetriever`

Minimal RAG interface. Users bring their own implementation via DI.

```csharp
namespace LMP;

/// <summary>
/// Retrieval abstraction for RAG pipelines. Implement this to connect
/// your vector store, search index, or any document source.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Retrieves the top-K most relevant passages for the given query.
    /// </summary>
    Task<string[]> RetrieveAsync(
        string query,
        int k = 5,
        CancellationToken cancellationToken = default);
}
```

**Usage (in a module):**

```csharp
public record QaInput(
    [Description("The user's question")] string Question,
    [Description("Retrieved context passages")] string Context);

[LmpSignature("Answer the question using only the provided context")]
public partial record QaOutput
{
    [Description("The answer to the question")]
    public required string Answer { get; init; }
}

public class RagQaModule : LmpModule
{
    private readonly IRetriever _retriever;
    private readonly Predictor<QaInput, QaOutput> _answer;

    public RagQaModule(IRetriever retriever, IChatClient client)
    {
        _retriever = retriever;
        _answer = new Predictor<QaInput, QaOutput>(client);
    }

    public async Task<QaOutput> ForwardAsync(QaInput input)
    {
        var passages = await _retriever.RetrieveAsync(input.Question, k: 5);
        return await _answer.PredictAsync(input with
        {
            Context = string.Join("\n\n", passages)
        });
    }
}
```

**Design notes:**

- Single-method interface — the minimal contract for retrieval. Implementations can wrap Azure AI Search, Qdrant, Pinecone, or a simple in-memory list.
- Returns `string[]` not `IReadOnlyList<string>` — simpler, sufficient for the common case.

---

### 4.10 `IOptimizer`

All optimizers implement this interface. Returns the same module type with parameters filled in.

```csharp
namespace LMP;

/// <summary>
/// Compiles (optimizes) a module by running it against training data,
/// scoring with a metric, and filling in learnable parameters.
/// </summary>
public interface IOptimizer
{
    /// <summary>
    /// Optimizes the module's learnable parameters (demos, instructions)
    /// using the provided training set and metric function.
    /// Returns the same module with parameters filled in.
    /// </summary>
    Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

**Design notes:**

- Named `CompileAsync` (not `OptimizeAsync`) to align with DSPy's terminology where optimization is called "compiling".
- Takes non-generic `Example` and `Func<Example, object, float>` — optimizers iterate `GetPredictors()` which returns `IPredictor` (non-generic). Use `Metric.Create<TPredicted, TExpected>()` to build typed metric functions.
- Returns `TModule` — the same module type with its predictor `Demos` and `Instructions` filled in. No wrapper types.

---

### 4.11 `ISampler`

Bayesian hyperparameter sampling interface. Proposes configurations and learns from evaluation results.

```csharp
namespace LMP;

/// <summary>
/// Proposes and updates categorical hyperparameter configurations for Bayesian
/// optimization. Implementations maintain trial history and guide subsequent
/// proposals toward higher-scoring regions. Mirrors ML.NET AutoML's ITuner pattern.
/// </summary>
public interface ISampler
{
    /// <summary>Number of completed trials recorded so far.</summary>
    int TrialCount { get; }

    /// <summary>
    /// Proposes a new configuration: parameter name → selected category index.
    /// </summary>
    Dictionary<string, int> Propose();

    /// <summary>
    /// Reports the result of evaluating a proposed configuration.
    /// </summary>
    void Update(Dictionary<string, int> config, float score);
}
```

**Design notes:**

- Each parameter is a named categorical variable with a fixed number of choices (e.g., `{ "classify_instr" → 5, "classify_demos" → 4 }`).
- Implementations include `CategoricalTpeSampler` (Tree-structured Parzen Estimator) and `SmacSampler` (Sequential Model-based Algorithm Configuration with random forests).
- The `Propose` → `Update` loop runs inside `MIPROv2.CompileAsync` for Bayesian optimization over instruction and demo-set combinations.

---

### 4.12 `Metric`

Factory for creating typed metric functions that bridge to the untyped signatures used by optimizers.

```csharp
namespace LMP;

public static class Metric
{
    // ── Synchronous metrics ──

    /// <summary>
    /// Creates a metric from a typed scoring function (predicted, expected) → float.
    /// TPredicted and TExpected may differ (module output vs. ground truth label).
    /// </summary>
    public static Func<Example, object, float> Create<TPredicted, TExpected>(
        Func<TPredicted, TExpected, float> metric);

    /// <summary>
    /// Creates a metric from a typed predicate where true → 1.0f, false → 0.0f.
    /// </summary>
    public static Func<Example, object, float> Create<TPredicted, TExpected>(
        Func<TPredicted, TExpected, bool> metric);

    // ── Asynchronous metrics (LLM-as-judge, SemanticF1, etc.) ──

    /// <summary>Creates an async metric from a typed async scoring function.</summary>
    public static Func<Example, object, Task<float>> CreateAsync<TPredicted, TExpected>(
        Func<TPredicted, TExpected, Task<float>> metric);

    /// <summary>Creates an async metric from a typed async predicate.</summary>
    public static Func<Example, object, Task<float>> CreateAsync<TPredicted, TExpected>(
        Func<TPredicted, TExpected, Task<bool>> metric);
}
```

**Usage:**

```csharp
// Same types — compiler infers both type params:
var m1 = Metric.Create((ClassifyTicket predicted, ClassifyTicket expected) =>
    predicted.Category == expected.Category ? 1f : 0f);

// Bool predicate (true → 1.0, false → 0.0):
var m2 = Metric.Create((ClassifyTicket p, ClassifyTicket e) => p.Category == e.Category);

// Async LLM-as-judge:
var m3 = Metric.CreateAsync<DraftReply, DraftReply>(async (predicted, expected) =>
{
    var result = await judgeClient.GetResponseAsync<JudgeScore>(
        $"Rate this reply: {predicted.ReplyText}");
    return result.Result!.Score / 5f;
});
```

---

### 4.13 `PredictAttribute`

Marks partial methods on `LmpModule` subclasses for source-generated predictor wiring.

```csharp
namespace LMP;

/// <summary>
/// Marks a partial method for source-generated predictor wiring.
/// The source generator emits a backing Predictor field and method body.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PredictAttribute : Attribute;
```

**Usage:**

```csharp
public partial class TicketTriageModule : LmpModule
{
    public TicketTriageModule(IChatClient client) { Client = client; }

    [Predict]
    public partial Task<ClassifyTicket> ClassifyAsync(TicketInput input);

    [Predict]
    public partial Task<DraftReply> DraftAsync(ClassifyTicket classification);

    public override async Task<object> ForwardAsync(object input, CancellationToken ct = default)
    {
        var ticket = (TicketInput)input;
        var classification = await ClassifyAsync(ticket);
        return await DraftAsync(classification);
    }
}
```

---

### 4.14 `ILmpRunner`

CLI entry point interface for `dotnet lmp` tool.

```csharp
namespace LMP;

/// <summary>
/// Entry point for CLI-driven optimization and evaluation.
/// Implement this and the CLI discovers it via reflection.
/// </summary>
public interface ILmpRunner
{
    LmpModule CreateModule();
    Func<Example, object, float> CreateMetric();
    IReadOnlyList<Example> LoadDataset(string path);

    /// <summary>Deserializes JSON into the module's input type. Used by 'dotnet lmp run'.</summary>
    object DeserializeInput(string json) =>
        throw new NotSupportedException("Implement this to use 'dotnet lmp run'.");
}
```

---

### 4.15 Serialization State Types

Types for artifact save/load (`SaveAsync` / `LoadAsync`).

```csharp
namespace LMP;

public sealed record ModuleState
{
    public required string Version { get; init; }
    public required string Module { get; init; }
    public required Dictionary<string, PredictorState> Predictors { get; init; }
}

public sealed record PredictorState
{
    public required string Instructions { get; init; }
    public required List<DemoEntry> Demos { get; init; }
    public Dictionary<string, JsonElement>? Config { get; init; }
}

public sealed record DemoEntry
{
    public required Dictionary<string, JsonElement> Input { get; init; }
    public required Dictionary<string, JsonElement> Output { get; init; }
}
```

---

## 5. Complete Type Reference — LMP.Modules

Thin wrappers around `Predictor<TIn, TOut>`. Each is under 100 lines of code. These are reasoning strategies — they change _how_ the LM is called, not _what_ it's called with.

### 5.1 `ChainOfThought<TIn, TOut>`

Extends `TOut` with a `Reasoning` field. The source generator creates an extended output record at build time so the LM produces step-by-step reasoning before the final answer.

```csharp
namespace LMP.Modules;

/// <summary>
/// Chain-of-thought prompting: extends the output type with a Reasoning field
/// so the LM "thinks out loud" before answering.
/// </summary>
public class ChainOfThought<TIn, TOut> : Predictor<TIn, TOut>
    where TOut : class
{
    public ChainOfThought(IChatClient client);

    // PredictAsync inherited — returns TOut with Reasoning field populated
}
```

**Usage:**

```csharp
var cot = new ChainOfThought<TicketInput, ClassifyTicket>(chatClient);
var result = await cot.PredictAsync(input);
// result has .Category + .Urgency
// The LM generated step-by-step reasoning internally before producing the answer
```

**How it works:** The source generator detects that the predictor is `ChainOfThought` and emits an extended output type that prepends a `Reasoning` string field. The prompt builder instructs the LM to produce reasoning first, then the actual output fields.

*Inspired by:* [`dspy/predict/chain_of_thought.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/predict/chain_of_thought.py) (49 LOC in DSPy).

---

### 5.2 `BestOfN<TIn, TOut>`

Runs N parallel predictions via `Task.WhenAll`, returns the best by a reward function.

```csharp
namespace LMP.Modules;

/// <summary>
/// Parallel N-way prediction with reward-based selection.
/// True parallelism — no GIL. Each candidate runs on its own thread.
/// </summary>
public class BestOfN<TIn, TOut> : Predictor<TIn, TOut>
    where TOut : class
{
    /// <param name="client">The chat client to use for predictions.</param>
    /// <param name="n">Number of parallel candidates to generate.</param>
    /// <param name="reward">Scoring function — higher is better.</param>
    public BestOfN(IChatClient client, int n, Func<TIn, TOut, float> reward);
}
```

**Usage:**

```csharp
var best = new BestOfN<TicketInput, ClassifyTicket>(chatClient, n: 5,
    reward: (input, output) => output.Urgency >= 1 && output.Urgency <= 5 ? 1f : 0f);
var result = await best.PredictAsync(input);
```

**How it works:** `PredictAsync` fires N concurrent `IChatClient` calls via `Task.WhenAll`, evaluates each result with the reward function, and returns the highest-scoring output. True parallelism — no Python GIL.

---

### 5.3 `Refine<TIn, TOut>`

Iterative improvement: predict → LM-generated critique → predict again with critique context.

```csharp
namespace LMP.Modules;

/// <summary>
/// Sequential refinement: predict, critique, re-predict with critique feedback.
/// Runs for a configurable number of iterations.
/// </summary>
public class Refine<TIn, TOut> : Predictor<TIn, TOut>
    where TOut : class
{
    /// <param name="client">The chat client to use.</param>
    /// <param name="maxIterations">Maximum predict-critique cycles (default: 2).</param>
    public Refine(IChatClient client, int maxIterations = 2);
}
```

**Usage:**

```csharp
var refiner = new Refine<TicketInput, DraftReply>(chatClient, maxIterations: 3);
var result = await refiner.PredictAsync(input);
// Result has been iteratively improved via LM self-critique
```

---

### 5.4 `ReActAgent<TIn, TOut>`

Think → Act → Observe loop using M.E.AI's `AIFunction` for tools. No custom tool abstraction needed.

```csharp
namespace LMP.Modules;

/// <summary>
/// ReAct agent: interleaves reasoning (Think) with tool calls (Act)
/// and observation of results (Observe) until the final answer.
/// Uses M.E.AI's AIFunction and FunctionInvokingChatClient — zero new abstractions.
/// </summary>
public class ReActAgent<TIn, TOut> : Predictor<TIn, TOut>
    where TOut : class
{
    /// <param name="client">The chat client (should include FunctionInvokingChatClient middleware).</param>
    /// <param name="tools">Available tools as AIFunction instances.</param>
    /// <param name="maxSteps">Maximum Think→Act→Observe iterations (default: 5).</param>
    public ReActAgent(IChatClient client, IEnumerable<AIFunction> tools, int maxSteps = 5);
}
```

**Usage:**

```csharp
var tools = new[]
{
    AIFunctionFactory.Create(SearchKnowledgeBase),
    AIFunctionFactory.Create(GetAccountInfo)
};
var agent = new ReActAgent<TicketInput, TriageResult>(chatClient, tools);
var result = await agent.PredictAsync(input);
```

**Design notes:**

- Tools are M.E.AI's `AIFunction` / `AIFunctionFactory` — already exists in the platform, zero new abstractions.
- `FunctionInvokingChatClient` handles tool dispatch. The agent just manages the Think → Act → Observe loop.

*Inspired by:* [`dspy/predict/react.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/predict/react.py) (345 LOC in DSPy).

---

## 6. Complete Type Reference — LMP.Optimizers

This is DSPy's core insight brought to .NET: LM programs should be optimized programmatically, not manually tuned.

### 6.1 `Evaluator`

Runs a module on a dev set, scores with a metric function, returns aggregate results.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Evaluates a module against a dataset using a metric function.
/// Uses Parallel.ForEachAsync for concurrent evaluation.
/// Uses TensorPrimitives for aggregate statistics (Average, Min, Max).
/// </summary>
public static class Evaluator
{
    // ── Untyped overloads (work with any LmpModule) ──

    /// <summary>Evaluates with a synchronous metric, returning aggregate results.</summary>
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;

    /// <summary>Evaluates with an async metric (LLM-as-judge).</summary>
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, Task<float>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;

    // ── Typed overloads (work with LmpModule<TInput, TOutput>) ──

    /// <summary>Typed evaluation with float metric.</summary>
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    /// <summary>Typed evaluation with bool metric (true → 1.0, false → 0.0).</summary>
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, bool> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    // ── Typed async overloads ──

    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, Task<float>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);

    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, Task<bool>> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);
}

/// <summary>Aggregate result of evaluating a module against a dataset.</summary>
public sealed record EvaluationResult(
    IReadOnlyList<ExampleResult> PerExample,
    float AverageScore,
    float MinScore,
    float MaxScore,
    int Count);

/// <summary>Result for a single evaluated example.</summary>
public sealed record ExampleResult(Example Example, object Output, float Score);
```

**Usage:**

```csharp
// Untyped (works with any LmpModule):
var result = await Evaluator.EvaluateAsync(
    module, devSet,
    Metric.Create((ClassifyTicket p, ClassifyTicket e) => p.Category == e.Category));
Console.WriteLine($"Accuracy: {result.AverageScore:P1}"); // "Accuracy: 87.0%"
Console.WriteLine($"Min: {result.MinScore}, Max: {result.MaxScore}, Count: {result.Count}");

// Typed (works with LmpModule<TInput, TOutput>):
var result = await Evaluator.EvaluateAsync(
    typedModule, devSet,
    (ClassifyTicket predicted, ClassifyTicket expected) =>
        predicted.Category == expected.Category);
```

**Design notes:**

- Returns `EvaluationResult` with per-example scores, average, min, max, and count — not just a float.
- Uses `TensorPrimitives.Average`, `.Min`, `.Max` for SIMD-accelerated aggregation.
- `maxConcurrency` defaults to 4 for I/O-bound LM calls.

---

### 6.2 `BootstrapFewShot`

The core "compile" step. Runs a teacher module on the training set, collects successful traces (where the metric passes), and fills `predictor.Demos` with those traces as few-shot examples.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Bootstraps few-shot examples by running a teacher module on training data.
/// Successful traces (metric ≥ threshold) become demos on each predictor.
/// </summary>
public sealed class BootstrapFewShot : IOptimizer
{
    /// <param name="maxDemos">Maximum demos per predictor (default: 4).</param>
    /// <param name="maxRounds">Number of bootstrap rounds (default: 1).</param>
    /// <param name="metricThreshold">Minimum score for a trace to become a demo (default: 1.0).</param>
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

**Usage:**

```csharp
var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 0.8f);
var optimized = await optimizer.CompileAsync(module, trainSet,
    Metric.Create((ClassifyTicket p, ClassifyTicket e) => p.Category == e.Category));
// optimized module now has few-shot demos filled in each predictor
```

*Inspired by:* [`dspy/teleprompt/bootstrap.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/teleprompt/bootstrap.py) (~250 LOC in DSPy).

---

### 6.3 `BootstrapRandomSearch`

`BootstrapFewShot` × N candidates. Evaluates each on a held-out validation split and returns the best.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Runs BootstrapFewShot N times with different random shuffles,
/// evaluates each on a validation split, returns the best.
/// </summary>
public sealed class BootstrapRandomSearch : IOptimizer
{
    /// <param name="numTrials">Number of bootstrap trials (default: 8).</param>
    /// <param name="maxDemos">Maximum demos per predictor in each trial (default: 4).</param>
    /// <param name="metricThreshold">Minimum score for trace → demo (default: 1.0).</param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
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

**Usage:**

```csharp
var optimizer = new BootstrapRandomSearch(numTrials: 8, seed: 42);
var best = await optimizer.CompileAsync(module, trainSet,
    Metric.Create((ClassifyTicket p, ClassifyTicket e) => p.Category == e.Category));
```

---

### 6.4 `MIPROv2`

Bayesian optimization over both instructions and demo set selection. Three phases: bootstrap demos, propose instruction variants via LM, then search over (instruction × demo-set) per predictor using TPE.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Bayesian optimization over instructions and demos via TPE.
/// </summary>
public sealed class MIPROv2 : IOptimizer
{
    /// <param name="proposalClient">Chat client for generating instruction candidates.</param>
    /// <param name="samplerFactory">
    /// Optional ISampler factory. Defaults to CategoricalTpeSampler.
    /// </param>
    /// <param name="numTrials">Number of Bayesian search trials (default: 20).</param>
    /// <param name="numInstructionCandidates">Instruction variants per predictor (default: 5).</param>
    /// <param name="numDemoSubsets">Random demo subsets per predictor (default: 5).</param>
    /// <param name="maxDemos">Max demos per predictor in each subset (default: 4).</param>
    /// <param name="metricThreshold">Minimum score for bootstrap demos (default: 1.0).</param>
    /// <param name="gamma">TPE quantile threshold (0, 1). Default: 0.25.</param>
    /// <param name="seed">Optional random seed.</param>
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

    /// <summary>Trial history from the last CompileAsync call. Useful with TraceAnalyzer.</summary>
    public IReadOnlyList<TrialResult>? LastTrialHistory { get; }

    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

**Usage:**

```csharp
var optimizer = new MIPROv2(
    proposalClient: chatClient,
    numTrials: 20,
    numInstructionCandidates: 5);
var optimized = await optimizer.CompileAsync(module, trainSet,
    Metric.Create((ClassifyTicket p, ClassifyTicket e) => p.Category == e.Category));

// Analyze search results
if (optimizer.LastTrialHistory is { } history)
{
    var posteriors = TraceAnalyzer.ComputePosteriors(history);
    // ... inspect parameter importance
}
```

---

### 6.5 `GEPA` (Greedy Evolutionary Pareto-front Assembler)

Multi-objective optimizer that assembles Pareto-optimal predictor configurations across multiple metrics or cost dimensions.

```csharp
namespace LMP.Optimizers;

public sealed class GEPA : IOptimizer
{
    public Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

---

### 6.6 `CategoricalTpeSampler`

Tree-structured Parzen Estimator for categorical hyperparameters.

```csharp
namespace LMP.Optimizers;

public sealed class CategoricalTpeSampler : ISampler
{
    public CategoricalTpeSampler(
        Dictionary<string, int> cardinalities,
        double gamma = 0.25,
        int? seed = null);

    public int TrialCount { get; }
    public Dictionary<string, int> Propose();
    public void Update(Dictionary<string, int> config, float score);
}
```

---

### 6.7 `SmacSampler`

Sequential Model-based Algorithm Configuration using random forests with Expected Improvement acquisition.

```csharp
namespace LMP.Optimizers;

public sealed class SmacSampler : ISampler
{
    public SmacSampler(
        Dictionary<string, int> cardinalities,
        int? seed = null);

    public int TrialCount { get; }
    public Dictionary<string, int> Propose();
    public void Update(Dictionary<string, int> config, float score);
}
```

---

### 6.8 `TraceAnalyzer`

Post-optimization analysis of trial history. Computes per-parameter posteriors and detects interactions.

```csharp
namespace LMP.Optimizers;

/// <summary>A single trial's config and score.</summary>
public sealed record TrialResult(Dictionary<string, int> Config, float Score);

/// <summary>Posterior statistics for a single parameter value.</summary>
public sealed record ParameterPosterior(double Mean, double StandardError, int Count);

public static class TraceAnalyzer
{
    /// <summary>
    /// Computes posterior mean and SE for each parameter value across trial history.
    /// </summary>
    public static Dictionary<string, Dictionary<int, ParameterPosterior>> ComputePosteriors(
        IReadOnlyList<TrialResult> trialHistory);

    /// <summary>Detects pairwise parameter interactions via ANOVA-style analysis.</summary>
    public static Dictionary<(string, string), double> DetectInteractions(
        IReadOnlyList<TrialResult> trialHistory);

    /// <summary>Warm-starts a sampler from trial history.</summary>
    public static void WarmStart(
        ISampler sampler,
        IReadOnlyList<TrialResult> trialHistory);
}
```

---

### 6.9 `EvaluationBridge` (LMP.Extensions.Evaluation)

Bridges `Microsoft.Extensions.AI.Evaluation` evaluators into LMP's metric system.

```csharp
namespace LMP.Extensions.Evaluation;

public static class EvaluationBridge
{
    /// <summary>
    /// Creates an LMP async metric from an M.E.AI.Evaluation IEvaluator.
    /// Normalizes scores to [0, 1] by dividing by maxScore.
    /// </summary>
    public static Func<Example, object, Task<float>> CreateMetric(
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration,
        string metricName,
        float maxScore = 5.0f);

    /// <summary>Typed variant using Metric.CreateAsync under the hood.</summary>
    public static Func<Example, object, Task<float>> CreateTypedMetric<TPredicted, TExpected>(
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration,
        string metricName,
        float maxScore = 5.0f);

    /// <summary>
    /// Runs multiple evaluators and returns weighted average score.
    /// </summary>
    public static Func<Example, object, Task<float>> CreateCombinedMetric(
        IEnumerable<(IEvaluator Evaluator, string MetricName, float Weight)> evaluators,
        ChatConfiguration? chatConfiguration,
        float maxScore = 5.0f);
}
```

**Usage:**

```csharp
var coherenceMetric = EvaluationBridge.CreateMetric(
    new CoherenceEvaluator(),
    chatConfiguration,
    "Coherence",
    maxScore: 5.0f);

var result = await Evaluator.EvaluateAsync(module, devSet, coherenceMetric);
```

---

## 7. Error Handling

### Exceptions

| Exception | When | Thrown By |
|---|---|---|
| `ArgumentException` | Null input, invalid configuration | `Predictor.PredictAsync`, constructors |
| `InvalidOperationException` | Module state error (e.g., structured output returned null) | `LmpModule`, `Predictor` |
| `JsonException` | Structured output parsing fails (LM returns malformed JSON) | `Predictor.PredictAsync` |
| `LmpAssertionException` | `LmpAssert.That` predicate fails | `LmpAssert.That` |
| `LmpMaxRetriesExceededException` | All retry attempts exhausted | `Predictor.PredictAsync` |
| `OperationCanceledException` | `CancellationToken` is cancelled | All async methods |

### CancellationToken Support

Every async method in the public API accepts an optional `CancellationToken`:

- `Predictor<TIn, TOut>.PredictAsync(input, trace, validate, maxRetries, ct)`
- `LmpModule.ForwardAsync(input, ct)`
- `LmpModule.SaveAsync(path, ct)` / `LoadAsync(path, ct)`
- `IRetriever.RetrieveAsync(query, k, ct)`
- `IOptimizer.CompileAsync(module, trainSet, metric, ct)`
- `Evaluator.EvaluateAsync(module, devSet, metric, maxConcurrency, ct)`

Cancellation propagates through modules: if a token is cancelled mid-execution, the current predictor throws `OperationCanceledException` and no subsequent steps execute.

---

## 8. End-to-End Example: Ticket Triage

```csharp
using System.ComponentModel;
using LMP;
using LMP.Modules;
using LMP.Optimizers;
using Microsoft.Extensions.AI;

// ========================================
// Step 1: Define input and output types
// ========================================

// Input — plain C# record, no LMP attributes
public record TicketInput(
    [Description("The raw ticket text")] string TicketText,
    [Description("Customer plan tier")] string AccountTier);

// Output — partial record with [LmpSignature]
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

[LmpSignature("Draft a helpful reply to the customer")]
public partial record DraftReply
{
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}

// ========================================
// Step 2: Define the module
// ========================================

public partial class TicketTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new ChainOfThought<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input, CancellationToken ct = default)
    {
        var classification = await _classify.PredictAsync(input, Trace);
        LmpAssert.That(classification, c => c.Urgency >= 1 && c.Urgency <= 5);
        LmpSuggest.That(classification, c => c.Category != "unknown");
        return await _draft.PredictAsync(classification, Trace);
    }
}

// ========================================
// Step 3: Create client and module
// ========================================

var client = new ChatClientBuilder(new OpenAIChatClient("gpt-4o-mini"))
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

var module = new TicketTriageModule(client);

// ========================================
// Step 4: Load training data
// ========================================

var trainSet = Example.LoadFromJsonl<TicketInput, ClassifyTicket>("train.jsonl");
var devSet   = Example.LoadFromJsonl<TicketInput, ClassifyTicket>("dev.jsonl");

// ========================================
// Step 5: Optimize — bootstrap few-shot demos
// ========================================

var metric = Metric.Create((ClassifyTicket predicted, ClassifyTicket expected) =>
    predicted.Category == expected.Category ? 1f : 0f);

var optimizer = new BootstrapRandomSearch(numTrials: 8);
var optimized = await optimizer.CompileAsync(module, trainSet, metric);

// ========================================
// Step 6: Evaluate
// ========================================

var result = await Evaluator.EvaluateAsync(optimized, devSet, metric);
Console.WriteLine($"Accuracy: {result.AverageScore:P1}");

// ========================================
// Step 7: Save and deploy
// ========================================

await optimized.SaveAsync("triage-v1.json");

// In production: load optimized parameters
var production = new TicketTriageModule(client);
await production.LoadAsync("triage-v1.json");
var reply = await production.ForwardAsync(new TicketInput("I was charged twice", "Pro"));
```

---

## 9. DSPy → LMP Quick Reference

| DSPy | LMP | Notes |
|---|---|---|
| `dspy.Signature` (mixed I/O) | Separate `TInput` record + `[LmpSignature]` output record | Split design matches `IChatClient.GetResponseAsync<T>()` |
| `dspy.Predict` | `Predictor<TIn, TOut>` | Core primitive with `Demos`, `Instructions`, `Config` |
| `dspy.Module` + `forward()` | `LmpModule` / `LmpModule<TIn, TOut>` + `ForwardAsync()` | Source gen emits `GetPredictors()`, `CloneCore()` |
| `dspy.Example` | `Example` / `Example<TInput, TLabel>` | Abstract base + typed record with `WithInputs()` / `GetLabel()` |
| `dspy.ChainOfThought` | `ChainOfThought<TIn, TOut>` | Source gen extends output with `Reasoning` |
| `dspy.majority` / Best-of-N | `BestOfN<TIn, TOut>` | `Task.WhenAll` — true parallelism, no GIL |
| `dspy.Refine` | `Refine<TIn, TOut>` | Predict → critique → re-predict loop |
| `dspy.ReAct` | `ReActAgent<TIn, TOut>` | M.E.AI's `AIFunction` + `FunctionInvokingChatClient` |
| `dspy.Assert` / `dspy.Suggest` | `LmpAssert.That` / `LmpSuggest.That` | Typed lambda predicate |
| `dspy.Retrieve` | `IRetriever` | Users bring implementation via DI |
| `dspy.Evaluate` | `Evaluator.EvaluateAsync()` | `Parallel.ForEachAsync`, returns `EvaluationResult` |
| `BootstrapFewShot` | `BootstrapFewShot.CompileAsync()` | Run teacher, collect traces, fill `Demos` |
| `BootstrapFewShotWithRandomSearch` | `BootstrapRandomSearch.CompileAsync()` | × N candidates with validation split |
| `MIPROv2` (Optuna TPE, ~35K LOC) | `MIPROv2` | Custom `CategoricalTpeSampler` / `SmacSampler` |
| `COPRO` | `GEPA` | Pareto-front assembly |
| `dspy.Metric` | `Metric.Create` / `Metric.CreateAsync` | Typed → untyped metric bridge |
| N/A (manual) | `ISampler` (Propose/Update) | Pluggable Bayesian sampling |
| `module.save()` / `module.load()` | `module.SaveAsync()` / `module.LoadAsync()` | Source-gen `JsonSerializerContext` — AOT-compatible |
| N/A | `[Predict]` attribute | Source-gen partial method wiring |
| N/A | `ILmpRunner` | CLI entry point for `dotnet lmp` |
| N/A | `EvaluationBridge` | M.E.AI.Evaluation → LMP metric bridge |

---

## 10. What Gets Generated at Build Time

For a `Predictor<TicketInput, ClassifyTicket>`, the source generator emits:

| Artifact | Purpose | DSPy Equivalent (runtime) |
|---|---|---|
| `PromptBuilder<TicketInput, ClassifyTicket>` | Assembles `ChatMessage[]` from instructions + demos + input | `ChatAdapter` string formatting |
| `JsonTypeInfo<ClassifyTicket>` | Zero-reflection JSON serialization (AOT-safe) | Pydantic runtime validation |
| `GetPredictors()` on module | Returns `(name, predictor)` pairs | `named_predictors()` walks `__dict__` |
| `CloneCore()` on module | Deep clones all predictor fields | `copy.deepcopy(module)` |
| `JsonSerializerContext` for state | AOT-compatible `SaveAsync` / `LoadAsync` | `pickle` / runtime JSON introspection |
| Interceptors (C# 14) | Wires `SetPromptBuilder()` at call sites | N/A |
| Diagnostic LMP001 | Missing `[Description]` on output property | Runtime crash |
| Diagnostic LMP002 | Non-serializable output type | Runtime crash |
| Diagnostic LMP003 | Invalid `[LmpSignature]` usage | Runtime crash |

---

## 11. What's Intentionally Excluded

| Dropped Concept | Why |
|---|---|
| Directed program graphs (`ProgramGraph`, `Step.*`) | DSPy has no graph IR. Modules are plain Python classes with `forward()`. LMP mirrors this with `LmpModule.ForwardAsync()`. |
| `[Input]` / `[Output]` attributes | Replaced by separate TInput/TOutput types. Input types use standard `[Description]`. Output types use `[LmpSignature]`. |
| `[BindFrom]` attribute | Over-engineered binding system. `Predictor<TIn, TOut>` with source-gen `PromptBuilder` covers all data flow. |
| `LmpProgram<TIn, TOut>` with `Build()` | Replaced by `LmpModule` with `ForwardAsync()`. Control flow lives in C#, not declarative graphs. |
| `CompileSpec` / `IProgramCompiler` / `CompileReport` | Replaced by `IOptimizer.CompileAsync()`. Optimization is a library feature, not an enterprise pipeline. |
| `CompiledArtifact` / `ICompiledArtifactLoader` | Replaced by `SaveAsync` / `LoadAsync` on `LmpModule`. JSON state files are sufficient. |
| `IOptimizerBackend` | Internal concern — the public API is `IOptimizer`. Backend strategy is an implementation detail. |
| Three-tier binding model | No binding needed. Types compose naturally via C# method calls in `ForwardAsync()`. |
| Hot-swap `AssemblyLoadContext` | Premature. JSON save/load is the right starting point. |
