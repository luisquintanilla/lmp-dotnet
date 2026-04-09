# LMP System Architecture

> **Status:** v2 — rewritten from first principles  
> **Target:** .NET 10 / C# 14  
> **Dependency:** `Microsoft.Extensions.AI` (`IChatClient`)  
> **Philosophy:** Building blocks, not a framework

---

## Design Principles

1. **Building blocks, not a framework** — simple primitives that compose naturally via standard C# types.
2. **Separate `TInput` / `TOutput` types** — mirrors how `IChatClient.GetResponseAsync<T>()` actually works. Input is messages; `T` is the output type. They are naturally separate.
3. **Source generators are the star** — compile-time superpowers Python cannot replicate: typed prompt builders, zero-reflection serialization, build-time diagnostics.
4. **LMP depends ONLY on `IChatClient`** from [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient). No other LM abstraction.
5. **Don't overengineer** — start simple, layer complexity later. No intermediate representations, no directed graphs, no custom MSBuild SDKs.
6. **Inspired by DSPy, not rebuilding it** — adopt DSPy's core insight (LM programs should be optimized programmatically), build .NET-native.

---

## Layer Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 4 — Optional Tooling (post-MVP)                               │
│   dotnet lmp optimize CLI · Aspire dashboard · [Predict] sugar      │
│   C# 14 interceptors for zero-dispatch PredictAsync                 │
├─────────────────────────────────────────────────────────────────────┤
│ Layer 3 — Optimization                           LMP.Optimizers     │
│   Evaluator · BootstrapFewShot · BootstrapRandomSearch · MIPROv2*   │
│   IOptimizer interface — all return same module with params filled   │
├─────────────────────────────────────────────────────────────────────┤
│ Layer 2 — Reasoning Strategies                   LMP.Modules        │
│   ChainOfThought · BestOfN · Refine · ReActAgent · ProgramOfThought*│
│   Thin wrappers around Predictor<TIn, TOut> — each < 100 LOC       │
├─────────────────────────────────────────────────────────────────────┤
│ Layer 1 — Core Building Blocks    LMP.Abstractions + LMP.Core       │
│   [LmpSignature] · Predictor<TIn,TOut> · LmpModule · Example       │
│   Trace · LmpAssert / LmpSuggest · IRetriever                      │
├─────────────────────────────────────────────────────────────────────┤
│ Layer 0 — .NET Platform (zero LMP code)                             │
│   IChatClient · GetResponseAsync<T>() · ChatClientBuilder           │
│   DataAnnotations · System.Text.Json source gen · M.E.AI.Evaluation │
│   AIFunction · FunctionInvokingChatClient · IOptions<T>             │
└─────────────────────────────────────────────────────────────────────┘

                         * = post-MVP
```

---

## Layer 0: .NET Platform (Already Exists — Zero LMP Code)

Everything below is provided by the .NET ecosystem. LMP builds on top of it, never re-implements it.

| Capability | .NET Component | What It Does for LMP |
|---|---|---|
| LM abstraction | [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient) | Uniform interface to any LM provider (OpenAI, Anthropic, Ollama, etc.) |
| Structured output | [`GetResponseAsync<T>()`](https://learn.microsoft.com/dotnet/ai/quickstarts/quickstart-ai-chat-with-data#get-a-structured-response) | Deserializes LM output directly into C# types — no parsing |
| Middleware pipeline | [`ChatClientBuilder`](https://learn.microsoft.com/dotnet/ai/conceptual/middleware) | Caching, telemetry, logging, rate limiting — compose via `Use()` |
| Input/output types | C# `record` types | Immutable, equatable, deconstruct-able — natural data carriers |
| Validation | `DataAnnotations` / `IValidatableObject` | Standard .NET validation on output types |
| JSON serialization | `System.Text.Json` source gen | Zero-reflection, AOT-safe serialization |
| Evaluation | [`M.E.AI.Evaluation`](https://learn.microsoft.com/dotnet/ai/conceptual/evaluation-libraries-overview) | Built-in evaluators: Relevance, Truth, Coherence, Groundedness, Completeness |
| Tool use | [`AIFunction`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.aifunction) / `FunctionInvokingChatClient` | Function calling — ReAct agent tools with zero new code |
| Configuration | `IOptions<T>` | Standard options pattern for model settings, temperature, etc. |

**Key insight:** Layer 0 already solves LM calling, structured output, tool use, evaluation, and middleware. LMP's job is to add *programmable optimization* on top.

---

## Layer 1: Core Building Blocks (`LMP.Abstractions` + `LMP.Core`)

### `[LmpSignature("instructions")]`

An attribute placed on a `partial record` **output type**. This is the entry point for the source generator.

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

The source generator reads this at build time and emits:
- **`PromptBuilder<TIn, TOut>`** — assembles `ChatMessage[]` from instructions + demos + input fields
- **`JsonTypeInfo<TOut>`** — zero-reflection JSON serialization for structured output
- **Diagnostics** — IDE red squiggles for missing descriptions, non-serializable output types

### `Predictor<TInput, TOutput>`

The core primitive. Binds an input type to an output type. Contains learnable state.

```csharp
var classifier = new Predictor<TicketInput, ClassifyTicket>(chatClient);

// Learnable state (filled by optimizers)
classifier.Demos = new[] { /* few-shot examples */ };
classifier.Instructions = "Classify a support ticket..."; // can be overridden
classifier.Config = new PredictorConfig { Temperature = 0.7f };

// Predict
var result = await classifier.PredictAsync(new TicketInput("I was charged twice"));
// result.Category == "billing", result.Urgency == 4
```

Internally: source-generated `PromptBuilder` → `ChatMessage[]` → `IChatClient.GetResponseAsync<TOutput>()`.

### `LmpModule`

Base class for composable LM programs. Users override `ForwardAsync()`.

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

Source generator emits `GetPredictors()` — zero-reflection predictor discovery for optimization.
`SaveAsync()` / `LoadAsync()` use source-generated `JsonSerializerContext` — AOT-compatible.

### Supporting Types

| Type | Purpose |
|---|---|
| `Example<TInput, TLabel>` | Training data record. `WithInputs()` extracts just the input portion. |
| `Trace` | Records `(predictor, inputs, outputs)` tuples during execution for optimization. |
| `LmpAssert` | Runtime assertion with retry/backtrack: `LmpAssert.That(result, r => r.Urgency >= 1)` |
| `LmpSuggest` | Soft assertion — logs a warning, no retry: `LmpSuggest.That(result, r => r.Category != "unknown")` |
| `IRetriever` | RAG interface: `RetrieveAsync(query, k) → string[]`. Users bring their own implementation via DI. |

---

## Layer 2: Reasoning Strategies (`LMP.Modules`)

Thin wrappers around `Predictor<TIn, TOut>`. Each is under 100 lines of code.

### `ChainOfThought<TIn, TOut>`

Extends `TOut` with a `Reasoning` field. The source generator creates an extended output record at build time so the LM produces step-by-step reasoning before the final answer.

```csharp
var cot = new ChainOfThought<TicketInput, ClassifyTicket>(client);
var result = await cot.PredictAsync(input);
// result has .Reasoning + .Category + .Urgency
```

*Inspired by:* [`dspy/predict/chain_of_thought.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/predict/chain_of_thought.py) (49 LOC in DSPy).

### `BestOfN<TIn, TOut>`

Runs N parallel predictions via `Task.WhenAll`, returns the best by a reward function.

```csharp
var best = new BestOfN<TicketInput, ClassifyTicket>(client, n: 5,
    reward: (input, output) => output.Urgency >= 1 && output.Urgency <= 5 ? 1f : 0f);
var result = await best.PredictAsync(input);
```

True parallelism — no GIL. Each candidate runs on its own thread.

### `Refine<TIn, TOut>`

Sequential improvement: predict → LM-generated critique → predict again with critique context.

### `ReActAgent<TIn, TOut>`

Think → Act → Observe loop using M.E.AI's [`AIFunction`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.aifunction) for tools. No custom tool abstraction needed — `FunctionInvokingChatClient` handles tool dispatch.

```csharp
var tools = new[] { AIFunctionFactory.Create(SearchKnowledgeBase), AIFunctionFactory.Create(GetAccountInfo) };
var agent = new ReActAgent<TicketInput, TriageResult>(client, tools);
var result = await agent.PredictAsync(input);
```

*Inspired by:* [`dspy/predict/react.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/predict/react.py) (345 LOC in DSPy).

### `ProgramOfThought<TIn, TOut>` *(post-MVP)*

LM generates C# code → Roslyn scripting executes it → returns structured result. No Deno, no Python — C# all the way down.

---

## Layer 3: Optimization (`LMP.Optimizers`)

This is DSPy's core insight brought to .NET: LM programs should be optimized programmatically, not manually tuned.

### `Evaluator`

Runs a module on a dev set, scores with a metric function, aggregates results.

```csharp
var score = await Evaluator.EvaluateAsync(
    module,
    devSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
// score == 0.87 (87% accuracy)
```

Uses `Parallel.ForEachAsync` for concurrent evaluation across the dataset.

### `BootstrapFewShot`

The core "compile" step. Runs a teacher module on the training set, collects successful traces (where the metric passes), and fills `predictor.Demos` with those traces as few-shot examples.

```csharp
var optimizer = new BootstrapFewShot(metric, maxDemos: 4);
var optimized = await optimizer.OptimizeAsync(module, trainSet);
// optimized module now has few-shot demos filled in
```

*Inspired by:* [`dspy/teleprompt/bootstrap.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/teleprompt/bootstrap.py) (~250 LOC in DSPy).

### `BootstrapRandomSearch`

`BootstrapFewShot` × N candidates with `Task.WhenAll`. Returns the best.

```csharp
var optimizer = new BootstrapRandomSearch(metric, numTrials: 8);
var best = await optimizer.OptimizeAsync(module, trainSet, devSet);
```

### `MIPROv2` *(post-MVP)*

Bayesian optimization over both instructions and demos. DSPy's MIPROv2 uses Optuna's TPE sampler (~35K LOC). LMP could use [ML.NET AutoML tuners](https://learn.microsoft.com/dotnet/machine-learning/how-to-guides/how-to-use-the-automl-api) as a backend.

### `IOptimizer`

All optimizers implement this interface. All return the same module type with parameters filled in — no new types created.

```csharp
public interface IOptimizer
{
    Task<TModule> OptimizeAsync<TModule>(
        TModule module,
        IReadOnlyList<Example<TInput, TLabel>> trainSet,
        Func<TLabel, TOutput, float>? metric = null)
        where TModule : LmpModule;
}
```

---

## Separate `TInput` / `TOutput` Design

This is a deliberate departure from DSPy, where a single `dspy.Signature` class mixes input and output fields (decorated with `InputField` / `OutputField`). In LMP, input and output are separate C# types.

**Why:** M.E.AI's actual API is `chatClient.GetResponseAsync<T>()` where `T` is the output type and input is `ChatMessage[]`. They are naturally separate. Forcing them into one type fights the platform.

### Input Types — Plain C# Records

```csharp
// Option A: [Description] on constructor parameters
public record TicketInput(
    [Description("The raw ticket text")] string TicketText,
    [Description("Customer plan tier")] string AccountTier);

// Option B: XML doc comments (source gen reads these too)
/// <param name="TicketText">The raw ticket text</param>
/// <param name="AccountTier">Customer plan tier</param>
public record TicketInput(string TicketText, string AccountTier);
```

No LMP attributes needed on input types. They're just data.

### Output Types — `partial record` with `[LmpSignature]`

```csharp
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1-5")]
    public required int Urgency { get; init; }
}
```

### Composition Is Type-Checked

```csharp
var classify = new Predictor<TicketInput, ClassifyResult>(client);
var draft    = new Predictor<ClassifyResult, DraftReply>(client);  // output IS next input

// Compiler enforces: ClassifyResult must be a valid input AND valid output
```

When one step's output feeds into the next step's input, the C# compiler verifies type compatibility at build time. No runtime `KeyError` surprises.

---

## Source Generator — The .NET Advantage

The source generator is the single biggest differentiator between LMP and DSPy. It reads both `TInput` and `TOutput` types at build time and emits compile-time artifacts that Python fundamentally cannot produce.

### What Gets Generated

For a `Predictor<TicketInput, ClassifyTicket>`:

1. **`PromptBuilder<TicketInput, ClassifyTicket>`** — Assembles `ChatMessage[]` from instructions, demos, and input. Field names, types, and descriptions are baked in as string constants. No runtime reflection.

2. **`JsonTypeInfo<ClassifyTicket>`** — Zero-reflection JSON serialization via `System.Text.Json` source generation. AOT-safe.

3. **`GetPredictors()` on `LmpModule`** — Discovers all `Predictor` fields in a module without walking `__dict__` at runtime (which is what DSPy's `named_predictors()` does in Python).

4. **Diagnostics (2–3 rules):**
   - Missing `[Description]` on output properties → warning
   - Non-serializable output type → error
   - (Future) Unreachable predictor in module → warning

### Input Field Description Sources

The source generator reads input descriptions from three sources, in priority order:

1. XML doc comments (`/// <param name="X">...</param>`)
2. `[Description]` on constructor parameters
3. `[Description]` on properties

No `property:` prefix is needed. The generator handles it.

### What DSPy Does at Runtime, LMP Does at Build Time

| Capability | DSPy (Python, runtime) | LMP (C#, compile-time) |
|---|---|---|
| Signature field types | Pydantic runtime validation | Source gen emits `JsonTypeInfo<T>` |
| Prompt assembly | Runtime string formatting | Source gen emits `PromptBuilder` class |
| Predictor discovery | `named_predictors()` walks `__dict__` | Source gen emits `GetPredictors()` |
| State serialization | Runtime introspection (`pickle`/JSON) | Source gen `JsonSerializerContext` — AOT-safe |
| Missing descriptions | Runtime error | IDE red squiggle at build time |
| Invalid output types | Runtime crash | Build error with diagnostic code |

*Source: DSPy's runtime signature handling in [`dspy/signatures/signature.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/signatures/signature.py) and adapter prompt assembly in [`dspy/adapters/chat_adapter.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/adapters/chat_adapter.py).*

### Prompt Format

DSPy's `ChatAdapter` uses `[[ ## field_name ## ]]` delimiters in prompt text. LMP's source-generated `PromptBuilder` creates equivalent `ChatMessage[]`:

```
System message:
  Instructions (from [LmpSignature])
  Field descriptions (from [Description] / XML doc comments)

Demo pairs (from predictor.Demos):
  User message:  input field values
  Assistant message: JSON output

Current input:
  User message:  input field values
```

**Key difference:** LMP uses `GetResponseAsync<TOutput>()` for structured output. No output delimiter parsing needed — M.E.AI handles JSON schema negotiation with the provider natively.

---

## DSPy → LMP Mapping

### Reasoning Strategies

| DSPy | LMP | Notes |
|---|---|---|
| `ChainOfThought` (49 LOC) | `ChainOfThought<TIn, TOut>` | Source gen extends `TOut` with `Reasoning` field |
| `ProgramOfThought` (262 LOC) | `ProgramOfThought<TIn, TOut>` | Roslyn scripting for C# execution (post-MVP) |
| `majority` / Best-of-N | `BestOfN<TIn, TOut>` | `Task.WhenAll` for true parallelism (no GIL) |

### Agents and Tools

| DSPy | LMP | Notes |
|---|---|---|
| `ReAct` (345 LOC) | `ReActAgent<TIn, TOut>` | M.E.AI's `AIFunction` + `FunctionInvokingChatClient` |
| `dspy.Tool` | `AIFunction` / `AIFunctionFactory` | Already exists in M.E.AI — zero new code |

### RAG

| DSPy | LMP | Notes |
|---|---|---|
| `Retrieve` (61 LOC) | `IRetriever` interface | Users bring implementation via DI |
| RAG pipeline | `LmpModule` composition | `IRetriever` + `Predictor` composed in `ForwardAsync()` |

### Evaluation

| DSPy | LMP | Notes |
|---|---|---|
| `dspy.Example` | `Example<TInput, TLabel>` | Typed record with `WithInputs()` |
| Metric function | `Func<TLabel, TOutput, float>` | Or use `M.E.AI.Evaluation` evaluators — don't rebuild |
| `dspy.Evaluate` | `Evaluator.EvaluateAsync()` | `Parallel.ForEachAsync` for concurrency |

### Optimization

| DSPy | LMP | Notes |
|---|---|---|
| `BootstrapFewShot` (~250 LOC) | `BootstrapFewShot` | Run teacher, collect successful traces, fill `Demos` |
| `BootstrapFewShotWithRandomSearch` | `BootstrapRandomSearch` | × N candidates with `Task.WhenAll` |
| `MIPROv2` (~35K LOC, uses Optuna TPE) | `MIPROv2` (post-MVP) | ML.NET AutoML tuners as possible backend |

### Save / Load

| DSPy | LMP | Notes |
|---|---|---|
| `module.save("file.json")` | `module.SaveAsync("file.json")` | Source-gen `JsonSerializerContext` — AOT-compatible |
| `module.load("file.json")` | `module.LoadAsync("file.json")` | Same module type, parameters filled in |

### Assertions

| DSPy | LMP | Notes |
|---|---|---|
| `dspy.Assert(condition, msg)` | `LmpAssert.That(result, r => ...)` | Typed lambda predicate, retry/backtrack on failure |
| `dspy.Suggest(condition, msg)` | `LmpSuggest.That(result, r => ...)` | Logs warning, no retry |

---

## Five Things Python Cannot Do

These are structural advantages of the .NET platform that no amount of Python library code can replicate:

1. **Compile-time signature validation** — Missing descriptions, invalid output types, and type mismatches surface as IDE red squiggles and build errors. In DSPy, these are runtime crashes.

2. **Zero-reflection predictor discovery** — Source gen emits `GetPredictors()` at build time. DSPy's `named_predictors()` walks `__dict__` at runtime, which is fragile and invisible to tooling.

3. **Source-generated prompt builders** — `PromptBuilder<TIn, TOut>` is a concrete class with field names baked in as constants. No `string.format()` or f-string assembly at runtime.

4. **AOT-deployable LM programs** — With source-gen JSON and no reflection, LMP modules can be published as native AOT binaries. ~50ms cold start vs. 2–5s for Python. Critical for serverless / edge deployment.

5. **True parallelism in optimization** — `Task.WhenAll` + `Parallel.ForEachAsync` use real OS threads. Python's GIL serializes CPU-bound work, making `BestOfN` and `BootstrapRandomSearch` fundamentally slower.

---

## Post-MVP Extensions

| Extension | Package | What It Does |
|---|---|---|
| ML.NET AutoML | `LMP.Optimizers.AutoML` | MIPROv2 tuner backend using [ML.NET AutoML](https://learn.microsoft.com/dotnet/machine-learning/how-to-guides/how-to-use-the-automl-api) |
| Infer.NET | `LMP.Extensions.Probabilistic` | Bayesian A/B testing of instruction variants |
| Z3 | `LMP.Extensions.Constraints` | Multi-constraint feasibility analysis for optimizer search spaces |
| C# 14 Interceptors | `LMP.Core` | Zero-dispatch `PredictAsync` optimization — compiler rewrites call sites |
| Aspire integration | `LMP.Aspire` | Dashboard for optimization runs, traces, evaluator metrics |
| `[Predict]` sugar | `LMP.Core` | Partial method attribute — source gen emits `PredictAsync` body |
| `dotnet lmp optimize` | `LMP.Cli` | CLI tool wrapping `IOptimizer` for CI/CD pipelines |

---

## What's Intentionally Excluded

The following concepts appeared in earlier drafts but have been dropped. They added complexity without matching how DSPy actually works or how .NET developers naturally build software:

| Dropped Concept | Why |
|---|---|
| Directed program graphs | DSPy has no graph IR. Modules are plain Python classes with `forward()`. LMP mirrors this with `LmpModule.ForwardAsync()`. |
| Intermediate Representation (IR) | DSPy has no IR layer. `ProgramDescriptor` / `StepDescriptor` / `SignatureDescriptor` added indirection without value. |
| MSBuild targets for graph validation | No graphs → no graph validation. Standard `dotnet build` with source gen diagnostics is sufficient. |
| Three-tier binding system | Over-engineered. `Predictor<TIn, TOut>` with source-gen `PromptBuilder` covers all binding. |
| Hot-swap `AssemblyLoadContext` artifacts | Premature. `SaveAsync` / `LoadAsync` with JSON is the right starting point. |
| NuGet artifact packaging | Not core architecture. Can be layered later if needed. |
| CLI compilation tool as core architecture | Optimization is a library feature (`IOptimizer`), not a CLI-first workflow. CLI is optional tooling (Layer 4). |
| 7+ source generator diagnostics with code fixes | Start with 2–3 essential diagnostics. Add more based on real usage. |
| Three-layer build architecture | One source generator, one build step. `dotnet build` does everything. |

---

## End-to-End Example: Ticket Triage

```csharp
// === Input type — plain C# record ===
public record TicketInput(
    [Description("The raw ticket text")] string TicketText);

// === Output type — partial record with [LmpSignature] ===
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
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}

// === Module — composes two predictors ===
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
        LmpAssert.That(classification, c => c.Urgency >= 1 && c.Urgency <= 5);
        return await _draft.PredictAsync(classification);
    }
}

// === Optimize — bootstrap few-shot demos from training data ===
var client = new ChatClientBuilder(new OpenAIChatClient("gpt-4o-mini"))
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

var module = new TicketTriageModule(client);

var trainSet = LoadExamples<TicketInput, DraftReply>("train.jsonl");
var devSet   = LoadExamples<TicketInput, DraftReply>("dev.jsonl");

var optimizer = new BootstrapRandomSearch(
    metric: (label, output) => label.ReplyText.Contains(output.Category) ? 1f : 0f,
    numTrials: 8);

var optimized = await optimizer.OptimizeAsync(module, trainSet, devSet);

// === Save optimized parameters ===
await optimized.SaveAsync("triage-v1.json");

// === Evaluate ===
var score = await Evaluator.EvaluateAsync(optimized, devSet,
    metric: (label, output) => label.ReplyText.Contains(output.Category) ? 1f : 0f);
Console.WriteLine($"Accuracy: {score:P1}");

// === Deploy — load in production ===
var production = new TicketTriageModule(client);
await production.LoadAsync("triage-v1.json");
var reply = await production.ForwardAsync(new TicketInput("I was charged twice"));
```

---

## Package Structure

```
LMP.sln
├── src/
│   ├── LMP.Abstractions/     # [LmpSignature], IRetriever, Example<T>, Trace, base types
│   ├── LMP.Core/             # Predictor<TIn,TOut>, LmpModule, LmpAssert, source generator
│   ├── LMP.Modules/          # ChainOfThought, BestOfN, Refine, ReActAgent
│   └── LMP.Optimizers/       # Evaluator, BootstrapFewShot, BootstrapRandomSearch, IOptimizer
│
├── tests/
│   ├── LMP.Core.Tests/
│   ├── LMP.Modules.Tests/
│   └── LMP.Optimizers.Tests/
│
└── samples/
    └── LMP.Samples.TicketTriage/
```
