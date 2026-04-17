# Phased Implementation Plan

> **Derived from:** [System Architecture](system-architecture.md) (v2)
>
> **Target:** .NET 10 / C# 14
>
> **Audience:** Developers implementing the LMP framework, phase by phase.

---

## Phase Dependency Graph

```
Phase 1: Abstractions
    │
    ▼
Phase 2: Source Generator + Core Predictor
    │
    ▼
Phase 3: Reasoning Modules
    │
    ├────────────────────────┐
    ▼                        ▼
Phase 4: Evaluation +    Phase 5: Agents + RAG
BootstrapFewShot             │
    │    ┌───────────────────┘
    ▼    ▼
Phase 6: Advanced Optimization ◄── requires Phase 4 + Phase 5
    │
    ▼
Phase 7: Tooling
    │
    ▼
Phase 8: Advanced
```

---

## Phase 1: Abstractions

> **Status:** ✅ Complete

Definethe foundational types in `LMP.Abstractions`. No runtime, no code generation — just the type contracts that every later phase builds on.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `LmpSignatureAttribute` | Simple | `[LmpSignature("instructions")]` attribute placed on `partial record` output types |
| `Predictor<TInput, TOutput>` class | Medium | Core primitive that binds an input type to an output type; exposes `PredictAsync`, learnable `Demos`, `Instructions`, `ChatOptions Config` |
| `LmpModule` base class | Medium | Abstract `ForwardAsync()`, source-gen-friendly `GetPredictors()` slot, `SaveAsync()` / `LoadAsync()` |
| `Example<TInput, TLabel>` | Simple | Training data record with `WithInputs()` to extract the input portion |
| `Trace` | Simple | Records `(predictor, inputs, outputs)` tuples during execution |
| `IRetriever` | Simple | RAG interface: `RetrieveAsync(query, k) → string[]` |
| `IOptimizer` | Simple | `OptimizeAsync<TModule>(module, trainSet, metric?)` returning the same module type with parameters filled |
| `LmpAssert` / `LmpSuggest` | Simple | Runtime assertion (retry/backtrack) and soft assertion (warning, no retry) |
| Unit tests | Simple | Attribute construction, record equality, `with` expressions, interface contracts |

### Entry Criteria

- Repository skeleton exists (`LMP.sln`, project files, `Directory.Build.props`)
- `dotnet build` passes with zero errors on the empty solution

### Exit Criteria

- `LMP.Abstractions` compiles with zero warnings
- A consumer project can declare `[LmpSignature("...")]` output records and reference `Predictor<TInput, TOutput>`
- All interface contracts have XML doc comments
- Unit tests pass for record equality, attribute instantiation, and `Example<TInput, TLabel>.WithInputs()`

### Key APIs

```csharp
// Attribute — placed on output types only
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket { ... }

// Core primitive
public class Predictor<TInput, TOutput> { ... }

// Composable module
public abstract class LmpModule
{
    public virtual IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => [];
    public virtual Task SaveAsync(string path, CancellationToken cancellationToken = default);
    public virtual Task LoadAsync(string path, CancellationToken cancellationToken = default);
}

// Training data
public record Example<TInput, TLabel>(TInput Input, TLabel Label);

// Retrieval
public interface IRetriever
{
    Task<string[]> RetrieveAsync(string query, int k);
}

// Optimization contract
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

---

## Phase 2: Source Generator + Core Predictor

> **Status:** ✅ Complete

Wirethe source generator to `[LmpSignature]` output types and make `Predictor<TInput, TOutput>` actually call an LM. This is the first phase where an LM call runs end-to-end.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `LmpSourceGenerator` : `IIncrementalGenerator` | Complex | Discovers `[LmpSignature]` types, reads `TInput` + `TOutput` metadata at build time |
| `PromptBuilder<TInput, TOutput>` (generated) | Complex | Assembles `ChatMessage[]` from instructions, demos, and input fields. Field names/descriptions baked in as string constants. |
| `JsonTypeInfo<TOutput>` (generated) | Medium | Zero-reflection `System.Text.Json` source gen for structured output — AOT-safe |
| `GetPredictors()` (generated) | Medium | Emits predictor discovery on `LmpModule` subclasses — no runtime reflection |
| Diagnostic LMP001: missing `[Description]` | Simple | Warning when output properties lack `[Description]` attribute |
| Diagnostic LMP002: non-serializable output | Simple | Error when `TOutput` contains types `System.Text.Json` cannot handle |
| Working `Predictor<TInput, TOutput>` | Complex | `PredictAsync` → source-gen `PromptBuilder` → `ChatMessage[]` → `IChatClient.GetResponseAsync<TOutput>()` |
| Snapshot / golden tests | Medium | Verify deterministic generated output with Verify library |
| Integration test with `FakeChatClient` | Medium | One predictor, end-to-end: input → prompt → mock LM → typed output |

### Entry Criteria

- Phase 1 complete: all abstractions compile, unit tests pass
- `Microsoft.Extensions.AI` package reference configured

### Exit Criteria

- `dotnet build` on a project with `[LmpSignature]` produces `*.g.cs` files containing `PromptBuilder` and `JsonTypeInfo`
- `Predictor<TicketInput, ClassifyTicket>.PredictAsync()` runs against `FakeChatClient` and returns a typed `ClassifyTicket`
- Rebuilding produces byte-identical generated source (determinism)
- LMP001 fires on properties without `[Description]`; LMP002 fires on non-serializable output types
- Snapshot tests pass; integration test passes deterministically

### Key APIs

```csharp
// Input — plain C# record, no LMP attributes required
public record TicketInput(
    [Description("The raw ticket text")] string TicketText);

// Output — partial record with [LmpSignature]
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1-5")]
    public required int Urgency { get; init; }
}

// Usage — core predict flow
var client = new ChatClientBuilder(new OpenAIChatClient("gpt-4o-mini")).Build();
var classifier = new Predictor<TicketInput, ClassifyTicket>(client);
var result = await classifier.PredictAsync(new TicketInput("I was charged twice"));
// result.Category == "billing", result.Urgency == 4
```

### Source Generator Reads Input Descriptions From

1. XML doc comments (`/// <param name="X">...</param>`)
2. `[Description]` on constructor parameters
3. `[Description]` on properties

---

## Phase 3: Reasoning Modules

> **Status:** ✅ Complete

Thinwrappers around `Predictor<TInput, TOutput>` in `LMP.Modules`. Each module is under 100 LOC — they augment the prediction strategy, not the core abstraction.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `ChainOfThought<TInput, TOutput>` | Medium | Source gen extends `TOutput` with a `Reasoning` field at build time; LM produces step-by-step reasoning before the final answer |
| `BestOfN<TInput, TOutput>` | Medium | Runs N parallel predictions via `Task.WhenAll`, returns the best by a reward function. True parallelism — no GIL. |
| `Refine<TInput, TOutput>` | Medium | Sequential improvement: predict → LM-generated critique → predict again with critique context |
| Unit tests for each module | Simple | Verify prompt shape, parallelism behavior, refinement loop termination |

### Entry Criteria

- Phase 2 complete: `Predictor<TInput, TOutput>.PredictAsync()` works end-to-end
- Source generator emits `PromptBuilder` correctly

### Exit Criteria

- `ChainOfThought<TicketInput, ClassifyTicket>` produces output with `.Reasoning` populated
- `BestOfN` invokes N parallel calls and selects the best result by reward function
- `Refine` executes at least two rounds (predict → critique → predict) and returns an improved result
- All module tests pass against `FakeChatClient`

### Key APIs

```csharp
// Chain of Thought
var cot = new ChainOfThought<TicketInput, ClassifyTicket>(client);
var result = await cot.PredictAsync(input);
// result has .Reasoning + .Category + .Urgency

// Best of N
var best = new BestOfN<TicketInput, ClassifyTicket>(client, n: 5,
    reward: (input, output) => output.Urgency >= 1 && output.Urgency <= 5 ? 1f : 0f);
var result = await best.PredictAsync(input);

// Refine
var refiner = new Refine<TicketInput, ClassifyTicket>(client, maxRounds: 3);
var result = await refiner.PredictAsync(input);
```

---

## Phase 4: Evaluation + BootstrapFewShot

> **Status:** ✅ Complete

Addthe `Evaluator` and the two foundational optimizers in `LMP.Optimizers`. This is the first phase where LMP can programmatically improve an LM program — DSPy's core insight.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `Evaluator` | Medium | Runs a module on a dev set, scores with a metric function, aggregates results via `Parallel.ForEachAsync` |
| `BootstrapFewShot` | Complex | Runs a teacher module on training set, collects successful traces (metric passes), fills `predictor.Demos` with those traces as few-shot examples |
| `BootstrapRandomSearch` | Medium | `BootstrapFewShot` × N candidates with `Task.WhenAll`; returns the best by evaluation score |
| JSONL dataset loader | Simple | Parse typed `Example<TInput, TLabel>` records from JSONL files |
| `SaveAsync()` / `LoadAsync()` implementation | Medium | Source-gen `JsonSerializerContext` round-trip for optimized module parameters — AOT-compatible |
| Integration test: optimize + evaluate | Complex | Full loop: train set → `BootstrapFewShot` → evaluate on dev set → verify score improvement |

### Entry Criteria

- Phase 2 complete: working `Predictor<TInput, TOutput>`
- Phase 3 complete (recommended but not strictly required — optimizers work on any `LmpModule`)

### Exit Criteria

- `Evaluator.EvaluateAsync(module, devSet, metric)` returns an aggregate score
- `BootstrapFewShot.CompileAsync()` fills `predictor.Demos` from successful teacher traces
- `BootstrapRandomSearch` runs N parallel trials and returns the best module
- Optimized module can be saved to JSON and loaded back with zero data loss
- Integration test demonstrates measurable score improvement (even with mock LM)

### Key APIs

```csharp
// Evaluate
var score = await Evaluator.EvaluateAsync(
    module,
    devSet,
    metric: (label, output) => label.Category == output.Category ? 1f : 0f);
// score == 0.87

// Bootstrap few-shot
var optimizer = new BootstrapFewShot(maxDemos: 4);
var optimized = await optimizer.CompileAsync(module, trainSet, metric);
// optimized module now has few-shot demos filled in

// Bootstrap random search
var search = new BootstrapRandomSearch(numTrials: 8);
var best = await search.CompileAsync(module, trainSet, metric);

// Save / Load
await best.SaveAsync("triage-v1.json");
var production = new TicketTriageModule(client);
await production.LoadAsync("triage-v1.json");
```

---

## Phase 5: Agents + RAG

> **Status:** ✅ Complete

Add`ReActAgent<TInput, TOutput>` and `IRetriever` implementations. These compose M.E.AI's existing tool-use infrastructure (`AIFunction`, `FunctionInvokingChatClient`) with LMP's predictor abstraction.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `ReActAgent<TInput, TOutput>` | Complex | Think → Act → Observe loop using M.E.AI's `AIFunction` for tools; `FunctionInvokingChatClient` handles dispatch |
| `IRetriever` implementations | Medium | At least one concrete retriever (e.g., in-memory vector search or Azure AI Search adapter) registered via DI |
| RAG composition example | Medium | `IRetriever` + `Predictor` composed in `LmpModule.ForwardAsync()` — demonstrates retrieval-augmented generation |
| Integration tests | Medium | ReAct agent with mock tools; RAG module with fake retriever |

### Entry Criteria

- Phase 2 complete: `Predictor<TInput, TOutput>` works
- `Microsoft.Extensions.AI` `AIFunction` / `FunctionInvokingChatClient` available

### Exit Criteria

- `ReActAgent` executes a Think → Act → Observe loop with at least one tool call
- RAG module retrieves context via `IRetriever` and passes it to a predictor
- Agent and RAG tests pass against `FakeChatClient` + mock tools / fake retriever
- `ReActAgent` is optimizable (has predictors discoverable by `GetPredictors()`)

### Key APIs

```csharp
// ReAct Agent
var tools = new[]
{
    AIFunctionFactory.Create(SearchKnowledgeBase),
    AIFunctionFactory.Create(GetAccountInfo)
};
var agent = new ReActAgent<TicketInput, TriageResult>(client, tools);
var result = await agent.PredictAsync(input);

// RAG composition in a module
public class RagTriageModule : LmpModule
{
    private readonly IRetriever _retriever;
    private readonly Predictor<ContextualInput, ClassifyTicket> _classify;

    public async Task<ClassifyTicket> ForwardAsync(TicketInput input)
    {
        var docs = await _retriever.RetrieveAsync(input.TicketText, k: 3);
        var augmented = new ContextualInput(input.TicketText, string.Join("\n", docs));
        return await _classify.PredictAsync(augmented);
    }
}
```

---

## Phase 6: Advanced Optimization

> **Status:** ✅ Complete

Add`MIPROv2` — Bayesian optimization over both instructions and demos. This is the most sophisticated optimizer, requiring a search backend.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `MIPROv2` optimizer | Complex | Bayesian search over instruction variants + demo subsets; proposes candidates, evaluates, and converges |
| Search backend integration | Complex | `ISampler` abstraction (Propose/Update pattern) with `CategoricalTpeSampler` (Tree-structured Parzen Estimator) and `SmacSampler` (random forest + Expected Improvement) backends |
| Instruction proposal module | Medium | LM-generated instruction candidates based on dataset analysis and prior trial results |
| Multi-objective scoring | Medium | Weighted objective aggregation — accuracy, latency, cost trade-offs |
| Integration test: MIPROv2 end-to-end | Complex | Full Bayesian optimization loop on a sample program |

### Entry Criteria

- Phase 4 complete: `Evaluator`, `BootstrapFewShot`, and `BootstrapRandomSearch` working
- Phase 5 complete (recommended — ensures agent modules are also optimizable)

### Exit Criteria

- `MIPROv2.CompileAsync()` runs a Bayesian optimization loop over instructions + demos
- Trials converge toward higher scores across successive iterations (demonstrable on mock data)
- ML.NET AutoML tuner backend (or equivalent) is wired and functional
- Integration test shows `MIPROv2` outperforms `BootstrapRandomSearch` on a multi-dimensional search space (even if marginally, on mock data)

> **Status:** ✅ Complete — ISampler abstraction with CategoricalTpeSampler and SmacSampler backends implemented. GEPA multi-objective optimizer also implemented.

### Key APIs

```csharp
// MIPROv2 optimizer
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 50,
    maxDemos: 4,
    numInstructionCandidates: 10,
    // Optional: custom sampler backend
    samplerFactory: dims => new CategoricalTpeSampler(dims));

var optimized = await mipro.CompileAsync(module, trainSet, metric);
```

---

## Phase 7: Tooling

> **Status:** ✅ Complete

Shipthe CLI tool and Aspire integration. These are developer-experience layers — the core framework works without them, but they make optimization workflows accessible from CI/CD and the Aspire dashboard.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| `dotnet lmp optimize` CLI command | Medium | Wraps `IOptimizer` — loads module, runs optimization, writes saved parameters to disk |
| `dotnet lmp eval` CLI command | Medium | Loads module + dataset, runs `Evaluator`, prints report |
| `dotnet lmp inspect` CLI command | Simple | Pretty-prints saved module parameters JSON |
| Aspire integration (`LMP.Aspire`) | Medium | `AddLmpOptimizer()` extension — integrates optimization runs as Aspire resources with dashboard telemetry |
| Sample data + end-to-end demo | Simple | 10–20 training examples, 10 validation examples; demo script runs in under 15 minutes |

### Entry Criteria

- Phase 4 complete: optimizers and evaluator working
- Phase 6 recommended for `MIPROv2` CLI support (but not required for `BootstrapFewShot` workflows)

### Exit Criteria

- `dotnet lmp optimize --module TicketTriageModule --train train.jsonl --dev dev.jsonl` runs successfully
- `dotnet lmp eval` prints per-metric scores and aggregate
- Aspire dashboard shows optimization progress and traces (if Aspire integration is included)
- Full demo script executes without errors; mock mode works without an API key

### Key APIs

```bash
# Optimize a module from the CLI
dotnet lmp optimize \
  --module TicketTriageModule \
  --optimizer BootstrapRandomSearch \
  --train data/train.jsonl \
  --dev data/dev.jsonl \
  --output triage-v1.json

# Evaluate
dotnet lmp eval \
  --module TicketTriageModule \
  --params triage-v1.json \
  --data data/dev.jsonl

# Inspect saved parameters
dotnet lmp inspect triage-v1.json
```

```csharp
// Aspire hosting
var builder = DistributedApplication.CreateBuilder(args);
builder.AddLmpOptimizer<TicketTriageModule>("triage-optimizer")
    .WithTrainData("data/train.jsonl")
    .WithDevData("data/dev.jsonl");
```

---

## Phase 8: Advanced

> **Status:** ✅ Complete

Experimentalfeatures that push the .NET platform advantage further. These are individually scoped — each can ship independently.

### Deliverables

| Deliverable | Complexity | Description |
|---|---|---|
| C# 14 interceptors | Complex | Zero-dispatch `PredictAsync` optimization — compiler rewrites call sites to skip virtual dispatch and inline the source-gen prompt builder directly |
| `[Predict]` partial method sugar | Medium | Attribute on a `partial` method in an `LmpModule`; source gen emits the method body (`PromptBuilder` → `PredictAsync`) |
| `ProgramOfThought<TInput, TOutput>` | Complex | LM generates C# code → Roslyn scripting executes it → returns structured result. C# all the way down — no Deno, no Python. |

### Entry Criteria

- Phase 2 complete: source generator and `Predictor<TInput, TOutput>` stable
- C# 14 interceptor feature available in the target SDK

### Exit Criteria

- Interceptor-rewritten `PredictAsync` calls produce identical results to the standard path (verified by differential tests)
- `[Predict]` partial methods compile and execute correctly, with source-gen emitting the full method body
- `ProgramOfThought` executes LM-generated C# via Roslyn scripting and returns typed output
- All advanced features are opt-in — default behavior is unchanged

### Key APIs

```csharp
// [Predict] sugar — source gen fills in the body
public partial class TicketTriageModule : LmpModule
{
    [Predict]
    public partial Task<ClassifyTicket> ClassifyAsync(TicketInput input);

    [Predict]
    public partial Task<DraftReply> DraftAsync(ClassifyTicket classification);

    public async Task<DraftReply> ForwardAsync(TicketInput input)
    {
        var classification = await ClassifyAsync(input);
        return await DraftAsync(classification);
    }
}

// ProgramOfThought
var pot = new ProgramOfThought<MathInput, MathResult>(client);
var result = await pot.PredictAsync(new MathInput("What is the 10th Fibonacci number?"));
// LM generates C# → Roslyn scripting runs it → result.Answer == 55
```

---

## Summary Table

| Phase | Name | Status | Key Risk | Depends On |
|---|---|---|---|---|
| 1 | Abstractions | ✅ Complete | Over-engineering interfaces before usage patterns are clear | — |
| 2 | Source Generator + Core Predictor | ✅ Complete | Generator debugging; `netstandard2.0` API constraints | 1 |
| 3 | Reasoning Modules | ✅ Complete | Source gen extending `TOutput` for `ChainOfThought` | 2 |
| 4 | Evaluation + BootstrapFewShot | ✅ Complete | Teacher trace collection reliability | 2 |
| 5 | Agents + RAG | ✅ Complete | M.E.AI `FunctionInvokingChatClient` integration surface | 2 |
| 6 | Advanced Optimization | ✅ Complete | ISampler abstraction complexity (CategoricalTpeSampler, SmacSampler) | 4, 5 |
| 7 | Tooling | ✅ Complete | CLI ergonomics; Aspire API surface stability | 4 |
| 8 | Advanced | ✅ Complete | C# 14 interceptor API newness; `[Predict]` partial method sugar | 2 |
| A | Unified Pipeline Seams | 🔄 In Progress | `IOptimizer` breaking change migration across 6 impls | 1–8 |
| B | Context-ify Algorithms | ⬜ Pending | SIMBA architecture; `UseLmpTrace()` MEAI middleware | A |
| C | TypedParameterSpace + ISearchStrategy | ⬜ Pending | `ISampler` → `ISearchStrategy` breaking change; source-gen `SearchSpaceDescriptor` | A |
| D | Vertical Targets + MEAI Middleware | ⬜ Pending | `ChatClientTarget`; `.UseOptimized()` | C |
| E | Tools Axis (AITool-native) | ⬜ Pending | `AITool` pool; Breaking E.1 `ReActAgent` | C |
| F | Trajectory + Multi-turn | ⬜ Pending | `Trajectory` type; `AgentThread` conversion | C |
| G | Skills Axis | ⬜ Pending | Source-gen skill manifests; `.NET-unique differentiator | C |
| H | Cost as Metric Dimension | ⬜ Pending | `MetricVector`; `EvaluationCritique` + GEPA bridge | D, E, F, G |

---

## Phase A: Unified Pipeline Seams

> **Status:** 🔄 In Progress
> **Branch:** `feat/unified-optimization`

Introduce the unified optimization pipeline scaffolding without changing existing optimizer behavior.

### New in `LMP.Abstractions`

- `IOptimizationTarget` — vertical seam; `ExecuteAsync`, `WithParameters`, `GetParameterSpace`
- `TargetShape` (SingleTurn | MultiTurn), `TargetState`
- `OptimizationContext` — pipeline carrier: target, train/dev sets, metric, budget, spaces, history, logs
- `CostBudget` — multi-dimensional: `MaxTokens`, `MaxTurns`, `MaxWallClock`, `Custom(Func<TrialCost, bool>)`
- `TrialHistory`, `ReflectionLog` (stub), `TypedParameterSpace` (stub — empty record)
- `OptimizationAxis` enum, `Goal` enum, `OptimizationProgress` record

### Breaking A.1 — `IOptimizer` refactor

- Remove `CompileAsync<TModule>` from `IOptimizer` interface
- Add `Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct)` as the new required method
- Add `OptimizerExtensions.CompileAsync<TModule>()` as extension method in `LMP.Core`
- All 6 implementations add `OptimizeAsync` wrapping existing logic (mechanical migration)
- `IOptimizerTests.cs` stub updated (1 file, ~10 lines) — ONLY test file needing changes

### New in `LMP.Core`

- `ModuleTarget : IOptimizationTarget`
- `OptimizationPipeline : IOptimizer` — fluent builder
- `OptimizationResult` record
- `LmpModuleOptimizationExtensions` — `.OptimizeAsync()`, `.AsOptimizationPipeline()`
- `OptimizerExtensions` — backward-compat `CompileAsync<TModule>()`
- `Lmp` static class — `Lmp.Optimize.AutoAsync()` Tier 4 façade

---

## Phase B: Context-ify Algorithms

> **Status:** ⬜ Pending

Wire all existing algorithms to read/write `OptimizationContext` fields.

1. `BootstrapFewShot` — reads `TrainSet`/`Target` → writes `SearchSpace` (demo pools)
2. `GEPA` — reads `Trace`/`ReflectionLog`/`TrainSet` → writes `SearchSpace`, `ReflectionLog`, `ParetoFrontier`
3. `MIPROv2` — injectable `ISearchStrategy`; reads `SearchSpace`/`TrialHistory`/`Budget` → writes `TrialHistory`/`Target`
4. `Z3Feasibility` — reads `SearchSpace` → writes constrained `SearchSpace`
5. `SIMBA` (new) — mini-batch stochastic ascent + self-reflection; inline updates `Target`
6. `BayesianCalibration` (new) — reads `TrialHistory` → writes refined `Target`
7. `AutoSampler` wired as default for `MIPROv2`
8. `ChatClientBuilder.UseLmpTrace(ctx)` middleware — replaces manual `trace?.Record()` in `Predictor.PredictAsync()`

---

## Phase C: TypedParameterSpace + ISearchStrategy

> **Status:** ⬜ Pending

**Breaking C.1**: `ISampler` → `ISearchStrategy` (one version window with `LegacyCategoricalAdapter`).

Full `TypedParameterSpace` (6 kinds: Categorical, Integer, Continuous, StringValued, Subset, Composite).
`ParameterAssignment` strongly-typed.

Source-gen additions:
- `[Hyperparameter(Min, Max)]`, `[Instruction]` → emit `TypedParameterSpace` entries
- `SearchSpaceDescriptor` — compile-time cardinality hints from `[LmpSignature]` enum members,
  cross-predictor dependency graph, stable parameter-name schema for Z3 and samplers

---

## Phase D: Vertical Targets + MEAI Middleware

> **Status:** ⬜ Pending

- `ChatClientTarget` — wraps `IChatClient`; system prompt = `StringValued`; temperature = `Continuous`; tools = `Subset(Pool: IReadOnlyList<AITool>)`
- `DelegateTarget` — wraps `Func<TIn, Task<TOut>>`
- `ChainTarget` — ordered composition
- `.UseOptimized(study)` MEAI middleware — optimization result as `IChatClient` wrapper

---

## Phase E: Tools Axis (AITool-native)

> **Status:** ⬜ Pending

**Breaking E.1**: `ReActAgent(IEnumerable<AITool>)` — backward compat: `AIFunction IS-A AITool`.

- `[Tool]` on `LmpModule` methods → source-gen Pipeline 7 → `Subset(Pool: IReadOnlyList<AITool>)`
- `GEPA` evolves `AIFunction.Description`
- `Z3Feasibility` constraints over `AITool` pool
- `MIPROv2` optimizes tool subset selection

---

## Phase F: Trajectory + Multi-turn

> **Status:** ⬜ Pending

- `Trajectory` type: ordered `Turn[]`, per-turn rewards, attribution metadata
- `ITrajectoryMetric` (sibling to `IMetric`)
- `[Trajectory]` source-gen
- `AgentThread` (Agent Framework) → `Trajectory` conversion
- `GEPA` and `SIMBA` extended to operate on trajectories

---

## Phase G: Skills Axis (.NET Differentiator)

> **Status:** ⬜ Pending

- `[Skill]` attribute, `SKILL.md` spec
- Source-gen Pipeline 8: typed skill manifest + `Subset` in `TypedParameterSpace`
- Skill compatibility graph → Z3 constraints
- `ContextualBandit` for per-request routing

---

## Phase H: Cost as Metric Dimension

> **Status:** ⬜ Pending

- `MetricVector` + `IMetric` upgrade (non-breaking default interface method)
- `ParetoFrontier<TTarget>` multi-objective-native
- `RouteLLM`, `MultiFidelity`, `EvaluationCritique` optimizers
- `EvaluationCritique`: M.E.AI evaluator rationale text → `ReflectionLog` → GEPA free reflection signal
- Budget enforcement: advisory per-step, mandatory at pipeline

---

## What's Intentionally Excluded

Concepts from earlier drafts that have been dropped — see [System Architecture § What's Intentionally Excluded](system-architecture.md#whats-intentionally-excluded) for rationale:

| Dropped Concept | Replacement |
|---|---|
| Directed program graphs / `ProgramGraph` / `Step` | `LmpModule.ForwardAsync()` — plain C# composition |
| Intermediate Representation (IR) records | No IR layer. Source gen works directly on C# types. |
| `[Input]` / `[Output]` field direction attributes | `TInput` and `TOutput` are separate types — direction is structural |
| `LmpProgramAttribute` | `LmpModule` subclasses — no attribute needed |
| Three-tier binding system | `Predictor<TInput, TOutput>` with source-gen `PromptBuilder` |
| 7+ diagnostics + code fixes | Start with 2–3; add more based on real usage |
| MSBuild targets / Three-layer build | One source generator, `dotnet build` does everything |
| TPL Dataflow graph execution | Sequential `await` in `ForwardAsync()` — compose with normal C# |
| Infer.NET statistical layer | Too heavy for general use; future opt-in package |
| Interceptor-inlined optimized prompts | Post-Phase-H; `[EmbeddedModuleParams]` future |
| Aspire fan-out for parallel MIPROv2 trials | Future distributed capability |
| Visual pipeline designer | Every framework that built one regretted it |
| ArCHer RL | Research still consolidating; deferred 2026+ |

