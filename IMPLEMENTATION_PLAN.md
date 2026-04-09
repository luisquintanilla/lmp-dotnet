# LMP Implementation Plan

> **Status:** Planning complete — no code exists yet (`src/` is empty)
>
> **Target:** .NET 10 / C# 14
>
> **Authoritative specs:** `docs/02-specs/` (public-api, source-generator, runtime-execution, compiler-optimizer, artifact-format, diagnostics)
>
> **Project structure** (from `AGENTS.md` / `system-architecture.md`):
> ```
> src/
> ├── LMP.Abstractions/      # Interfaces, attributes, base types (no dependencies)
> ├── LMP.Core/               # Predictor<TIn,TOut>, LmpModule, assertions
> ├── LMP.SourceGen/          # Roslyn IIncrementalGenerator (netstandard2.0)
> ├── LMP.Modules/            # ChainOfThought, BestOfN, Refine, ReActAgent
> └── LMP.Optimizers/         # Evaluator, BootstrapFewShot, BootstrapRandomSearch
> test/
> ├── LMP.Abstractions.Tests/
> ├── LMP.Core.Tests/
> ├── LMP.SourceGen.Tests/
> ├── LMP.Modules.Tests/
> └── LMP.Optimizers.Tests/
> samples/
> └── LMP.Samples.TicketTriage/
> ```

> **Note on `docs/03-implementation/`:** `repo-layout.md` and `testing-strategy.md` describe a **v1 design** with dropped concepts (IR graphs, StepDescriptor, binding tiers, TPL Dataflow, 9-project layout). This plan follows the **current** specs in `docs/02-specs/` and the simplified 5-project structure from `AGENTS.md`.

---

## Gap Analysis

**Existing code:** None. No `src/` directory, no `.cs`/`.csproj`/`.sln` files.

| Component | Spec Reference | Status |
|---|---|---|
| Solution skeleton (slnx, props, global.json) | `AGENTS.md` | ✅ Complete |
| `LMP.Abstractions` — attributes, interfaces, base types | `public-api.md` | ❌ Not started |
| `LMP.Core` — Predictor, LmpModule, assertions | `runtime-execution.md` §2–3, §6 | ❌ Not started |
| `LMP.SourceGen` — IIncrementalGenerator | `source-generator.md` | ❌ Not started |
| `LMP.Modules` — CoT, BestOfN, Refine, ReAct | `runtime-execution.md` §4–5 | ❌ Not started |
| `LMP.Optimizers` — Evaluator, Bootstrap* | `compiler-optimizer.md` | ❌ Not started |
| Diagnostics LMP001–LMP003 | `diagnostics.md` | ❌ Not started |
| Artifact save/load (JSON) | `artifact-format.md` | ❌ Not started |
| Test projects (5) | `AGENTS.md` | ❌ Not started |

---

## Dependency Graph

```
Phase 0 ─→ Phase 1 ─→ Phase 2 ─┬─→ Phase 3
                                 │
                                 ├─→ Phase 4 ─→ Phase 6
                                 │      │
                                 ├─→ Phase 5  │
                                 │      │     │
                                 │      └──→ Phase 7
                                 │
                                 └─→ Phase 8
```

**Hard deps:** 0→1→2. Then 2→3, 2→4, 2→5, 2→8. 4→6, 4→7.
**Soft deps:** Phase 3 recommended before Phase 4. Phase 5 recommended before Phase 6.

**MVP boundary: Phases 0–5.** After Phase 5 a developer can author, build, compose, evaluate, optimize, and save/load LM programs.

---

## Phase 0 — Repository Skeleton

**Goal:** Empty solution that compiles — the foundation for everything.

**Spec:** `AGENTS.md`, `docs/01-architecture/system-architecture.md` §Project Structure

| # | Task | Completion Criteria | Status |
|---|------|---------------------|--------|
| 0.1 | Create `global.json` pinning .NET 10 SDK | `dotnet --version` shows 10.x | ✅ Done |
| 0.2 | Create `Directory.Build.props`: `net10.0`, `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` | All projects inherit settings | ✅ Done |
| 0.3 | Create `Directory.Packages.props` for central package management (`Microsoft.Extensions.AI`, `Microsoft.CodeAnalysis.*`, `xunit`, `Moq`) | Versions centralized in one file | ✅ Done |
| 0.4 | Create `LMP.slnx` with solution folders `src/`, `test/` | `dotnet sln list` shows all projects | ✅ Done |
| 0.5 | Create `LMP.Abstractions` classlib (`net10.0`, `RootNamespace=LMP`) — no project references | Compiles | ✅ Done |
| 0.6 | Create `LMP.Core` classlib → refs `LMP.Abstractions` + `Microsoft.Extensions.AI` | Compiles | ✅ Done |
| 0.7 | Create `LMP.SourceGen` classlib → `netstandard2.0`, `<IsRoslynComponent>true`, `<EnforceExtendedAnalyzerRules>true`, `Microsoft.CodeAnalysis.CSharp` 5.3.0 | Compiles | ✅ Done |
| 0.8 | Create `LMP.Modules` classlib → refs `LMP.Core` | Compiles | ✅ Done |
| 0.9 | Create `LMP.Optimizers` classlib → refs `LMP.Core` | Compiles | ✅ Done |
| 0.10 | Create 5 xUnit test projects, each referencing its src counterpart | `dotnet test` passes (0 tests) | ✅ Done |
| 0.11 | Wire `LMP.SourceGen` as analyzer ref in `LMP.Core`: `<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` | Source gen in build pipeline | ✅ Done |
| 0.12 | Create `.editorconfig` with C# conventions | Code style enforced | ✅ Done |

**Entry:** Repo exists with `docs/`, `AGENTS.md`.
**Exit:** `dotnet build LMP.slnx && dotnet test LMP.slnx` — 0 errors, 0 warnings. ✅ COMPLETE

> **Note:** .NET 10 SDK creates `.slnx` format by default (not `.sln`).

---

## Phase 1 — Abstractions

**Goal:** Define all foundational types in `LMP.Abstractions` and `LMP.Core`. No runtime logic, no code generation — purely type contracts.

**Spec:** `public-api.md`, `artifact-format.md` §3, `runtime-execution.md` §2.4/§6

| # | Task | Completion Criteria |
|---|------|---------------------|
| 1.1 | **`LmpSignatureAttribute`** — `[AttributeUsage(Class, AllowMultiple=false, Inherited=false)]`, constructor `(string instructions)`, property `string Instructions`. Namespace `LMP`. | Test: construct attribute, verify `Instructions` round-trips |
| 1.2 | **`IPredictor` interface** (non-generic, for optimizer enumeration) — `Name` (get/set), `Instructions` (get/set), `Demos` (`IList`), `Config` (`ChatOptions`), `GetState()` → `PredictorState`, `LoadState(PredictorState)` | Test: stub class implements interface without error |
| 1.3 | **Artifact records** — `ModuleState` { Version, Module, Predictors `Dictionary<string, PredictorState>` }, `PredictorState` { Instructions, Demos `List<DemoEntry>`, Config `Dictionary<string, JsonElement>?` }, `DemoEntry` { Input, Output as `Dictionary<string, JsonElement>` }. Per `artifact-format.md` §3. | Test: construct each, verify equality/`with` expressions |
| 1.4 | **`ModuleStateSerializerContext`** — `[JsonSerializable(typeof(ModuleState))]` with camelCase, WriteIndented, WhenWritingNull options | Test: serialize → deserialize `ModuleState` round-trip via context |
| 1.5 | **`Example<TInput, TLabel>`** — Record `(TInput Input, TLabel Label)` with `WithInputs()` → `TInput` | Test: construct, `WithInputs()` returns input; record equality works |
| 1.6 | **`Trace` + `TraceEntry`** — `Trace` holds `List<TraceEntry>`, has `Record(string name, object input, object output)`. `TraceEntry` is record `(string PredictorName, object Input, object Output)` | Test: record entries, verify list contents |
| 1.7 | **`LmpAssert`** — Static `That<T>(T result, Func<T, bool> predicate, string? message)` throws `LmpAssertionException` on failure | Test: passing predicate no-ops; failing throws with message |
| 1.8 | **`LmpSuggest`** — Static `That<T>(T result, Func<T, bool> predicate, string? message)` returns `bool`, never throws | Test: failing predicate returns false without throwing |
| 1.9 | **`LmpAssertionException`** — `Message` + `object? FailedResult`. **`LmpMaxRetriesExceededException`** — `PredictorName` + `MaxRetries`. | Test: construct, verify properties |
| 1.10 | **`IRetriever`** — `Task<string[]> RetrieveAsync(string query, int k = 5, CancellationToken ct = default)` | Test: stub implements interface |
| 1.11 | **`IOptimizer`** — `Task<TModule> CompileAsync<TModule, TInput, TLabel>(TModule module, IReadOnlyList<Example<TInput, TLabel>> trainSet, Func<Example<TInput, TLabel>, object, float> metric, CancellationToken ct) where TModule : LmpModule` | Test: stub implements interface |
| 1.12 | **`LmpModule`** — Abstract base class. `Trace?` property. Abstract `GetPredictors()` → `IReadOnlyList<(string Name, IPredictor Predictor)>`. Concrete `SaveAsync`/`LoadAsync` (impl deferred; can throw `NotImplementedException` until Phase 4). | Test: subclass compiles, overrides GetPredictors |
| 1.13 | **`Predictor<TInput, TOutput>` shell** in `LMP.Core` — Constructor `(IChatClient)`. Properties: `Demos` (`List<(TInput, TOutput)>`), `Instructions`, `Config` (`ChatOptions`), `Name`. Implements `IPredictor`. `PredictAsync` throws `NotImplementedException` (wired in Phase 2). `where TOutput : class`. | Test: construction, property defaults, IPredictor satisfaction |

**Entry:** Phase 0 complete.
**Exit:** `LMP.Abstractions` + `LMP.Core` compile with 0 warnings. Consumer project references all types. All unit tests pass.

---

## Phase 2 — Source Generator + Core Predictor

**Goal:** Wire the source generator to `[LmpSignature]` output types and make `Predictor<TIn, TOut>.PredictAsync()` work end-to-end.

**Spec:** `source-generator.md`, `runtime-execution.md` §2, `diagnostics.md`

### 2A — Generator Infrastructure

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2A.1 | **`[Generator] LmpSourceGenerator : IIncrementalGenerator`** — Register pipeline with `ForAttributeWithMetadataName("LMP.LmpSignatureAttribute")` | Generator discovered by compiler (Roslyn test harness) |
| 2A.2 | **`EquatableArray<T>`** helper — Wraps `ImmutableArray<T>` with element-wise equality for incremental caching | Test: two arrays with same elements are equal |
| 2A.3 | **Model records** — `OutputTypeModel`(Namespace, TypeName, Instructions, `EquatableArray<OutputFieldModel>` OutputFields, IsPartialRecord). `OutputFieldModel`(Name, ClrTypeName, Description, IsRequired). Never store `ISymbol`/`SyntaxNode`. | Test: model equality works |
| 2A.4 | **`ExtractOutputModel` transform** — Read `[LmpSignature]` attribute, extract TOutput properties. Description priority: XML docs > `[Description]` on params > `[Description]` on properties. | Test: model has correct fields and descriptions from known source |

### 2B — Diagnostics

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2B.1 | **LMP003** — Error when `[LmpSignature]` on non-`partial record`. Skip generation. | Test: `public record Foo` → LMP003; `public partial class Foo` → LMP003 |
| 2B.2 | **LMP001** — Warning when output property lacks `[Description]`. Continue generation; use property name as fallback. | Test: property without `[Description]` → LMP001 with property name |
| 2B.3 | **LMP002** — Error when output property type not JSON-serializable (`Delegate`, `IntPtr`, `Span<T>`, `Stream`, etc.). Skip generation. | Test: `Action<string>` property → LMP002 |

### 2C — PromptBuilder Emission

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2C.1 | **`PromptBuilderEmitter`** — Emit `file static class {TypeName}PromptBuilder` with `BuildMessages(TInput input, IReadOnlyList<(TInput, TOutput)>? demos, string? lastError)` → `IList<ChatMessage>`. Bake instructions + field descriptions as `const string`. | Snapshot test: `.g.cs` matches golden file |
| 2C.2 | **System message** — Instructions + input/output field descriptions. Use raw string literals. | Snapshot test: system message content correct |
| 2C.3 | **Demo pairs** — User(`FormatInput(demo.Input)`) → Assistant(JSON `demo.Output`). | Test: 2 demos → 6 messages total (sys + 4 demo + 1 input) |
| 2C.4 | **Retry feedback** — When `lastError` non-null, append `"Previous attempt failed: {lastError}. Try again."` to final user message. | Test: lastError appears in last message |

### 2D — JsonTypeInfo Emission

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2D.1 | **`JsonContextEmitter`** — Emit `[JsonSerializable(typeof(TOutput))] internal partial class {TypeName}JsonContext : JsonSerializerContext` with camelCase. | Snapshot test: matches golden file |
| 2D.2 | **File headers** — `// <auto-generated />`, `#nullable enable`, `[GeneratedCode("LMP.Generators", "1.0.0")]`. Hint names: `{TypeName}.PromptBuilder.g.cs`, `{TypeName}.JsonContext.g.cs`. | Snapshot test: headers correct |

### 2E — Predictor Implementation

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2E.1 | **`PredictAsync` body** — 1) `PromptBuilder.BuildMessages(input, Demos, lastError)` → `ChatMessage[]`, 2) `_client.GetResponseAsync<TOutput>(messages, Config, ct)`, 3) record trace if `Trace` set, 4) return `TOutput`. | Integration test: `Predictor<TicketInput, ClassifyTicket>` with FakeChatClient → typed output |
| 2E.2 | **Retry loop** — Loop up to `maxRetries` (default 3). On `LmpAssertionException`, capture message, rebuild prompt with feedback, retry. After max → throw `LmpMaxRetriesExceededException`. | Test: assertion failure triggers retry; max exceeded throws |
| 2E.3 | **`GetState()` / `LoadState()`** — Serialize/restore Instructions + Demos + Config via `PredictorState` + `JsonElement` dictionaries. | Test: round-trip preserves state |
| 2E.4 | **`FakeChatClient`** — Test helper implementing `IChatClient`, returns pre-configured JSON responses keyed by system prompt. Place in shared test infrastructure. | Test: returns configured response for given signature |

### 2F — Module Predictor Discovery

| # | Task | Completion Criteria |
|---|------|---------------------|
| 2F.1 | **Module pipeline stage** — Scan classes extending `LmpModule`, extract `Predictor<,>` fields → `ModuleModel`(Namespace, TypeName, `EquatableArray<PredictorFieldModel>`). `PredictorFieldModel`(FieldName, InputTypeName, OutputTypeName). | Test: module with 2 fields → model has 2 entries |
| 2F.2 | **`GetPredictors()` emitter** — `partial class {Module} { public override ... GetPredictors() => [("name", _field), ...]; }` Strips `_` prefix for name. Hint: `{Module}.Predictors.g.cs`. | Snapshot test: matches golden file |
| 2F.3 | **End-to-end integration** — `TicketInput` + `[LmpSignature] ClassifyTicket` + module + `FakeChatClient` → `PredictAsync` returns typed result. | Test passes deterministically |

**Entry:** Phase 1 complete.
**Exit:** `dotnet build` produces `.g.cs` files. `PredictAsync` works end-to-end. Deterministic output. LMP001/002/003 fire correctly. All snapshot + integration tests pass.

---

## Phase 3 — Reasoning Modules

**Goal:** Thin wrappers (<100 LOC each) around `Predictor` that augment prediction strategy.

**Spec:** `runtime-execution.md` §4, `source-generator.md` §6

| # | Task | Completion Criteria |
|---|------|---------------------|
| 3.1 | **ChainOfThought extended record (source gen)** — When `ChainOfThought<TIn, TOut>` found, emit `internal partial record {TOut}WithReasoning` with `[Description("Think step by step")] [JsonPropertyOrder(-1)] required string Reasoning` + all original TOutput props. Emit corresponding `JsonSerializerContext`. | Snapshot test: extended record matches golden file |
| 3.2 | **`ChainOfThought<TIn, TOut>` class** — Wraps inner `Predictor<TIn, TOutWithReasoning>`. `PredictAsync`: call inner → strip Reasoning → return TOut. Reasoning in trace. Delegates Instructions/Demos/Config. Implements `IPredictor`. | Test: returns `ClassifyTicket`; reasoning captured in trace |
| 3.3 | **`BestOfN<TIn, TOut>` class** — Constructor `(IChatClient, int n, Func<TIn, TOut, float> reward)`. `PredictAsync`: N concurrent calls via `Task.WhenAll`, return highest-scored. Implements `IPredictor`. | Test: N=3, returns best; verify all 3 calls concurrent |
| 3.4 | **`Refine<TIn, TOut>` class** — Holds `_predictor` (initial) + `_refiner` (critique). Constructor `(IChatClient, int maxIterations = 2)`. `PredictAsync`: predict → loop(critique → re-predict). Both recorded in trace. | Test: maxIterations=2 → 3 LM calls total |
| 3.5 | **`RefineCritiqueInput<TOutput>` record** — Combines original input context + previous output for refiner. | Test: fields accessible |

**Entry:** Phase 2 complete.
**Exit:** All three modules work against FakeChatClient. CoT has reasoning in trace. BestOfN runs N parallel calls. Refine iterates. All tests pass.

---

## Phase 4 — Evaluation + BootstrapFewShot

**Goal:** First phase where LMP *programmatically improves* an LM program — DSPy's core insight.

**Spec:** `compiler-optimizer.md`, `artifact-format.md`

### 4A — Evaluator

| # | Task | Completion Criteria |
|---|------|---------------------|
| 4A.1 | **`Evaluator` static class** — `EvaluateAsync<TModule>(module, devSet, metric, maxConcurrency=4, ct)` using `Parallel.ForEachAsync`. Returns `EvaluationResult`. | Test: 10 examples → correct average score |
| 4A.2 | **`EvaluationResult`** — `PerExample` (list), `AverageScore`, `MinScore`, `MaxScore`, `Count`. **`ExampleResult`** — `Example`, `Output` (object), `Score`. | Test: aggregation math correct |
| 4A.3 | **Error swallowing** — If `ForwardAsync` throws, record score=0, log warning, continue. | Test: 1 of 5 throws → avg over all 5, failed=0 |

### 4B — BootstrapFewShot

| # | Task | Completion Criteria |
|---|------|---------------------|
| 4B.1 | **`BootstrapFewShot : IOptimizer`** — Constructor `(int maxDemos=4, int maxRounds=1, float metricThreshold=1.0f)` | Test: defaults correct |
| 4B.2 | **CompileAsync — trace collection** — Clone module. For each training example (parallel): enable tracing → `ForwardAsync` → score → if ≥ threshold, collect traces per predictor into `ConcurrentDictionary<string, ConcurrentBag<Demo>>`. Swallow failures. | Test: 10 examples, 7 pass → 7 traces |
| 4B.3 | **CompileAsync — demo assignment** — For each predictor: take up to `maxDemos` from successful traces → assign `Demos`. Return student. | Test: maxDemos=4, 7 traces → 4 demos per predictor |
| 4B.4 | **Module cloning** — Deep copy via source-generated helper or manual pattern. Each predictor's demos/instructions independent. | Test: modify original after clone → clone unaffected |

### 4C — BootstrapRandomSearch

| # | Task | Completion Criteria |
|---|------|---------------------|
| 4C.1 | **`BootstrapRandomSearch : IOptimizer`** — Constructor `(int numTrials=8, int maxDemos=4, float metricThreshold=1.0f, int? seed=null)` | Test: defaults correct |
| 4C.2 | **CompileAsync** — Split 80/20. Run N `BootstrapFewShot` with shuffled subsets. Evaluate all on validation split. Return best. Seed → deterministic. | Test: 3 trials → returns best; seeded runs are reproducible |

### 4D — Save/Load + JSONL

| # | Task | Completion Criteria |
|---|------|---------------------|
| 4D.1 | **`SaveAsync` implementation** — `GetPredictors()` → `ModuleState` → `ModuleStateSerializerContext` → atomic write (temp→rename). Per `artifact-format.md` §4. | Test: file exists, valid JSON, matches schema |
| 4D.2 | **`LoadAsync` implementation** — Deserialize → iterate predictors → `LoadState()`. | Test: save → load into fresh module → same state |
| 4D.3 | **Round-trip integration** — Optimize → save → load → evaluate → same score. | Test: pre/post optimization scores differ; load preserves post score |
| 4D.4 | **JSONL loader** — `Example.LoadFromJsonl<TInput, TLabel>(string path)`. One JSON object per line. | Test: 3-line JSONL → 3 typed examples |

### 4E — Full Integration

| # | Task | Completion Criteria |
|---|------|---------------------|
| 4E.1 | **Optimize-evaluate loop** — Module + FakeChatClient + train set → `BootstrapFewShot.CompileAsync` → `Evaluator.EvaluateAsync` → verify demos filled, score computable. Save/load round-trip. | End-to-end test passes |

**Entry:** Phase 2 complete (Phase 3 recommended).
**Exit:** Evaluator scores modules. BootstrapFewShot fills demos. RandomSearch finds best. Save/Load round-trips. Integration test passes.

---

## Phase 5 — Agents + RAG

**Goal:** `ReActAgent` and `IRetriever`-based RAG composition.

**Spec:** `runtime-execution.md` §5 + §7

| # | Task | Completion Criteria |
|---|------|---------------------|
| 5.1 | **`ReActAgent<TIn, TOut>`** — Constructor `(IChatClient, IEnumerable<AIFunction> tools, int maxSteps=5)`. Wraps client with `ChatClientBuilder().UseFunctionInvocation().Build()`. `PredictAsync`: build messages, set `ChatOptions.Tools`, call `GetResponseAsync<TOutput>`. Record final trace. Implements `IPredictor`. | Test: agent with mock calculator tool → calls tool, returns typed result |
| 5.2 | **Tool registration** — Document `AIFunctionFactory.Create(method)` pattern in sample. | Test: AIFunction has correct description |
| 5.3 | **`InMemoryRetriever`** — Simple `IRetriever` for testing: keyword/substring match over document list. | Test: query → matching documents returned |
| 5.4 | **RAG module example** — `ForwardAsync`: retrieve passages → augmented input → predict. | Test: retrieves context, passes to predictor, returns typed result |
| 5.5 | **Agent optimizability** — `GetPredictors()` discovers ReActAgent; optimizer can fill its demos. | Test: BootstrapFewShot can optimize a module containing ReActAgent |

**Entry:** Phase 2 complete. M.E.AI `AIFunction`/`FunctionInvokingChatClient` available.
**Exit:** ReActAgent does Think→Act→Observe. RAG module retrieves + predicts. Both optimizable. Tests pass.

---

## Phase 6 — Advanced Optimization (Post-MVP)

**Goal:** `MIPROv2` — Bayesian optimization over instructions and demos.

**Spec:** `compiler-optimizer.md` §MIPROv2

| # | Task | Completion Criteria |
|---|------|---------------------|
| 6.1 | **Instruction proposal** — LM generates N candidate instructions per predictor given task + fields + samples. | Test: N diverse instruction strings |
| 6.2 | **Minimal TPE sampler** — Categorical-only (~300 LOC). `l(x)`/`g(x)` frequency distributions, gamma=0.25. | Test: proposals favor high-scoring regions after reporting |
| 6.3 | **`MIPROv2 : IOptimizer`** — Phase 1: bootstrap demo pool. Phase 2: propose instructions. Phase 3: Bayesian search over (instruction × demo subset) per predictor. | Test: trials converge toward higher scores |
| 6.4 | **Integration** — MIPROv2 ≥ BootstrapRandomSearch on same dataset. | Test: score comparison |

**Entry:** Phase 4 complete. Phase 5 recommended.
**Exit:** MIPROv2 runs Bayesian optimization. Trials converge.

---

## Phase 7 — Tooling (Post-MVP)

**Goal:** CLI tool and developer experience.

**Spec:** `phased-plan.md` Phase 7

| # | Task | Completion Criteria |
|---|------|---------------------|
| 7.1 | **`dotnet lmp optimize`** — Loads module assembly, runs optimizer, writes artifact JSON. Uses `System.CommandLine`. | CLI produces artifact |
| 7.2 | **`dotnet lmp eval`** — Loads module + dataset, runs Evaluator, prints metric scores. | CLI prints scores |
| 7.3 | **`dotnet lmp inspect`** — Pretty-prints saved module params. | CLI shows formatted state |
| 7.4 | **Sample data** — 10-20 train + 10 validation examples for ticket triage in `data/`. | JSONL parses correctly |
| 7.5 | **End-to-end demo** — `samples/LMP.Samples.TicketTriage/` per `mvp-demo-script.md`. Works without API key (mock mode). | `dotnet run` completes < 10 min |

**Entry:** Phase 4 complete.
**Exit:** CLI commands work. Demo runs end-to-end.

---

## Phase 8 — Advanced (Post-MVP)

**Goal:** Experimental .NET platform features.

**Spec:** `phased-plan.md` Phase 8

| # | Task | Completion Criteria |
|---|------|---------------------|
| 8.1 | **`[Predict]` partial method** — Source gen emits method body (PromptBuilder → PredictAsync). | Partial methods compile + execute |
| 8.2 | **C# 14 interceptors** — Zero-dispatch PredictAsync: compiler inlines prompt builder. | Differential test: intercepted = standard output |
| 8.3 | **`ProgramOfThought<TIn, TOut>`** — LM generates C# → Roslyn scripting executes → typed result. | Sandbox execution returns correct output |

**Entry:** Phase 2 complete. C# 14 interceptors available.
**Exit:** All features opt-in. Default behavior unchanged.

---

## Implementation Priority

| Priority | Phase | Complexity | Key Risk |
|---|---|---|---|
| **P0** | 0: Repo Skeleton | Trivial | None |
| **P0** | 1: Abstractions | Simple | Over-engineering before usage patterns clear |
| **P0** | 2: Source Gen + Predictor | **Complex** | Generator debugging; netstandard2.0 API limits |
| **P1** | 3: Reasoning Modules | Medium | Source gen extending TOutput for CoT |
| **P1** | 4: Eval + BootstrapFewShot | **Complex** | Teacher trace collection reliability |
| **P1** | 5: Agents + RAG | Medium | M.E.AI FunctionInvokingChatClient surface |
| **P2** | 6: MIPROv2 | **Complex** | Bayesian search backend |
| **P2** | 7: Tooling | Medium | CLI ergonomics |
| **P3** | 8: Advanced | **Complex** | C# 14 interceptor API newness |

---

## Key Architecture Decisions

1. **Separate `TInput` / `TOutput`** — NOT DSPy's single Signature. Matches `IChatClient.GetResponseAsync<T>()`.
2. **Source generator emits:** PromptBuilder, JsonTypeInfo, GetPredictors(), CoT extended output, diagnostics.
3. **LMP depends ONLY on `IChatClient`** from `Microsoft.Extensions.AI`.
4. **`Predictor<TIn, TOut>`** — core primitive with learnable state (Demos, Instructions, Config).
5. **`LmpModule` + `ForwardAsync()`** — plain C# composition, no graphs, no IR.
6. **Optimizers return same module type** with parameters filled — no new types.
7. **`CompileAsync` naming** — aligns with DSPy "compilation" terminology.
8. **`SaveAsync`/`LoadAsync`** — simple JSON on `LmpModule`, no archive format.
9. **Source gen targets `netstandard2.0`** — separate `LMP.SourceGen` project.
10. **Three diagnostics:** LMP001 (missing description), LMP002 (non-serializable), LMP003 (non-partial record).

---

## Spec Discrepancies to Resolve

1. **Generator project name:** `AGENTS.md` → `LMP.SourceGen`. `source-generator.md` → `LMP.Generators`. **Use `LMP.SourceGen`** — generator MUST be netstandard2.0, separate from net10.0 `LMP.Core`.

2. **`IOptimizer` method name:** `public-api.md` → `CompileAsync`. `phased-plan.md` Phase 1 → `OptimizeAsync`. **Use `CompileAsync`** — aligns with DSPy "compile" terminology and `compiler-optimizer.md`.

3. **`Predictor.Demos` type:** `public-api.md` → `IReadOnlyList<(TInput, TOutput)>`. `runtime-execution.md` → `List<Example<TInput, TOutput>>`. `IPredictor.Demos` → `IList`. **Use `List<(TInput, TOutput)>`** on generic class, expose as `IList` via `IPredictor`.

4. **`Predictor.Config` type:** `public-api.md` → `PredictorConfig` record. `runtime-execution.md` → `ChatOptions` directly. **Use `ChatOptions`** — don't reinvent what M.E.AI provides.

5. **`docs/03-implementation/`** references v1 concepts (ProgramGraph, StepDescriptor, binding tiers). **Ignore** — follow `AGENTS.md` + `docs/02-specs/`.

6. **`ForwardAsync` signature:** `public-api.md` → `Task<object> ForwardAsync(object input, CancellationToken)`. `runtime-execution.md` → typed overrides. **Use untyped abstract base**, users write typed wrappers.

---

*Generated from comprehensive spec analysis. No code implemented yet.*
