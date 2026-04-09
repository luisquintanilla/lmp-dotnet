# LMP (Language Model Programs) — Product Requirements Document

> **Status:** Draft | **Source of Truth:** `spec.org` (Sections 1–4, 17) | **Word Target:** 2000–3000

---

## 1. Executive Summary

**LMP** (Language Model Programs) provides .NET 10 / C# 14 **building blocks** that turn enterprise AI workflows — today scattered across prompt strings, notebooks, and ad-hoc scripts — into **typed, compilable, testable, observable software artifacts**. It adopts DSPy's core insight that LM programs should be optimized programmatically rather than manually tuned, and extends it with .NET-native innovations: **compile-time source generation**, **separate `TInput`/`TOutput` types** that mirror how `IChatClient` actually works, and **zero-reflection serialization** that enables Native AOT deployment. The building blocks — `Predictor<TIn,TOut>`, reasoning strategies like `ChainOfThought` and `BestOfN`, and optimizers like `BootstrapFewShot` — compose naturally via standard C# types. LMP depends **only** on `IChatClient` from `Microsoft.Extensions.AI`; no other LM abstraction is required.

> **One-sentence thesis:** LMP is a set of .NET 10 building blocks that compile LM programs — turning prompt strings and notebook experiments into typed, optimized, deployable software artifacts.

---

## 2. The Problem (Written for Non-Experts)

### The Business Case

Enterprise AI is bleeding money. Not because the technology doesn't work — but because there is **no engineering discipline** for managing LM programs as software:

| Pain | Impact | Source |
|------|--------|--------|
| **Silent prompt drift** | 3–5× debugging time per incident; 20–40% of AI budgets wasted on unnecessary model swaps | InsightFinder 2024 [1], TrackAI 2025 [2] |
| **Manual optimization** | Substantial labor cost per program (LMP team estimate: ~$154K/yr based on 2 weeks × 10 cycles at blended rates) | LMP team estimate — see spec.org Section 1A for methodology |
| **Governance failure** | EU AI Act fines: up to €35M or 7% of global turnover for prohibited practices; up to €15M or 3% for high-risk violations | EU AI Act Article 99 [3] |
| **Enterprise AI failure** | 70–85% of GenAI deployments fail to meet ROI expectations | NTT Data 2024 [4] |

Only **~25%** of AI initiatives deliver expected ROI ([Google Cloud / Deloitte / McKinsey 2025][5]). GenAI spending is projected to grow from $16B (2023) to $143B+ by 2027 at 73% CAGR ([IDC][9]). Most AI projects stall at "pilot purgatory" — technically functional but operationally immature ([McKinsey][7]).

**Sources:**
- [1] https://insightfinder.com/blog/hidden-cost-llm-drift-detection/
- [2] https://trackai.dev/tracks/evaluations/model-drift/root-cause/
- [3] https://artificialintelligenceact.eu/article/99/
- [4] https://www.nttdata.com/global/en/insights/focus/2024/between-70-85p-of-genai-deployment-efforts-are-failing
- [5] https://services.google.com/fh/files/misc/google_cloud_roi_of_ai_2025.pdf
- [7] https://aigazine.com/industry/mckinsey-most-companies-are-stuck-in-ai-pilot-purgatory--s
- [9] https://www.computerworld.com/article/1637459/generative-ai-spending-to-reach-143b-in-2027-idc.html

### What Is an "LM Program"?

Imagine an enterprise **ticket triage system**. A customer submits a support ticket. The system must look up knowledge-base articles, classify the severity, route it to the right team, draft a grounded reply, decide if a human needs to intervene, and justify every decision. Each step calls a **language model** (like GPT-4) with carefully crafted instructions. Chained together, these steps form an **LM program** — a multi-step workflow where AI models do structured work under constraints.

### Why Current Approaches Break Down

Today, most teams build these workflows in Python notebooks or scripts. That works fine for a prototype. It falls apart at enterprise scale:

- **When your triage system handles 10,000 tickets/day and a prompt change breaks severity classification**, there's no compiler to catch it. The bug ships silently.
- **When a new team member adds a routing step but forgets to handle the "Critical" case**, Python gives zero warnings. The gap surfaces as a production incident.
- **When you need to prove to compliance that version 2.3 of your triage logic is exactly what's running in production**, there's no artifact, no hash, no versioned package — just a notebook someone ran last Thursday.
- **When optimization takes 4 hours and costs $200 in API calls**, you can't pause, inspect, or reproduce what happened. The process is a black box.

These aren't AI problems. They're **software engineering problems**. LMP solves them.

> **Why This Matters:** Every pain point above is a real conversation happening in enterprise AI/ML teams today. LMP doesn't make AI "smarter" — it makes AI **governable**. Organizations with mature AI governance save an average of $1.9M per data breach and reduce response times by 80 days ([IBM 2025 Cost of a Data Breach Report](https://newsroom.ibm.com/2025-07-30-ibm-report-13-of-organizations-reported-breaches-of-ai-models-or-applications,-97-of-which-reported-lacking-proper-ai-access-controls)). Stanford's AI Index 2025 documents a [56.4% year-over-year increase](https://hai.stanford.edu/ai-index/2025-ai-index-report/responsible-ai) in AI-related incidents, underscoring the urgency.

---

## 3. The Solution

### What LMP Does in Plain Language

LMP gives developers composable building blocks to **author** AI programs as typed C# modules, **build** them with source-generated validation, **run** them via `IChatClient` with full observability, **evaluate** quality on datasets, **optimize** automatically (few-shot bootstrapping, random search), and **save/load** optimized parameters as versioned JSON. Every step produces inspectable output.

### The 6-Step Developer Story Arc

| Step | What Happens | Output |
|------|-------------|--------|
| **1. Author** | Developer writes `TInput`/`TOutput` records and composes `Predictor<TIn,TOut>` instances inside an `LmpModule` | `.cs` source files |
| **2. Build** | Source generator emits `PromptBuilder`, `JsonTypeInfo`, `GetPredictors()`; diagnostics catch errors in IDE | `.g.cs` generated code |
| **3. Run** | Runtime executes the module via `IChatClient.GetResponseAsync<T>()` with OpenTelemetry traces | Structured trace output |
| **4. Evaluate** | `Evaluator.EvaluateAsync()` scores outputs on a labeled dataset using `Parallel.ForEachAsync` | Metric scores |
| **5. Optimize** | `BootstrapFewShot` / `BootstrapRandomSearch` searches over few-shot demos and parameters | Optimized module with filled `Demos` |
| **6. Save/Deploy** | `module.SaveAsync()` serializes optimized parameters; `module.LoadAsync()` restores them in production | Versioned `.json` artifact |

### Code: Defining Separate Input and Output Types

```csharp
// Input type — plain C# record (no LMP attributes needed)
public record TicketInput(
    [Description("Raw customer issue or support ticket text")] string TicketText,
    [Description("Customer plan tier: Free, Pro, Enterprise")] string AccountTier);

// Output type — partial record with [LmpSignature]
[LmpSignature("""
    You are a senior enterprise support triage assistant.
    Classify the issue severity, determine the owning team, and draft a grounded
    customer reply. If the evidence is insufficient, say so explicitly.
    """)]
public partial record TriageResult
{
    [Description("Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }

    [Description("Owning team name")]
    public required string RouteToTeam { get; init; }

    [Description("Grounded customer reply draft")]
    public required string DraftReply { get; init; }

    [Description("Reasoning for severity and routing")]
    public required string Rationale { get; init; }

    [Description("True if escalation to a human is required")]
    public required bool Escalate { get; init; }
}
```

**What This Does:** Input and output are **separate C# types** — mirroring how `IChatClient.GetResponseAsync<T>()` actually works (input is messages; `T` is the output type). The `[LmpSignature]` attribute on the output type triggers the source generator, which emits a `PromptBuilder<TIn,TOut>`, `JsonTypeInfo<TOut>` for zero-reflection serialization, and build-time diagnostics. The `required` keyword means the compiler refuses to build if any field is missing. **This is a key differentiator from DSPy**, where a single `dspy.Signature` class mixes input and output fields.

### Code: Composing a Multi-Step Module

```csharp
[LmpSignature("Draft a helpful reply to the customer")]
public partial record DraftReply
{
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}

public class TicketTriageModule : LmpModule
{
    private readonly Predictor<TicketInput, TriageResult> _classify;
    private readonly Predictor<TriageResult, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, TriageResult>(client);
        _draft = new Predictor<TriageResult, DraftReply>(client);
    }

    public async Task<DraftReply> ForwardAsync(TicketInput input)
    {
        var classification = await _classify.PredictAsync(input);
        LmpAssert.That(classification, c => c.Severity != null);
        return await _draft.PredictAsync(classification);
    }
}
```

**What This Does:** `LmpModule` is the composition primitive — users override `ForwardAsync()` to chain predictors together. One step's output type becomes the next step's input type, and the **C# compiler verifies type compatibility at build time**. The source generator emits `GetPredictors()` for zero-reflection predictor discovery, enabling optimizers to find and tune every predictor in the module automatically. `LmpAssert.That()` provides runtime assertions with retry/backtrack semantics.

### Code: Optimizing and Deploying

```csharp
var client = new ChatClientBuilder(new OpenAIChatClient("gpt-4.1-mini"))
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

var module = new TicketTriageModule(client);

// Load training and dev sets as typed examples
var trainSet = LoadExamples<TicketInput, DraftReply>("train.jsonl");
var devSet   = LoadExamples<TicketInput, DraftReply>("dev.jsonl");

// Optimize — bootstrap few-shot demos from successful traces
var optimizer = new BootstrapRandomSearch(
    metric: (label, output) => label.ReplyText.Contains(output.ReplyText) ? 1f : 0f,
    numTrials: 8);
var optimized = await optimizer.OptimizeAsync(module, trainSet, devSet);

// Save optimized parameters (source-gen JsonSerializerContext — AOT-safe)
await optimized.SaveAsync("triage-v1.json");

// Evaluate on held-out dev set
var score = await Evaluator.EvaluateAsync(optimized, devSet,
    metric: (label, output) => label.ReplyText.Contains(output.ReplyText) ? 1f : 0f);
Console.WriteLine($"Accuracy: {score:P1}");

// Deploy — load in production
var production = new TicketTriageModule(client);
await production.LoadAsync("triage-v1.json");
var reply = await production.ForwardAsync(
    new TicketInput("SSO login fails intermittently for 300+ users...", "Enterprise"));
```

**What This Does:** `BootstrapRandomSearch` runs the module on training data, collects successful traces (where the metric passes), and fills each predictor's `Demos` with those traces as few-shot examples — searching over N candidates in parallel via `Task.WhenAll`. `SaveAsync()`/`LoadAsync()` use source-generated `JsonSerializerContext` for AOT-compatible serialization. The same module type is used from development through production — no new types created.

---

## 4. Why .NET (Written for Skeptics)

LMP belongs in .NET not because .NET is "more AI-native" — it isn't. LMP belongs in .NET because .NET is **excellent at turning complicated behavior into disciplined software**. LMP sits directly on `IChatClient` from `Microsoft.Extensions.AI` — no dependency on Semantic Kernel, Microsoft Agent Framework, or any other higher-level abstraction.

### .NET Compile-Time Advantage

The source generator is the single biggest differentiator between LMP and DSPy. It reads both `TInput` and `TOutput` types at build time and emits compile-time artifacts that Python fundamentally cannot produce:

| What DSPy Does at Runtime | What LMP Does at Build Time |
|---|---|
| `dspy.Signature("question -> answer")` parses a string | Source gen emits `PromptBuilder<TIn, TOut>` with field names as constants |
| Pydantic validates types at runtime | Source gen emits `JsonTypeInfo<T>` — zero-reflection, AOT-safe |
| `named_predictors()` walks `__dict__` | Source gen emits `GetPredictors()` — zero-reflection discovery |
| `pickle` / JSON introspection for save/load | Source gen `JsonSerializerContext` — AOT-compatible |
| Missing descriptions → runtime error | Missing `[Description]` → IDE red squiggle at build time |
| Invalid output types → runtime crash | Non-serializable output → build error with diagnostic code |

**Five things Python cannot do:**

1. **Compile-time signature validation** — Missing descriptions, invalid output types, and type mismatches surface as IDE red squiggles and build errors. In DSPy, these are runtime crashes.
2. **Zero-reflection predictor discovery** — Source gen emits `GetPredictors()` at build time. DSPy's `named_predictors()` walks `__dict__` at runtime, which is fragile and invisible to tooling.
3. **Source-generated prompt builders** — `PromptBuilder<TIn, TOut>` is a concrete class with field names baked in as constants. No `string.format()` or f-string assembly at runtime.
4. **AOT-deployable LM programs** — With source-gen JSON and no reflection, LMP modules can be published as native AOT binaries. ~50ms cold start vs. 2–5s for Python. Critical for serverless / edge deployment.
5. **True parallelism in optimization** — `Task.WhenAll` + `Parallel.ForEachAsync` use real OS threads. Python's GIL serializes CPU-bound work, making `BestOfN` and `BootstrapRandomSearch` fundamentally slower.

### The Platform Does the Heavy Lifting

LMP depends only on `IChatClient` and composes with .NET's existing infrastructure:

| Capability | .NET Component | What It Does for LMP |
|---|---|---|
| LM abstraction | `IChatClient` | Uniform interface to any LM provider (OpenAI, Anthropic, Ollama, etc.) |
| Structured output | `GetResponseAsync<T>()` | Deserializes LM output directly into C# types — no parsing |
| Middleware | `ChatClientBuilder` | Caching, telemetry, logging, rate limiting — compose via `Use()` |
| Validation | `DataAnnotations` / `IValidatableObject` | Standard .NET validation on output types |
| JSON serialization | `System.Text.Json` source gen | Zero-reflection, AOT-safe serialization |
| Evaluation | `M.E.AI.Evaluation` | Built-in evaluators: Relevance, Truth, Coherence, Groundedness |
| Tool use | `AIFunction` / `FunctionInvokingChatClient` | Function calling — ReAct agent tools with zero new code |
| Configuration | `IOptions<T>` | Standard options pattern for model settings, temperature, etc. |
| Observability | `ActivitySource` / OpenTelemetry | W3C spans, metrics, in-process export for optimization feedback |
| Hosting | `IHost` / DI / `BackgroundService` | Production-grade lifecycle, multi-model routing via keyed DI |

> **Why This Matters:** "In Python, if you add a new step and forget to handle a case, you get a silent bug in production. In LMP, you get a compile error." This is not a marginal improvement — it's a structural guarantee that scales with codebase complexity.

---

## 5. Relationship to DSPy

LMP is **inspired by** DSPy (Stanford NLP), **not a port of it**.

### What We Adopt From DSPy (4 concepts)

1. **The core insight** that LM programs should be optimized programmatically, not manually tuned
2. **Typed Signatures** as LM contracts (`dspy.Signature` → `[LmpSignature]`)
3. **Predict and Retrieve** abstractions (`dspy.Predict` → `Predictor<TIn,TOut>`)
4. **A compile loop** that searches over program variants to find the best one (`BootstrapFewShot`, `BootstrapRandomSearch`)

### What We Built Differently (Key Differentiators)

- **Separate `TInput` / `TOutput` types** — DSPy mixes input/output fields in one `Signature` class. LMP mirrors how `IChatClient.GetResponseAsync<T>()` actually works: input is messages, `T` is the output type. They are naturally separate.
- **Source generators as the star** — compile-time `PromptBuilder`, `JsonTypeInfo`, `GetPredictors()`, and diagnostics. Python has no equivalent.
- **`LmpModule` with `ForwardAsync()`** — mirrors DSPy's module pattern but with typed composition and source-gen predictor discovery.
- **True parallelism** — `Task.WhenAll` for `BestOfN` and `BootstrapRandomSearch`. No GIL.
- **AOT-safe serialization** — `SaveAsync()` / `LoadAsync()` via source-gen `JsonSerializerContext`, not `pickle`.

### DSPy Comparison Table

| Capability | DSPy | LMP |
|---|---|---|
| Program model | Python class with `forward()` | `LmpModule` with typed `ForwardAsync()` |
| Input/output types | Mixed in one `Signature` class | Separate `TInput` / `TOutput` records |
| Compile-time validation | None | Source generators + Roslyn diagnostics |
| Predictor discovery | `named_predictors()` walks `__dict__` | Source gen `GetPredictors()` — zero reflection |
| Prompt assembly | Runtime string formatting | Source gen `PromptBuilder<TIn, TOut>` |
| Parallelism | GIL-limited | `Task.WhenAll` — real OS threads |
| Observability | `execution_log` (flat list of dicts) | OpenTelemetry (W3C spans, metrics) |
| Artifact model | `Module.save()`/`.load()` via JSON | Source-gen `JsonSerializerContext` — AOT-safe |
| IDE feedback | External linters (mypy, pylint) | In-IDE diagnostics while typing |

---

## 6. Target Users

### Enterprise AI/ML Teams Using .NET

**What they care about:** Governance, auditability, reproducibility, cost controls.
**How LMP helps:** Versioned artifacts with `SaveAsync()`/`LoadAsync()`, OpenTelemetry traces for every predictor call, typed evaluation on datasets.

### .NET Developers Building AI-Powered Features

**What they care about:** Familiar patterns, type safety, IDE support, DI/logging integration.
**How LMP helps:** Standard C# authoring with records and attributes, Roslyn diagnostics in the IDE, `IChatClient` + `ChatClientBuilder` middleware — nothing new to learn except the domain concepts.

### Platform Teams Needing Governance Over LM Usage

**What they care about:** Cost visibility, model selection control, deployment safety.
**How LMP helps:** Per-predictor model routing, typed evaluation pipelines, versioned parameter artifacts that pin exactly what was optimized.

---

## 7. MVP Scope

### Core Building Blocks

| Building Block | Package | Details |
|---|---|---|
| **`[LmpSignature]`** | `LMP.Abstractions` | Attribute on `partial record` output types; triggers source generation |
| **`Predictor<TIn,TOut>`** | `LMP.Core` | Core primitive — binds input type to output type, contains learnable `Demos` and `Instructions` |
| **`LmpModule`** | `LMP.Core` | Composition base class; users override `ForwardAsync()` |
| **`ChainOfThought<TIn,TOut>`** | `LMP.Modules` | Extends output with `Reasoning` field via source gen |
| **`BestOfN<TIn,TOut>`** | `LMP.Modules` | N parallel predictions via `Task.WhenAll`; returns best by reward function |
| **`ReActAgent<TIn,TOut>`** | `LMP.Modules` | Think → Act → Observe loop using M.E.AI's `AIFunction` for tools |
| **`Evaluator`** | `LMP.Optimizers` | Runs module on dev set, scores with metric, aggregates results |
| **`BootstrapFewShot`** | `LMP.Optimizers` | Runs teacher on training set, collects successful traces, fills `Demos` |
| **`BootstrapRandomSearch`** | `LMP.Optimizers` | `BootstrapFewShot` × N candidates with `Task.WhenAll`; returns best |

### Supporting Types

| Type | Package | Purpose |
|---|---|---|
| `Example<TInput, TLabel>` | `LMP.Abstractions` | Training data record; `WithInputs()` extracts just the input portion |
| `Trace` | `LMP.Core` | Records `(predictor, inputs, outputs)` tuples during execution |
| `LmpAssert` / `LmpSuggest` | `LMP.Core` | Runtime assertions (hard retry / soft warning) |
| `IRetriever` | `LMP.Abstractions` | RAG interface: users bring their own implementation via DI |

### Source Generation (emitted at build time)

| Generated Artifact | What It Does |
|---|---|
| `PromptBuilder<TIn, TOut>` | Assembles `ChatMessage[]` from instructions + demos + input fields |
| `JsonTypeInfo<TOut>` | Zero-reflection JSON serialization for structured output |
| `GetPredictors()` on `LmpModule` | Zero-reflection predictor discovery for optimization |
| Diagnostics (2–3 rules) | Missing `[Description]` → warning; non-serializable output → error |

### Post-MVP Extensions

| Extension | Package | Details |
|---|---|---|
| **`MIPROv2`** | `LMP.Optimizers` | Bayesian optimization over both instructions and demos |
| **`Refine<TIn,TOut>`** | `LMP.Modules` | Sequential improvement: predict → critique → predict again |
| **`ProgramOfThought<TIn,TOut>`** | `LMP.Modules` | LM generates C# code → Roslyn scripting executes it |
| **ML.NET AutoML** | `LMP.Optimizers.AutoML` | MIPROv2 tuner backend using ML.NET AutoML |
| **Infer.NET** | `LMP.Extensions.Probabilistic` | Bayesian A/B testing of instruction variants |
| **Z3** | `LMP.Extensions.Constraints` | Multi-constraint feasibility analysis for optimizer search spaces |
| **C# 14 Interceptors** | `LMP.Core` | Zero-dispatch `PredictAsync` optimization — compiler rewrites call sites |
| **Aspire integration** | `LMP.Aspire` | Dashboard for optimization runs, traces, evaluator metrics |
| **`dotnet lmp optimize` CLI** | `LMP.Cli` | CLI tool wrapping `IOptimizer` for CI/CD pipelines |

> **Note:** ML.NET, Infer.NET, and Z3 are post-MVP extensions, not core dependencies. They expand optimization capabilities but are not required for the core building blocks.

### Out of Scope (and Why)

| Excluded | Reason |
|---|---|
| Full DSPy feature parity | Inspired by DSPy, not a port — selective adoption is intentional |
| Directed program graphs / IR | Dropped — `LmpModule` with `ForwardAsync()` is simpler and mirrors DSPy's actual pattern |
| Hot-swap `AssemblyLoadContext` | Premature — `SaveAsync()`/`LoadAsync()` with JSON is the right starting point |
| Semantic Kernel / MAF dependency | LMP sits on `IChatClient` only — no higher-level abstraction dependency |
| Distributed optimization | Single-machine optimization sufficient to prove concept |
| Graphical UI | IDE diagnostics + optional CLI sufficient; UI layered later |
| Memory / multimodal / agent loops | Stateless text-only execution is simpler and sufficient for MVP |
| Full Native AOT optimization | AOT-compatible design now; full optimization deferred |

**Scope principle:** When in doubt, prefer simpler building blocks, fewer abstractions, deterministic behavior, and visible output over completeness.

---

## 8. Success Criteria

### Core Acceptance Criteria (all must be true)

1. A developer can define separate `TInput` / `TOutput` record types with `[LmpSignature]`
2. Source generation emits `PromptBuilder<TIn,TOut>`, `JsonTypeInfo<TOut>`, and `GetPredictors()`
3. A developer can compose a multi-step `LmpModule` with `ForwardAsync()`
4. The runtime executes the module via `IChatClient.GetResponseAsync<T>()`
5. `Evaluator.EvaluateAsync()` scores outputs on a dataset
6. `BootstrapFewShot` collects successful traces and fills predictor `Demos`
7. `BootstrapRandomSearch` searches over N candidates and returns the best
8. Optimized parameters can be saved with `SaveAsync()` and loaded with `LoadAsync()`
9. At least 2–3 meaningful source generator diagnostics exist
10. The sample ticket triage demo works end to end

### Demo Acceptance Criteria

The sample demo must visibly show:

- Authored source code with separate `TInput` / `TOutput` types
- Generated source (`.g.cs` files: `PromptBuilder`, `JsonTypeInfo`, `GetPredictors()`)
- Runtime trace output (predictors, timings, model, tokens)
- Evaluation results (metric scores on dev set)
- Optimization results (best candidate selected from N trials)
- Saved artifact (versioned `.json` with optimized parameters)

### What "Done" Looks Like

A developer can clone the repo, `dotnet build` the sample (seeing source-gen diagnostics), run the triage module (seeing trace output), optimize it with `BootstrapRandomSearch` (seeing candidate search), save/load the optimized parameters, and evaluate on a dev set. Demonstrable in under 10 minutes.

---

## 9. Ecosystem Dependencies

| Dependency | NuGet Package | Provides | Status |
|---|---|---|---|
| **IChatClient** | [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI) v10.0+ | Unified LM abstraction — the only LM dependency. No Semantic Kernel, no MAF. | ✅ GA |
| **Evaluation** | [`Microsoft.Extensions.AI.Evaluation.Quality`](https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation.Quality) | Built-in evaluators: `GroundednessEvaluator`, coherence, fluency, relevance | ✅ GA |
| **OpenTelemetry** | [`OpenTelemetry`](https://www.nuget.org/packages/OpenTelemetry) v1.10+ | Distributed tracing (`ActivitySource`), metrics (`Meter`), in-process export (`InMemoryExporter`) | ✅ Stable |

> **Why This Matters:** Every dependency is GA and actively maintained. LMP builds on `IChatClient` — Microsoft's lowest-level AI abstraction — not on higher-level frameworks that may evolve independently.

---

## 10. Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| **DSPy community confusion** — perceived as "just a C# port" | Medium | Clear "inspired by, not a port" positioning. Separate `TInput`/`TOutput` design and source-gen architecture are fundamentally different. Use "LMP" name in user-facing surfaces. |
| **API stability of M.E.AI** — breaking `IChatClient` changes | Medium | LMP depends only on `IChatClient` — the thinnest possible surface. Pin NuGet versions. |
| **Ecosystem adoption** — .NET has less ML community momentum | Medium | First-mover advantage. Target enterprise .NET shops (large market) rather than ML researchers. |
| **Optimizer sophistication** — `BootstrapRandomSearch` may be insufficient | Low (MVP) | Proves architecture. `MIPROv2` and ML.NET AutoML backends are pluggable post-MVP extensions. |

---

*This document is derived from `spec.org`. If any downstream document conflicts with the spec, the spec wins.*
