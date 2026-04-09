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
LMP.sln
├── src/
│   ├── LMP.Abstractions/     # [LmpSignature], Predictor, LmpModule, Example, Trace, IRetriever, IOptimizer
│   ├── LMP.Core/             # Source generator, PromptBuilder, JsonTypeInfo, diagnostics
│   ├── LMP.Modules/          # ChainOfThought, BestOfN, Refine, ReActAgent
│   └── LMP.Optimizers/       # Evaluator, BootstrapFewShot, BootstrapRandomSearch, IOptimizer impls
│
├── tests/
│   ├── LMP.Core.Tests/
│   ├── LMP.Modules.Tests/
│   └── LMP.Optimizers.Tests/
│
└── samples/
    └── LMP.Samples.TicketTriage/
```

### LMP.Abstractions

Shared contracts consumed by all other packages. **Zero Roslyn dependency.**

| Visibility | Types |
|---|---|
| **Public** | `LmpSignatureAttribute`, `Predictor<TInput, TOutput>`, `LmpModule`, `Example<TInput, TLabel>`, `Trace`, `LmpAssert`, `LmpSuggest`, `IRetriever`, `IOptimizer` |
| **Internal** | Descriptor record types, hash utilities |

### LMP.Core

Source generator and runtime plumbing. Depends on `LMP.Abstractions`.

| Visibility | Types |
|---|---|
| **Public** | `IServiceCollection` extension methods (`AddLmp`) |
| **Internal** | `PromptBuilder<TIn, TOut>` (source-generated), `JsonTypeInfo<TOut>` (source-generated), prompt assembler, structured-output parser |

### LMP.Modules

Reasoning strategies. Depends on `LMP.Abstractions`, `LMP.Core`.

| Visibility | Types |
|---|---|
| **Public** | `ChainOfThought<TIn, TOut>`, `BestOfN<TIn, TOut>`, `Refine<TIn, TOut>`, `ReActAgent<TIn, TOut>`, `ProgramOfThought<TIn, TOut>` *(post-MVP)* |

### LMP.Optimizers

Optimization pipeline. Depends on `LMP.Abstractions`, `LMP.Core`.

| Visibility | Types |
|---|---|
| **Public** | `Evaluator`, `BootstrapFewShot`, `BootstrapRandomSearch`, `MIPROv2` *(post-MVP)* |

> **Implementation Note:** `LMP.Core` contains the source generator (Roslyn `IIncrementalGenerator`). It ships as an analyzer package — consumers reference it like any other NuGet package and the generator runs automatically at `dotnet build`.

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

### 4.2 `Predictor<TInput, TOutput>`

The core primitive. Binds an input type to an output type. Contains learnable state that optimizers fill in.

```csharp
namespace LMP;

/// <summary>
/// A typed LM call: takes TInput, returns TOutput via structured output.
/// Contains learnable parameters (demos, instructions) that optimizers tune.
/// </summary>
public class Predictor<TInput, TOutput>
    where TOutput : class
{
    /// <summary>Creates a predictor bound to the given chat client.</summary>
    public Predictor(IChatClient client);

    /// <summary>
    /// Few-shot demonstration examples. Filled by optimizers.
    /// Each demo is an (input, output) pair included in the prompt.
    /// </summary>
    public IReadOnlyList<(TInput Input, TOutput Output)> Demos { get; set; }

    /// <summary>
    /// Task instructions. Defaults to the [LmpSignature] instructions.
    /// Can be overridden by optimizers (e.g., MIPROv2 instruction generation).
    /// </summary>
    public string Instructions { get; set; }

    /// <summary>
    /// Predictor-level configuration overrides (temperature, max tokens, etc.).
    /// </summary>
    public PredictorConfig Config { get; set; }

    /// <summary>
    /// Executes a single LM call: builds prompt from instructions + demos + input,
    /// calls IChatClient.GetResponseAsync&lt;TOutput&gt;(), returns typed output.
    /// </summary>
    public Task<TOutput> PredictAsync(
        TInput input,
        CancellationToken cancellationToken = default);
}
```

**Usage:**

```csharp
var classifier = new Predictor<TicketInput, ClassifyTicket>(chatClient);

// Learnable state (filled by optimizers or set manually)
classifier.Demos = [
    (new TicketInput("I was charged twice", "Pro"),
     new ClassifyTicket { Category = "billing", Urgency = 4 })
];
classifier.Config = new PredictorConfig { Temperature = 0.7f };

// Predict
var result = await classifier.PredictAsync(new TicketInput("SSO fails", "Enterprise"));
// result.Category == "technical", result.Urgency == 5
```

**Internals:** The source-generated `PromptBuilder<TInput, TOutput>` assembles `ChatMessage[]` → `IChatClient.GetResponseAsync<TOutput>()` using the source-generated `JsonTypeInfo<TOutput>`. No runtime reflection.

---

### 4.3 `LmpModule`

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
    /// Defines the module's execution logic. Override this to compose
    /// predictors, assertions, and other modules.
    /// </summary>
    public abstract Task<object> ForwardAsync(object input, CancellationToken ct = default);

    /// <summary>
    /// Returns all Predictor instances in this module. Source generator emits
    /// this method — zero-reflection predictor discovery for optimization.
    /// </summary>
    public virtual IReadOnlyList<object> GetPredictors();

    /// <summary>
    /// Serializes all learnable parameters (demos, instructions, config) to JSON.
    /// Uses source-generated JsonSerializerContext — AOT-compatible.
    /// </summary>
    public Task SaveAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Loads learnable parameters from a previously saved JSON file.
    /// Same module type, parameters filled in.
    /// </summary>
    public Task LoadAsync(string path, CancellationToken ct = default);
}
```

**Usage:**

```csharp
public class TicketTriageModule : LmpModule
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public async Task<DraftReply> ForwardAsync(TicketInput input)
    {
        var classification = await _classify.PredictAsync(input);
        return await _draft.PredictAsync(classification);
    }
}
```

**Source generator emits:**

- `GetPredictors()` override that returns `[_classify, _draft]` — no runtime reflection walking `__dict__` like DSPy's `named_predictors()`.
- `JsonSerializerContext` for `SaveAsync` / `LoadAsync` — AOT-compatible serialization of all predictor state.

**Design notes:**

- DSPy's `Module.forward()` is the direct inspiration. Python's introspection discovers sub-modules by walking `__dict__`; LMP uses source gen instead.
- `ForwardAsync()` is where control flow lives — standard C# `if`, loops, `try/catch`. No declarative graph needed.

---

### 4.4 `Example<TInput, TLabel>`

Training data record used by optimizers.

```csharp
namespace LMP;

/// <summary>
/// A single training/validation example pairing an input with its expected label.
/// </summary>
/// <typeparam name="TInput">The module's input type.</typeparam>
/// <typeparam name="TLabel">The expected output type (ground truth).</typeparam>
public record Example<TInput, TLabel>(TInput Input, TLabel Label)
{
    /// <summary>
    /// Extracts just the input portion — used when running the module
    /// during optimization (inputs go to the module, labels go to the metric).
    /// </summary>
    public TInput WithInputs() => Input;
}
```

**Usage:**

```csharp
var trainSet = new List<Example<TicketInput, ClassifyTicket>>
{
    new(new TicketInput("I was charged twice", "Pro"),
        new ClassifyTicket { Category = "billing", Urgency = 4 }),
    new(new TicketInput("Can't reset password", "Free"),
        new ClassifyTicket { Category = "account", Urgency = 2 }),
};
```

---

### 4.5 `Trace`

Execution record captured during `PredictAsync` calls. Used by optimizers to collect successful demonstrations.

```csharp
namespace LMP;

/// <summary>
/// Records a single (predictor, inputs, outputs) execution during ForwardAsync.
/// Optimizers collect traces where the metric passes and use them as few-shot demos.
/// </summary>
public record Trace(
    object Predictor,
    object Input,
    object Output);
```

**How it's used:** During optimization, the framework wraps `PredictAsync` calls to collect traces. When the metric passes for a training example, the successful traces become few-shot `Demos` on the relevant predictors.

---

### 4.6 `LmpAssert` / `LmpSuggest`

Runtime assertions that integrate with the LM retry loop.

```csharp
namespace LMP;

/// <summary>
/// Hard assertion with retry/backtrack. If the predicate fails, the framework
/// retries the preceding PredictAsync call with the failure message in context.
/// </summary>
public static class LmpAssert
{
    /// <summary>
    /// Asserts that the result satisfies the predicate. Retries on failure.
    /// </summary>
    public static void That<T>(T result, Func<T, bool> predicate, string? message = null);
}

/// <summary>
/// Soft assertion — logs a warning but does not retry.
/// Useful for quality guardrails that shouldn't block execution.
/// </summary>
public static class LmpSuggest
{
    /// <summary>
    /// Suggests that the result should satisfy the predicate. Logs warning on failure.
    /// </summary>
    public static void That<T>(T result, Func<T, bool> predicate, string? message = null);
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

### 4.7 `IRetriever`

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
        int k,
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

### 4.8 `IOptimizer`

All optimizers implement this interface. All return the same module type with parameters filled in — no new types created.

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
    Task<TModule> CompileAsync<TModule, TInput, TLabel>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

**Design notes:**

- Named `CompileAsync` (not `OptimizeAsync`) to align with DSPy's terminology where optimization is called "compiling" — `teleprompter.compile(module, trainset)`.
- Returns `TModule` — the same module type is returned with its predictor `Demos` and `Instructions` filled in. No wrapper types.

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

### 5.5 `ProgramOfThought<TIn, TOut>` *(post-MVP)*

LM generates C# code → Roslyn scripting executes it → returns structured result.

```csharp
namespace LMP.Modules;

/// <summary>
/// Post-MVP: LM generates C# code, Roslyn scripting executes it,
/// structured result is returned. No Deno, no Python — C# all the way down.
/// </summary>
public class ProgramOfThought<TIn, TOut> : Predictor<TIn, TOut>
    where TOut : class
{
    public ProgramOfThought(IChatClient client);
}
```

---

## 6. Complete Type Reference — LMP.Optimizers

This is DSPy's core insight brought to .NET: LM programs should be optimized programmatically, not manually tuned.

### 6.1 `Evaluator`

Runs a module on a dev set, scores with a metric function, aggregates results.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Evaluates a module against a dataset using a metric function.
/// Uses Parallel.ForEachAsync for concurrent evaluation.
/// </summary>
public static class Evaluator
{
    /// <summary>
    /// Runs the module on every example in the dev set, scores each with the metric,
    /// and returns the average score.
    /// </summary>
    public static Task<float> EvaluateAsync<TInput, TLabel>(
        LmpModule module,
        IReadOnlyList<Example<TInput, TLabel>> devSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default);
}
```

**Usage:**

```csharp
var score = await Evaluator.EvaluateAsync(
    module,
    devSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
Console.WriteLine($"Accuracy: {score:P1}"); // "Accuracy: 87.0%"
```

**Design notes:**

- Uses `Parallel.ForEachAsync` for concurrent evaluation across the dataset — real parallelism, no GIL.
- The metric function receives the ground truth label and the module's output, returns a float score (0–1).

---

### 6.2 `BootstrapFewShot`

The core "compile" step. Runs a teacher module on the training set, collects successful traces (where the metric passes), and fills `predictor.Demos` with those traces as few-shot examples.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Bootstraps few-shot examples by running a teacher module on training data.
/// Successful traces (metric ≥ threshold) become demos on each predictor.
/// </summary>
public class BootstrapFewShot : IOptimizer
{
    /// <param name="maxDemos">Maximum demos per predictor (default: 4).</param>
    public BootstrapFewShot(int maxDemos = 4);

    public Task<TModule> CompileAsync<TModule, TInput, TLabel>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

**Usage:**

```csharp
var optimizer = new BootstrapFewShot(maxDemos: 4);
var optimized = await optimizer.CompileAsync(module, trainSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
// optimized module now has few-shot demos filled in each predictor
```

*Inspired by:* [`dspy/teleprompt/bootstrap.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/teleprompt/bootstrap.py) (~250 LOC in DSPy).

---

### 6.3 `BootstrapRandomSearch`

`BootstrapFewShot` × N candidates with `Task.WhenAll`. Returns the best by dev set score.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Runs BootstrapFewShot N times in parallel, evaluates each on a dev set,
/// returns the best. True parallelism via Task.WhenAll.
/// </summary>
public class BootstrapRandomSearch : IOptimizer
{
    /// <param name="numTrials">Number of bootstrap trials to run in parallel.</param>
    public BootstrapRandomSearch(int numTrials = 8);

    public Task<TModule> CompileAsync<TModule, TInput, TLabel>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

**Usage:**

```csharp
var optimizer = new BootstrapRandomSearch(numTrials: 8);
var best = await optimizer.CompileAsync(module, trainSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
```

---

### 6.4 `MIPROv2` *(post-MVP)*

Bayesian optimization over both instructions and demos. DSPy's MIPROv2 uses Optuna's TPE sampler (~35K LOC). LMP could use [ML.NET AutoML tuners](https://learn.microsoft.com/dotnet/machine-learning/how-to-guides/how-to-use-the-automl-api) as a backend.

```csharp
namespace LMP.Optimizers;

/// <summary>
/// Post-MVP: Bayesian optimization over instructions and demos.
/// Uses ML.NET AutoML tuners or similar backend for search.
/// </summary>
public class MIPROv2 : IOptimizer
{
    public Task<TModule> CompileAsync<TModule, TInput, TLabel>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, object, float> metric,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule;
}
```

---

## 7. Error Handling

### Exceptions

| Exception | When | Thrown By |
|---|---|---|
| `ArgumentException` | Null input, invalid configuration | `Predictor.PredictAsync`, constructors |
| `InvalidOperationException` | Module state error (e.g., no client configured) | `LmpModule`, `Predictor` |
| `JsonException` | Structured output parsing fails (LM returns malformed JSON) | `Predictor.PredictAsync` |
| `LmpAssertionException` | `LmpAssert.That` fails after max retries | `LmpAssert.That` |
| `OperationCanceledException` | `CancellationToken` is cancelled | All async methods |

### CancellationToken Support

Every async method in the public API accepts an optional `CancellationToken`:

- `Predictor<TIn, TOut>.PredictAsync(input, ct)`
- `LmpModule.ForwardAsync(input, ct)`
- `LmpModule.SaveAsync(path, ct)` / `LoadAsync(path, ct)`
- `IRetriever.RetrieveAsync(query, k, ct)`
- `IOptimizer.CompileAsync(module, trainSet, metric, ct)`
- `Evaluator.EvaluateAsync(module, devSet, metric, ct)`

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

public class TicketTriageModule : LmpModule
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new ChainOfThought<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public async Task<DraftReply> ForwardAsync(TicketInput input)
    {
        var classification = await _classify.PredictAsync(input);
        LmpAssert.That(classification, c => c.Urgency >= 1 && c.Urgency <= 5);
        LmpSuggest.That(classification, c => c.Category != "unknown");
        return await _draft.PredictAsync(classification);
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

var trainSet = LoadExamples<TicketInput, DraftReply>("train.jsonl");
var devSet   = LoadExamples<TicketInput, DraftReply>("dev.jsonl");

// ========================================
// Step 5: Optimize — bootstrap few-shot demos
// ========================================

var optimizer = new BootstrapRandomSearch(numTrials: 8);
var optimized = await optimizer.CompileAsync(module, trainSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);

// ========================================
// Step 6: Evaluate
// ========================================

var score = await Evaluator.EvaluateAsync(optimized, devSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
Console.WriteLine($"Accuracy: {score:P1}");

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
| `dspy.Module` + `forward()` | `LmpModule` + `ForwardAsync()` | Source gen emits `GetPredictors()` |
| `dspy.Example` | `Example<TInput, TLabel>` | Typed record with `WithInputs()` |
| `dspy.ChainOfThought` | `ChainOfThought<TIn, TOut>` | Source gen extends output with `Reasoning` |
| `dspy.majority` / Best-of-N | `BestOfN<TIn, TOut>` | `Task.WhenAll` — true parallelism, no GIL |
| `dspy.Refine` | `Refine<TIn, TOut>` | Predict → critique → re-predict loop |
| `dspy.ReAct` | `ReActAgent<TIn, TOut>` | M.E.AI's `AIFunction` + `FunctionInvokingChatClient` |
| `dspy.ProgramOfThought` | `ProgramOfThought<TIn, TOut>` | Post-MVP. Roslyn scripting for C# execution |
| `dspy.Assert` / `dspy.Suggest` | `LmpAssert.That` / `LmpSuggest.That` | Typed lambda predicate |
| `dspy.Retrieve` | `IRetriever` | Users bring implementation via DI |
| `dspy.Evaluate` | `Evaluator.EvaluateAsync()` | `Parallel.ForEachAsync` for concurrency |
| `BootstrapFewShot` | `BootstrapFewShot.CompileAsync()` | Run teacher, collect traces, fill `Demos` |
| `BootstrapFewShotWithRandomSearch` | `BootstrapRandomSearch.CompileAsync()` | × N candidates with `Task.WhenAll` |
| `MIPROv2` (Optuna TPE, ~35K LOC) | `MIPROv2` (post-MVP) | ML.NET AutoML tuners as possible backend |
| `module.save()` / `module.load()` | `module.SaveAsync()` / `module.LoadAsync()` | Source-gen `JsonSerializerContext` — AOT-compatible |

---

## 10. What Gets Generated at Build Time

For a `Predictor<TicketInput, ClassifyTicket>`, the source generator emits:

| Artifact | Purpose | DSPy Equivalent (runtime) |
|---|---|---|
| `PromptBuilder<TicketInput, ClassifyTicket>` | Assembles `ChatMessage[]` from instructions + demos + input | `ChatAdapter` string formatting |
| `JsonTypeInfo<ClassifyTicket>` | Zero-reflection JSON serialization (AOT-safe) | Pydantic runtime validation |
| `GetPredictors()` on module | Returns all predictor instances | `named_predictors()` walks `__dict__` |
| `JsonSerializerContext` for state | AOT-compatible `SaveAsync` / `LoadAsync` | `pickle` / runtime JSON introspection |
| Diagnostic LMP001 | Missing `[Description]` on output property | Runtime crash |
| Diagnostic LMP002 | Non-serializable output type | Runtime crash |

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
