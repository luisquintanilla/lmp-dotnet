# LMP Implementation Plan

> **Status:** Phases J + K complete — 1,611 tests passing. L, M pending.
> **Target:** .NET 10 / C# 14
> **Authoritative specs:** `docs/01-architecture/`, `docs/02-specs/`, `AGENTS.md`
> **Last updated:** 2026-04-10

---

## Gap Analysis

**Existing code:** Solution skeleton is complete. All 5 `src/` projects and 5 `test/` projects exist with placeholder files. `dotnet build` succeeds with 0 warnings, 0 errors. No types are implemented yet.

**Skeleton inventory (all present):**
- `global.json` — SDK 10.0.201
- `Directory.Build.props` — net10.0, LangVersion=preview, Nullable=enable, TreatWarningsAsErrors
- `Directory.Packages.props` — M.E.AI 10.4.1, CodeAnalysis 5.3.0, xUnit 2.9.3, Moq 4.20.72
- `.editorconfig` — C# 14 conventions, file-scoped namespaces, `_camelCase` private fields
- `LMP.slnx` — all 10 projects in `/src/` and `/test/` folders
- `LMP.SourceGen.csproj` — netstandard2.0, `IsRoslynComponent`, `EnforceExtendedAnalyzerRules`, LangVersion=12.0
- `LMP.Core.csproj` — refs Abstractions + SourceGen (as analyzer) + M.E.AI
- `LMP.Modules.csproj` / `LMP.Optimizers.csproj` — ref Core
- `LmpSourceGenerator.cs` — stub with `[Generator]` + empty `Initialize()`

**What needs to be built (from specs):**

| Component | Spec Reference | Status |
|---|---|---|
| Solution skeleton (sln, props, global.json) | `AGENTS.md` | :white_check_mark: Complete |
| `LMP.Abstractions` — attributes, interfaces, base types | `public-api.md` §4 | :white_check_mark: Complete (Phase 1) |
| `LMP.Core` — Predictor, LmpModule, assertions | `runtime-execution.md` §2–3, §6 | :white_check_mark: Phase 2.7 complete (PredictAsync wired, retry-on-assertion, GetState/LoadState with demos) |
| `LMP.SourceGen` — IIncrementalGenerator | `source-generator.md` | :white_check_mark: Phase 2.8 complete (model extraction, PromptBuilder, JsonContext, GetPredictors, module JsonContext, LMP001/LMP002/LMP003 diagnostics) |
| `LMP.Modules` — CoT, BestOfN, Refine, ReAct | `runtime-execution.md` §4–5 | :white_check_mark: Phase 5.1 complete (ChainOfThought, BestOfN, Refine, ReActAgent) |
| `LMP.Optimizers` — Evaluator, Bootstrap*, MIPROv2 | `compiler-optimizer.md` | :white_check_mark: Phase 6.1 complete (Evaluator, Clone, BootstrapFewShot, BootstrapRandomSearch, MIPROv2+TPE) |
| Diagnostics LMP001–LMP003 | `diagnostics.md` | :white_check_mark: Complete |
| Artifact save/load (JSON) | `artifact-format.md` | :white_check_mark: Complete (Phase 4.5) |
| `LMP.Cli` — CLI tool (`dotnet lmp`) | `cli.md` | :white_check_mark: Phase 7.1 complete (inspect, optimize, eval, run commands) |
| `LMP.Aspire.Hosting` — Aspire integration | `phased-plan.md` Phase 7 | :white_check_mark: Phase 7.2 complete (AddLmpOptimizer, LmpTelemetry) |
| Test projects | `AGENTS.md` | :white_check_mark: 756 tests passing (Phases 1–7.2) |
| End-to-end sample (`LMP.Samples.TicketTriage`) | `phased-plan.md` Phase 7, `mvp-demo-script.md` | :white_check_mark: Phase 7.3 complete (18 train + 10 dev examples, mock client demo) |

**Skeleton issues to address during Phase 1:**
- `LMP.Modules.csproj` and `LMP.Optimizers.csproj` lack `<RootNamespace>`. Add `<RootNamespace>LMP.Modules</RootNamespace>` and `<RootNamespace>LMP.Optimizers</RootNamespace>` (or `LMP` if types should be in root namespace — spec shows `namespace LMP` for most types).
- `LMP.Abstractions` needs PackageReference to `Microsoft.Extensions.AI.Abstractions` for `ChatOptions` (used by `IPredictor.Config`).
- Consumer test projects that define `[LmpSignature]` types or `LmpModule` subclasses will need SourceGen analyzer reference (add in Phase 2).

> **Stale v1 docs:** `docs/03-implementation/repo-layout.md` and `testing-strategy.md` reference v1 concepts (ProgramGraph, StepDescriptor, binding tiers) that were **explicitly dropped** in v2. Use `AGENTS.md` and `docs/02-specs/` as the authoritative source.

---

## Resolved Spec Discrepancies

These disagreements exist across spec documents. Each is resolved below with rationale. Implementations **must** follow the resolution, not the individual spec that disagrees.

### D1. Project naming for source generator

| Spec | Name |
|---|---|
| `AGENTS.md` | `LMP.SourceGen` |
| `system-architecture.md` | Implied inside `LMP.Core` |
| `source-generator.md` | `LMP.Generators` |

**Resolution: `LMP.SourceGen`** per `AGENTS.md`. The generator MUST target `netstandard2.0` while `LMP.Core` targets `net10.0`, so they must be separate projects. `LMP.SourceGen` is referenced as an analyzer by consumer projects.

### D2. `IOptimizer.CompileAsync` signature

| Spec | Signature |
|---|---|
| `public-api.md` §4.8 | `CompileAsync<TModule, TInput, TLabel>(TModule, IReadOnlyList<Example<TInput, TLabel>>, Func<TLabel, object, float>, CancellationToken)` |
| `compiler-optimizer.md` §2 | `CompileAsync<TModule>(TModule, IReadOnlyList<Example>, Func<Example, object, float>, CancellationToken)` |

**Resolution: Use the `compiler-optimizer.md` form** — `CompileAsync<TModule>` with non-generic `Example` and `Func<Example, object, float>`. Rationale: optimizers iterate `GetPredictors()` which returns `IPredictor` (non-generic). The optimizer must work with any module regardless of specific TInput/TOutput types. `ForwardAsync` returns `object`. The typed `Example<TInput, TLabel>` is the user-facing type for constructing training sets, but it must be implicitly convertible to or extend the non-generic `Example` so optimizers can consume it.

### D3. `GetPredictors()` return type

| Spec | Return type |
|---|---|
| `public-api.md` §4.3 | `IReadOnlyList<object>` |
| `runtime-execution.md` §3.2, appendix | `IReadOnlyList<IPredictor>` |
| `artifact-format.md` §4, `source-generator.md` §5 | `IReadOnlyList<(string Name, IPredictor Predictor)>` |
| `compiler-optimizer.md` §2 | `IReadOnlyList<PredictorMetadata>` |

**Resolution: `IReadOnlyList<(string Name, IPredictor Predictor)>`** per `source-generator.md` §5 and `artifact-format.md` §4. Optimizers need both the name (for demo mapping in save/load) and the `IPredictor` instance. Named tuples are simple enough — no need for a `PredictorMetadata` record.

### D4. `Predictor.Demos` type

| Spec | Type |
|---|---|
| `public-api.md` §4.2 | `IReadOnlyList<(TInput Input, TOutput Output)>` |
| `runtime-execution.md` §2.1 | `List<Example<TInput, TOutput>>` |
| `IPredictor` (runtime §3.2) | `IList` (non-generic) |

**Resolution:** Use `List<Example<TInput, TOutput>>` on the generic `Predictor<TIn,TOut>`. Expose as `IList` via `IPredictor` for optimizers that work with erased types. The optimizer fills demos via the non-generic `IList` using boxed objects.

### D5. `Predictor.Config` type

| Spec | Type |
|---|---|
| `public-api.md` §4.2 | `PredictorConfig` (custom) |
| `runtime-execution.md` §2.1 | `ChatOptions` (M.E.AI) |

**Resolution: `ChatOptions`** from `Microsoft.Extensions.AI`. Don't reinvent — M.E.AI's `ChatOptions` already has temperature, max tokens, stop sequences, tools, etc.

### D6. `ForwardAsync` base signature

| Spec | Signature |
|---|---|
| `public-api.md` §4.3 | `abstract Task<object> ForwardAsync(object input, CancellationToken)` |
| `runtime-execution.md` §3.1 | Typed override: `Task<DraftReply> ForwardAsync(TicketInput input)` |

**Resolution:** Both are needed. Declare `abstract Task<object> ForwardAsync(object input, CancellationToken ct = default)` as the base (required by Evaluator/optimizers). Users also write typed convenience methods that call the base. Source-gen may bridge them.

### D7. `IPredictor` interface — save/load members

| Spec | Members |
|---|---|
| `runtime-execution.md` §3.2 | `Name`, `Instructions`, `Demos`, `Config` |
| `artifact-format.md` §5 | `GetState() -> PredictorState`, `LoadState(PredictorState)` |

**Resolution:** Combine both. `IPredictor` has all runtime members (`Name`, `Instructions`, `Demos`, `Config`) **plus** `GetState()` and `LoadState()` for artifact round-trip. The source generator emits typed `GetState()`/`LoadState()` implementations that serialize `Demos` through the type-safe `JsonSerializerContext`.

### D8. `Example` — typed vs non-generic

| Spec | Form |
|---|---|
| `public-api.md` §4.4 | `Example<TInput, TLabel>(TInput Input, TLabel Label)` |
| `compiler-optimizer.md` §2–5 | Non-generic `Example` with indexer `example["field"]` and `WithInputs() -> object` |

**Resolution:** Both are needed. Define a non-generic abstract base class `Example` with `WithInputs() -> object` and string indexer. `Example<TInput, TLabel>` inherits from it with typed access. Optimizers work with `Example` (non-generic); users construct `Example<TInput, TLabel>` (typed). The non-generic `Example` stores data as `JsonElement` dictionaries internally.

### D9. Module discovery for source generator

| Spec | Mechanism |
|---|---|
| `source-generator.md` §2 line 84 | `ForAttributeWithMetadataName("LMP.LmpModuleAttribute")` |
| All usage examples | `class Foo : LmpModule` — no attribute |

**Resolution:** Use **base-type check** via `CreateSyntaxProvider`. No `[LmpModule]` attribute required — scanning for `: LmpModule` base class is sufficient and avoids user boilerplate. The source-gen spec's mention of `LmpModuleAttribute` is an implementation alternative; the base-type check is preferred.

### D10. `Trace` class vs `Trace` record

| Spec | Definition |
|---|---|
| `public-api.md` §4.5 | `record Trace(object Predictor, object Input, object Output)` — single entry |
| `runtime-execution.md` §2.4 | `sealed class Trace` with `List<TraceEntry>` — multi-entry container |

**Resolution:** Use the `runtime-execution.md` form — `sealed class Trace` with `Record<TIn,TOut>()` method and `IReadOnlyList<TraceEntry> Entries`. This is what the optimizer actually needs: a container that captures all predictor calls during one `ForwardAsync` execution.

---

## Phase 1: Abstractions

**Goal:** Define the foundational types. No runtime behavior, no code generation — just type contracts.

**Spec references:** `public-api.md` §4, `phased-plan.md` Phase 1, `runtime-execution.md` appendix, `artifact-format.md` §3

### 1.1 — Solution Skeleton ✅ COMPLETE

Create the repository structure, solution file, and build infrastructure.

**Tasks:**
- [x] Create `global.json` pinning .NET 10 SDK
- [x] Create `Directory.Build.props` with shared properties: `net10.0`, `LangVersion=preview`, nullable enabled, `TreatWarningsAsErrors`, XML doc generation
- [x] Create `Directory.Packages.props` for central package management (`Microsoft.Extensions.AI`, `Microsoft.CodeAnalysis.CSharp`, xUnit, Moq/NSubstitute)
- [x] Create `.editorconfig` with C# 14 conventions
- [x] Create `LMP.slnx` solution file (`.slnx` is .NET 10 default format)
- [x] Create 5 `src/` project stubs per `AGENTS.md`:
  - `LMP.Abstractions` (net10.0, no deps)
  - `LMP.Core` (net10.0, refs Abstractions + M.E.AI)
  - `LMP.SourceGen` (netstandard2.0, refs CodeAnalysis)
  - `LMP.Modules` (net10.0, refs Core)
  - `LMP.Optimizers` (net10.0, refs Core)
- [x] Create 5 `test/` project stubs per `AGENTS.md`:
  - `LMP.Abstractions.Tests`, `LMP.Core.Tests`, `LMP.SourceGen.Tests`, `LMP.Modules.Tests`, `LMP.Optimizers.Tests`
- [x] Wire `LMP.SourceGen` as `<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false">` in `LMP.Core`
- [x] Verify `dotnet build` passes with zero errors on the solution
- [x] Verify `dotnet test` passes (no tests yet, but framework loads)
- [x] Add `<RootNamespace>` to `LMP.Modules.csproj` and `LMP.Optimizers.csproj` (decide: `LMP` vs `LMP.Modules` / `LMP.Optimizers`)
- [x] Add `Microsoft.Extensions.AI.Abstractions` PackageReference to `LMP.Abstractions.csproj` (needed for `ChatOptions` in `IPredictor`)

**Status:** ✅ Skeleton complete. Phase 1 types implemented.

### 1.2 — LmpSignatureAttribute ✅ COMPLETE

The source generator's entry point. Placed on `partial record` output types only.

**Spec:** `public-api.md` §4.1

**Tasks:**
- [x] Create `LmpSignatureAttribute` in `LMP.Abstractions` namespace `LMP`
  - `[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]`
  - Primary constructor: `LmpSignatureAttribute(string instructions)`
  - `public string Instructions { get; }` read-only property
- [x] XML doc comments on class and `Instructions` property
- [x] Unit test: `new LmpSignatureAttribute("Classify tickets").Instructions == "Classify tickets"`

**Completion criteria:** A consumer can write `[LmpSignature("Classify tickets")]` on a partial record and compile.

### 1.3 — IPredictor Interface and Artifact Records ✅ COMPLETE

The non-generic interface exposed to optimizers for predictor discovery and artifact round-trip.

**Spec:** `runtime-execution.md` §3.2, `artifact-format.md` §3, §5

**Tasks:**
- [x] Create `IPredictor` interface in `LMP.Abstractions` (per **D3**, **D7**):
  - `string Name { get; set; }`
  - `string Instructions { get; set; }`
  - `IList Demos { get; }` — non-generic `System.Collections.IList`
  - `ChatOptions Config { get; set; }` — M.E.AI's ChatOptions per **D5**
  - `PredictorState GetState()`
  - `void LoadState(PredictorState state)`
- [x] Create `ModuleState` sealed record in `LMP.Abstractions` per `artifact-format.md` §3:
  - `string Version`, `string Module`, `Dictionary<string, PredictorState> Predictors`
- [x] Create `PredictorState` sealed record:
  - `string Instructions`, `List<DemoEntry> Demos`, `Dictionary<string, JsonElement>? Config`
- [x] Create `DemoEntry` sealed record:
  - `Dictionary<string, JsonElement> Input`, `Dictionary<string, JsonElement> Output`
- [x] Create `ModuleStateSerializerContext` per `artifact-format.md` §3:
  - `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase, WriteIndented = true, DefaultIgnoreCondition = WhenWritingNull)]`
  - `[JsonSerializable(typeof(ModuleState))]`
  - `public partial class ModuleStateSerializerContext : JsonSerializerContext`
- [x] Unit tests: `PredictorState` record equality, `ModuleState` round-trips with `with` expressions

**Completion criteria:** `IPredictor` compiles with all members; `ModuleState` serializes/deserializes correctly.

### 1.4 — Example Base Class and Example\<TInput, TLabel\> ✅ COMPLETE

Training data types used by optimizers and evaluators.

**Spec:** `public-api.md` §4.4, `compiler-optimizer.md` §2 (per **D8**)

**Tasks:**
- [x] Create non-generic `Example` abstract class in `LMP.Abstractions`:
  - `abstract object WithInputs()` — returns input portion for `ForwardAsync`
  - `abstract object GetLabel()` — returns label portion for metrics
  - String indexer `this[string fieldName]` — accesses label fields by name (via `JsonElement`)
- [x] Create `Example<TInput, TLabel>` sealed record inheriting `Example`:
  - `TInput Input`, `TLabel Label` positional properties
  - `override object WithInputs() => Input!`
  - `override object GetLabel() => Label!`
  - Typed `TInput WithTypedInputs() => Input` convenience method
- [x] XML doc comments on all public members
- [x] Unit tests: construction, `WithInputs()`, record equality, typed access

**Completion criteria:** `new Example<TicketInput, ClassifyTicket>(input, label)` works; `WithInputs()` returns input; `GetLabel()` returns label.

### 1.5 — Trace and TraceEntry ✅ COMPLETE

Execution recording for optimizer trace collection.

**Spec:** `runtime-execution.md` §2.4

**Tasks:**
- [x] Create `Trace` sealed class in `LMP.Abstractions`:
  - `private readonly List<TraceEntry> _entries`
  - `IReadOnlyList<TraceEntry> Entries { get; }`
  - `void Record<TInput, TOutput>(string predictorName, TInput input, TOutput output)` — appends entry
  - `void Clear()` — resets for reuse
- [x] Create `TraceEntry` sealed record: `(string PredictorName, object Input, object Output)`
- [x] Unit tests: `Record()` appends entry, `Entries` returns recorded data, `Clear()` resets

**Completion criteria:** `trace.Record("classify", input, output)` appends; `trace.Entries[0].PredictorName == "classify"`.

### 1.6 — Demo Record ✅ COMPLETE

Non-generic demo type used by optimizers when filling predictor Demos.

**Spec:** `compiler-optimizer.md` §4 (Demo Type)

**Tasks:**
- [x] Create `Demo` sealed record in `LMP.Abstractions`: `(object Input, object Output)`
- [x] Unit test: construction and equality

**Completion criteria:** `new Demo(inputObj, outputObj)` works and compares by value.

### 1.7 — Predictor\<TInput, TOutput\> Class Shell ✅ COMPLETE

The core primitive. Shell only in Phase 1 — `PredictAsync` wired in Phase 2.

**Spec:** `public-api.md` §4.2, `runtime-execution.md` §2.1

**Tasks:**
- [x] Create `Predictor<TInput, TOutput>` in `LMP.Core` namespace `LMP`:
  - Constraint: `where TOutput : class`
  - Constructor: `Predictor(IChatClient client)`
  - `public string Name { get; set; }` — set by source-gen `GetPredictors()`
  - `public string Instructions { get; set; }` — defaults from `[LmpSignature]` (wired in Phase 2)
  - `public List<Example<TInput, TOutput>> Demos { get; set; } = []` (per **D4**)
  - `public ChatOptions Config { get; set; } = new()` (per **D5**)
  - Implements `IPredictor` explicitly:
    - `IList IPredictor.Demos => Demos` (the `List<>` implements `IList`)
    - `PredictorState GetState()` — stub (full impl in Phase 2/4)
    - `void LoadState(PredictorState state)` — stub
  - `PredictAsync(TInput, CancellationToken)` -> throws `NotImplementedException` until Phase 2
- [x] Unit test: construction, property defaults, `IPredictor` interface satisfaction, `IPredictor.Demos` returns the same list

**Completion criteria:** `new Predictor<TicketInput, ClassifyTicket>(client)` compiles; casting to `IPredictor` exposes `Name`, `Instructions`, `Demos`, `Config`.

### 1.8 — LmpModule Base Class ✅ COMPLETE

Abstract base class for composable LM programs with save/load.

**Spec:** `public-api.md` §4.3, `runtime-execution.md` §3, `artifact-format.md` §4

**Tasks:**
- [x] Create `LmpModule` abstract class in `LMP.Core` namespace `LMP`:
  - `public Trace? Trace { get; set; }`
  - `public abstract Task<object> ForwardAsync(object input, CancellationToken ct = default)` (per **D6**)
  - `public abstract IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()` (per **D3**)
  - `SaveAsync(string path, CancellationToken ct = default)` — concrete implementation per `artifact-format.md` §4:
    - Builds `ModuleState` from `GetPredictors()`, calling `GetState()` on each
    - Serializes via `ModuleStateSerializerContext.Default.ModuleState`
    - Atomic write: temp file -> `File.Move(overwrite: true)`
  - `LoadAsync(string path, CancellationToken ct = default)` — concrete implementation:
    - Deserializes `ModuleState` via `ModuleStateSerializerContext`
    - Iterates `GetPredictors()`, calls `LoadState()` on each matching predictor
  - `EnableTracing()` — sets `Trace = new Trace()` (for optimizer use)
  - `CollectTraces() -> IReadOnlyList<TraceEntry>` — returns `Trace.Entries` and clears
- [x] Unit test with a manual `LmpModule` subclass:
  - Override `GetPredictors()` manually (source-gen tested in Phase 2)
  - SaveAsync writes valid JSON matching `artifact-format.md` schema
  - LoadAsync reads it back, predictors have restored state

**Completion criteria:** `module.SaveAsync("test.json")` writes JSON; `module.LoadAsync("test.json")` restores predictor state.

### 1.9 — LmpAssert / LmpSuggest / Exceptions ✅ COMPLETE

Runtime assertions for LM output validation.

**Spec:** `runtime-execution.md` §6, `public-api.md` §4.6

**Tasks:**
- [x] Create `LmpAssertionException : Exception` in `LMP.Abstractions`:
  - `public object? FailedResult { get; }` property
  - `public string AssertionMessage { get; }` — the user-provided message
  - Constructor: `(string message, object? failedResult)`
- [x] Create `LmpMaxRetriesExceededException : Exception`:
  - `public int Attempts { get; }`
  - `public LmpAssertionException? LastAssertion { get; }`
- [x] Create `LmpAssert` static class in `LMP.Core`:
  - `static void That<T>(T result, Func<T, bool> predicate, string? message = null)`
  - Throws `LmpAssertionException` when predicate returns false
- [x] Create `LmpSuggest` static class in `LMP.Core`:
  - `static void That<T>(T result, Func<T, bool> predicate, string? message = null)`
  - Logs warning via `ILogger` (if available), never throws
- [x] Unit tests:
  - `LmpAssert.That(5, x => x > 0)` — passes silently
  - `LmpAssert.That(-1, x => x > 0, "Must be positive")` — throws `LmpAssertionException` with `FailedResult = -1`
  - `LmpSuggest.That(-1, x => x > 0)` — no exception

**Completion criteria:** Assert throws on failure with correct exception type; Suggest never throws.

### 1.10 — IRetriever and IOptimizer Interfaces ✅ COMPLETE

Contracts for RAG and optimization.

**Spec:** `public-api.md` §4.7–4.8, `compiler-optimizer.md` §2

**Tasks:**
- [x] Create `IRetriever` interface in `LMP.Abstractions` per `public-api.md` §4.7:
  - `Task<string[]> RetrieveAsync(string query, int k, CancellationToken ct = default)`
- [x] Create `IOptimizer` interface in `LMP.Abstractions` (per **D2**):
  - `Task<TModule> CompileAsync<TModule>(TModule module, IReadOnlyList<Example> trainSet, Func<Example, object, float> metric, CancellationToken ct = default) where TModule : LmpModule`
- [x] XML doc comments on both interfaces
- [x] Unit tests: mock implementations satisfy both interface contracts

**Completion criteria:** Interfaces compile; mock implementations can be instantiated and called.

### Phase 1 Exit Criteria

- ✅ `LMP.Abstractions` compiles with zero warnings — contains: `LmpSignatureAttribute`, `IPredictor`, `ModuleState`, `PredictorState`, `DemoEntry`, `Example<TIn,TLabel>`, `Trace`, `TraceEntry`, `LmpAssert`, `LmpSuggest`, `LmpAssertionException`, `LmpMaxRetriesExceededException`, `IRetriever`, `IOptimizer`, `LmpModule`
- ✅ `LMP.Core` compiles with zero warnings — contains: `Predictor<TIn,TOut>` (shell)
- ✅ All other projects compile (empty stubs — already verified)
- ✅ All public types have XML doc comments
- ✅ 67 unit tests pass for: attribute construction, record equality, `Example.WithInputs()`, assertion behavior, `Trace.Record()`, serialization round-trip, `IPredictor` interface satisfaction
- ✅ `dotnet build && dotnet test` succeeds with zero errors and zero warnings
- ✅ 67→231 unit tests pass across Phases 1–2.6

> **Status: ✅ PHASE 1 COMPLETE.** All abstractions and type contracts implemented. Next: Phase 2 (Source Generator + Core Predictor).

---

## Phase 2: Source Generator + Core Predictor

**Goal:** Wire the source generator to `[LmpSignature]` types and make `PredictAsync` work end-to-end.

**Spec references:** `source-generator.md`, `runtime-execution.md` §2, `diagnostics.md`

**Entry criteria:** Phase 1 complete; `Microsoft.Extensions.AI` configured.

### 2.1 — Generator Project Setup ✅ COMPLETE

**Spec:** `source-generator.md` §2

**Tasks:**
- [x] Configure `LMP.SourceGen.csproj` targeting `netstandard2.0`:
  - `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`
  - `<IsRoslynComponent>true</IsRoslynComponent>`
  - PackageReference: `Microsoft.CodeAnalysis.CSharp` >= 4.12
- [x] Create `LmpGenerator : IIncrementalGenerator` skeleton with empty `Initialize()` method
  - `[Generator]` attribute (note: spec uses `[Generator(LanguageNames.CSharp)]` but `[Generator]` also works)
- [x] Create `EquatableArray<T>` helper struct for incremental model cache correctness
- [x] Wire `LMP.SourceGen` as analyzer reference in consumer test projects (`LMP.Core.Tests`, integration test projects)
- [x] Add `InternalsVisibleTo("LMP.SourceGen.Tests")` for testing internal types
- [x] Add `Microsoft.CodeAnalysis.CSharp` + `LMP.Abstractions` references to `LMP.SourceGen.Tests` for Roslyn test infrastructure
- [x] Verify `dotnet build` runs the generator (empty — no output yet)
- [x] Unit tests: `EquatableArray<T>` equality, hash code, enumeration (16 tests)
- [x] Smoke tests: generator runs on empty compilation and `[LmpSignature]` types without errors (3 tests)

**Status:** ✅ Complete. `EquatableArray<T>` implemented with element-wise equality. Test projects wired with Roslyn infrastructure. 86 total tests pass.

**Completion criteria:** `dotnet build` triggers the generator; no errors.

### 2.2 — Output Type Model Extraction ✅ COMPLETE

Read `[LmpSignature]` types at build time.

**Spec:** `source-generator.md` §2 (pipeline), `diagnostics.md` (LMP003)

**Tasks:**
- [x] Implement `ForAttributeWithMetadataName("LMP.LmpSignatureAttribute")` pipeline:
  - Predicate: `node is TypeDeclarationSyntax` (broad — accepts classes too for LMP003)
  - Transform: `ModelExtractor.Extract(ctx, ct)`
- [x] Create `OutputTypeModel` record: Namespace, TypeName, Instructions, InputFields, OutputFields (EquatableArray), IsPartialRecord, TypeKindDescription, Location
- [x] Create `OutputFieldModel` record: Name, ClrTypeName, FullyQualifiedTypeName, Description (nullable), IsRequired, Location
- [x] Create `InputFieldModel` record: Name, ClrTypeName, Description (nullable)
- [x] Create `LocationInfo` struct: equatable wrapper for Roslyn Location (FilePath, TextSpan, LineSpan)
- [x] Create `LmpDiagnostics` static class with `LMP001`, `LMP002`, `LMP003` descriptors + release tracking files
- [x] Extract `[Description]` from output type properties per `source-generator.md` §3
- [x] Extract input type descriptions with priority order: XML doc `<param>` -> `[Description]` on ctor params -> `[Description]` on properties (via `ModelExtractor.ExtractInputFields` helper — called when Predictor<TIn,TOut> is resolved)
- [x] Report **LMP003** diagnostic for non-partial-record types per `diagnostics.md` — skip generation entirely
- [x] Unit tests (Roslyn `CSharpGeneratorDriver`): LMP003 fires on `class` and non-partial `record` (9 tests in `ModelExtractionTests`)
- [x] Unit tests: `InputFieldExtractionTests` — ctor params, property descriptions, priority, filtering (7 tests)
- [x] Unit tests: `OutputTypeModelExtractionTests` — LMP003 on class/non-partial record, valid partial record, location, multiple types, field extraction, description extraction (12 tests)

**Status:** ✅ Complete. 114 total tests pass (51 Abstractions + 16 Core + 47 SourceGen).

**Completion criteria:** Generator extracts correct metadata from `[LmpSignature]` records; LMP003 fires and skips non-partial types.

### 2.3 — PromptBuilder Emission ✅ COMPLETE

Generate the prompt assembly code.

**Spec:** `source-generator.md` §3

**Tasks:**
- [x] Create `PromptBuilderEmitter` that generates `file static class {TypeName}PromptBuilder`:
  - `private const string Instructions = "..."` — from `[LmpSignature]`
  - `private const string FieldDescriptions = "..."` — markdown of input/output fields from `[Description]` / XML docs
  - `public static IList<ChatMessage> BuildMessages(TInput input, IReadOnlyList<(TInput, TOutput)>? demos = null)`:
    - System message: Instructions + FieldDescriptions
    - Demo pairs: User=FormatInput(demoInput), Assistant=FormatOutput(demoOutput)
    - Current input: User=FormatInput(input)
  - `public static string DefaultInstructions` — returns Instructions constant
  - `private static string FormatInput(TInput input)` — formats each input field
  - `private static string FormatOutput(TOutput output)` — JSON-serializes (uses default serializer; Task 2.4 adds generated JsonContext)
- [x] Generated file attributes: `// <auto-generated />`, `#nullable enable`, `[GeneratedCode("LMP.Generators", "1.0.0")]`
- [x] Use `file` access modifier to prevent namespace pollution
- [x] Hint name: `{TypeName}.PromptBuilder.g.cs`
- [x] Created `PromptBuilderModel` — dedicated record for PromptBuilder emission with both input and output type metadata
- [x] Created `PredictorPairExtractor` — scans `Predictor<TIn,TOut>` generic type usages via `CreateSyntaxProvider`, resolves both input/output types, validates `[LmpSignature]` on output type, extracts combined model
- [x] Wired Pipeline 2 in `LmpSourceGenerator` — scans `GenericNameSyntax` nodes for `Predictor<,>`, deduplicates by output type, emits PromptBuilder for each valid pair
- [x] Unit tests: 26 direct emitter tests + 7 pipeline integration tests in `PromptBuilderEmitterTests` — structure, syntax validity, escaping, field descriptions, all methods, pipeline discovery, deduplication, hint name convention

**Status:** ✅ Complete. 160 total tests pass (51 Abstractions + 16 Core + 93 SourceGen). Full end-to-end pipeline: `Predictor<TIn,TOut>` usage → `PredictorPairExtractor` → `PromptBuilderEmitter` → emitted `{TypeName}.PromptBuilder.g.cs`. `FormatOutput` uses default `JsonSerializer.Serialize()`.

### 2.4 — JsonTypeInfo Emission ✅ COMPLETE

Generate STJ source-gen context for structured output.

**Spec:** `source-generator.md` §4

**Tasks:**
- [x] Create `JsonContextEmitter` that generates `file partial class {TypeName}JsonContext : JsonSerializerContext`:
  - `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase, DefaultIgnoreCondition = WhenWritingNull)]`
  - `[JsonSerializable(typeof(TOutput))]`
- [x] Hint name: `{TypeName}.JsonContext.g.cs`
- [x] Wired `JsonContextEmitter.Emit()` into `LmpSourceGenerator` pipeline for all valid partial record output types
- [x] Fixed pre-existing build error: `ModelExtractor.ExtractOutputFields` visibility changed from `private` to `internal` (needed by `PredictorPairExtractor`)
- [x] Updated `PromptBuilderEmitterTests` helpers to use `PromptBuilderModel` (aligned with prior refactor)
- [x] Snapshot tests: 16 tests in `JsonContextEmitterTests` — structure, syntax validity, attributes, namespaces, full snapshot verification
- [x] Updated existing integration tests to expect JsonContext output from valid partial records

**Status:** ✅ Complete. 128 total tests pass (51 Abstractions + 16 Core + 61 SourceGen). Generator emits `{TypeName}.JsonContext.g.cs` for every valid `[LmpSignature]` partial record. STJ source generator picks up the `[JsonSerializable]` declaration to generate actual `JsonTypeInfo<T>` metadata.

**Completion criteria:** `dotnet build` produces `ClassifyTicket.JsonContext.g.cs`; STJ source gen picks it up.

### 2.5 — GetPredictors() Emission ✅ COMPLETE

Generate predictor discovery on `LmpModule` subclasses.

**Spec:** `source-generator.md` §5

**Tasks:**
- [x] Implement module discovery pipeline using `CreateSyntaxProvider` (per **D9**):
  - Predicate: `ClassDeclarationSyntax` with base list and `partial` keyword
  - Transform: validate `LmpModule` base type, walk all fields and properties, identify `Predictor<,>` or subclasses
  - Use `PredictorPairExtractor.IsPredictorType()` helper walking base types to match `LMP.Predictor<TInput, TOutput>`
- [x] Create `ModuleModel` record: Namespace, TypeName, PredictorFields (EquatableArray)
- [x] Create `PredictorFieldModel`: FieldName, InputTypeFQN, OutputTypeFQN
- [x] Create `ModuleExtractor` with `IsCandidate` predicate and `Extract` transform
- [x] Create `ModuleEmitter` that generates `partial class {ModuleName}`:
  - `public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()` (per **D3**)
  - Returns list of `(fieldName, fieldReference)` for each Predictor field
  - Strips `_` prefix from field names for the Name string
- [x] Hint name: `{ModuleName}.Predictors.g.cs`
- [x] Wired Pipeline 3 in `LmpSourceGenerator` — scans partial class declarations for LmpModule subclasses
- [x] Unit tests: 27 tests in `ModuleEmitterTests` — direct emitter tests (structure, syntax validity, field stripping, snapshot) + pipeline integration tests (module with fields, single field, non-module, empty module, non-partial module, property predictors, mixed fields/properties, indirect subclass, multiple modules) + model equality tests
- [x] Un-skipped 5 PromptBuilder pipeline tests from Phase 2.3 (now all passing with the pipeline wired)

**Status:** ✅ Complete. 187 total tests pass (51 Abstractions + 16 Core + 120 SourceGen). Full end-to-end pipeline: `partial class : LmpModule` → `ModuleExtractor` → `ModuleEmitter` → emitted `{ModuleName}.Predictors.g.cs`. Walks both fields and properties. Predictor fields named with `_` prefix get the prefix stripped in the name string.

**Completion criteria:** A module with `_classify` and `_draft` predictor fields gets a generated `GetPredictors()` returning `[("classify", _classify), ("draft", _draft)]`.

### 2.6 — Diagnostics LMP001 and LMP002 ✅ COMPLETE

Build-time diagnostics for output type validation.

**Spec:** `diagnostics.md`

**Tasks:**
- [x] `LmpDiagnostics` static class already existed with `DiagnosticDescriptor` fields for LMP001, LMP002, LMP003
- [x] Implement **LMP001**: warn on output properties missing `[Description]` per `diagnostics.md`
  - Still generates code — use property name as fallback description
  - Message: `"Property '{0}' on output type '{1}' is missing a [Description] attribute"`
  - Reported in Pipeline 1 via `ReportFieldDiagnostics()` for valid partial records
- [x] Implement **LMP002**: error on non-serializable output property types per `diagnostics.md`
  - Skips code generation for the entire type (`HasNonSerializableProperty` flag on `OutputTypeModel`)
  - Blocklist: `Delegate` (all), `IntPtr`, `UIntPtr`, `Span<T>`, `ReadOnlySpan<T>`, `Action<>`, `Func<>`, `Expression<>`, `Task<>`, `Stream`, etc.
  - Message: `"Property '{0}' on output type '{1}' is not serializable by System.Text.Json"`
  - Created `SerializabilityChecker` static class for type analysis during model extraction
- [x] Added `IsNonSerializable` flag to `OutputFieldModel` for per-field diagnostic reporting
- [x] Added `HasNonSerializableProperty` flag to `OutputTypeModel` to suppress code generation
- [x] Updated `PredictorPairExtractor` to skip PromptBuilder emission for types with LMP002 errors
- [x] Unit tests: 18 diagnostic tests in `OutputTypeModelExtractionTests`:
  - LMP001: fires on missing description, doesn't fire when all present, multiple properties, still generates code, location points to property, doesn't fire on non-partial record
  - LMP002: fires on Action<>, Func<>, delegate, IntPtr; doesn't fire on serializable types; skips code gen; multiple errors reported; location, combined with LMP001, doesn't fire on non-partial record
- [x] Unit tests: 28 serialization checker tests in `SerializabilityCheckerTests`:
  - Primitives, nullable value types, arrays, List<>, Dictionary<>, DateTime all serializable
  - Action<>, Action, Func<>, IntPtr, UIntPtr, CancellationToken, custom delegates, Task<>, Task all non-serializable

**Status:** ✅ Complete. 256 total tests pass (51 Abstractions + 16 Core + 189 SourceGen). All three diagnostics (LMP001, LMP002, LMP003) implemented and tested. LMP001 is a warning that allows code gen. LMP002 is an error that suppresses all code gen for the type. LMP003 was already implemented in Phase 2.2. Additional 25 diagnostic integration tests added in `DiagnosticTests.cs` covering LMP001/LMP002 firing, location, code gen suppression, combined scenarios, and SerializabilityChecker coverage via Stream/Task/Expression/CancellationToken types.

**Completion criteria:** All 3 diagnostics fire correctly; LMP002 skips code generation.

### 2.7 — Predictor\<TInput, TOutput\>.PredictAsync Implementation ✅ COMPLETE

Wire the core prediction flow.

**Spec:** `runtime-execution.md` §2.2

**Tasks:**
- [x] Implement `PredictAsync(TInput input, CancellationToken ct)` in `Predictor<TIn,TOut>`:
  1. Build messages via `BuildMessages()` virtual method (default: Instructions + demos + input as toString; source-gen wired via `MessageBuilder` delegate)
  2. Call `_client.GetResponseAsync<TOutput>(messages, Config, ct)` → `ChatResponse<T>.Result`
  3. Record trace: `trace?.Record(Name, input, result)`
  4. Return typed `TOutput`
- [x] Trace passed as parameter to PredictAsync (matching existing signature design — module sets `Trace` and passes it)
- [x] Implement retry-on-assertion per `runtime-execution.md` §2.5:
  - `Action<TOutput>? validate` delegate parameter enables retry loop
  - On `LmpAssertionException`, captures error message and appends feedback: `"Previous attempt failed: {lastError}. Try again."`
  - Retry loop: up to `maxRetries` (default 3), catches `LmpAssertionException`, feeds message back
  - Throws `LmpMaxRetriesExceededException` when budget exhausted
- [x] Implement `GetState()` on Predictor — serializes `Instructions`, `Demos` (via JsonElement conversion)
- [x] Implement `LoadState(PredictorState)` on Predictor — restores Instructions and Demos with full deserialization
- [x] Create `FakeChatClient` test infrastructure that returns canned `TOutput` JSON
- [x] Integration test: `Predictor<TicketInput, ClassifyTicket>.PredictAsync()` -> `FakeChatClient` -> typed `ClassifyTicket`
- [x] Integration test: retry-on-assertion appends error feedback and re-calls
- [x] 20 new integration tests covering: basic predict, trace recording, demo messages, cancellation, retry mechanics, GetState/LoadState round-trip

**Implementation notes:**
- `MessageBuilder` internal property allows source-gen PromptBuilder to be wired in (future)
- `BuildMessages` is `protected virtual` for extensibility
- Default prompt uses `ToString()` for input formatting; source-gen PromptBuilder provides field-aware formatting
- `GetResponseAsync<TOutput>` returns `ChatResponse<TOutput>` with `.Result` property

**Completion criteria:** ✅ End-to-end prediction works against `FakeChatClient`; retry-on-assertion re-invokes with error context. 275 tests passing.

### 2.8 — Module-Level JsonSerializerContext Emission ✅ COMPLETE

Source generator emits per-module `JsonSerializerContext` for typed save/load of predictor demos.

**Spec:** `runtime-execution.md` §3.3, `source-generator.md` §5

**Tasks:**
- [x] Created `ModuleJsonContextEmitter` that generates an `internal partial class {ModuleName}JsonContext : JsonSerializerContext` per module:
  - `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase, WriteIndented = true, DefaultIgnoreCondition = WhenWritingNull)]`
  - `[JsonSerializable(typeof(ModuleState))]`
  - `[JsonSerializable(typeof(PredictorState))]`
  - `[JsonSerializable(typeof(DemoEntry))]`
  - `[JsonSerializable(typeof(TInput))]` / `[JsonSerializable(typeof(TOutput))]` per unique predictor type pair (sorted, deduplicated)
- [x] Wired into Pipeline 3 in `LmpSourceGenerator` — combined callback emits both GetPredictors and JsonContext
- [x] Hint name: `{ModuleName}.JsonContext.g.cs`
- [x] 30 tests in `ModuleJsonContextEmitterTests`:
  - Direct emitter tests: header, usings, namespace, GeneratedCode attribute, internal partial class, type name in class name
  - JsonSourceGenerationOptions tests: options present, ModuleState/PredictorState/DemoEntry [JsonSerializable] attributes
  - Concrete type tests: includes all unique TInput/TOutput types, deduplicates shared types, sorts for determinism, correct total attribute count
  - Syntax validity: valid C# with and without namespace
  - Snapshot tests: full output structure ordering, no-namespace variant
  - Pipeline integration tests: emits JsonContext file, contains all serializable types (core + concrete), uses internal partial class, both Predictors and JsonContext emitted together, multiple modules emit separate JsonContext files, non-partial modules skip JsonContext, single-predictor module emits JsonContext, total attribute count in pipeline

**Implementation notes:**
- Separate `ModuleJsonContextEmitter` class for clean separation from `ModuleEmitter` (GetPredictors)
- Uses `internal` access modifier so module's `SaveAsync`/`LoadAsync` can reference the context (not `file`)
- `WriteIndented = true` for human-readable artifact files (per `artifact-format.md`)
- Concrete TInput/TOutput types included for AOT-safe demo serialization
- Types sorted alphabetically by FQN for deterministic output

**Status:** ✅ Complete. 305 total tests pass (51 Abstractions + 35 Core + 219 SourceGen). Generator now emits `{ModuleName}.JsonContext.g.cs` with concrete types for every valid partial `LmpModule` subclass.

**Completion criteria:** ✅ Source-gen produces module-specific `JsonSerializerContext` for AOT-safe save/load.

### Phase 2 Exit Criteria

- `dotnet build` on a project with `[LmpSignature]` produces `*.g.cs` files containing PromptBuilder, JsonTypeInfo, and GetPredictors
- `Predictor<TicketInput, ClassifyTicket>.PredictAsync()` runs against `FakeChatClient` and returns typed `ClassifyTicket`
- Rebuilding produces identical generated source (determinism verified by snapshot tests)
- LMP001 fires on properties without `[Description]`; LMP002 fires on non-serializable types; LMP003 fires on non-partial records
- `SaveAsync`/`LoadAsync` round-trips with source-gen `JsonSerializerContext`
- All snapshot tests and integration tests pass deterministically

---

## Phase 3: Reasoning Modules

**Goal:** Build thin wrappers around `Predictor<TIn, TOut>` in `LMP.Modules`. Each module <100 LOC.

**Spec references:** `runtime-execution.md` §4, `phased-plan.md` Phase 3

**Entry criteria:** Phase 2 complete; `PredictAsync` works; source generator emits PromptBuilder.

### 3.1 — ChainOfThought\<TInput, TOutput\> ✅ COMPLETE

**Spec:** `runtime-execution.md` §4.1, `source-generator.md` §6

**Tasks:**
- [x] Extend source generator with CoT discovery pipeline per `source-generator.md` §2 line 93–99:
  - `CreateSyntaxProvider` scanning for `ChainOfThought<TIn, TOut>` generic usages
  - Extract `TOut` type symbol from generic argument
  - Emit `{TypeName}WithReasoning` internal record:
    - `[Description("Think step by step...")] [JsonPropertyOrder(-1)] public required string Reasoning { get; init; }`
    - All original `TOutput` fields copied with their `[Description]` attributes
  - Emit corresponding `{TypeName}WithReasoningJsonContext : JsonSerializerContext`
  - Hint name: `{TypeName}.ChainOfThought.g.cs`
- [x] Implement `ChainOfThought<TIn, TOut> : Predictor<TIn, TOut>` in `LMP.Modules`:
  - Overrides `PredictAsync` to call `GetResponseAsync<ChainOfThoughtResult<TOutput>>`
  - `ChainOfThoughtResult<TOutput>` generic wrapper has `Reasoning` + `Result` fields
  - Inherits from `Predictor<TIn, TOut>` for IPredictor/GetPredictors compatibility
  - Reasoning captured in trace via extended result recording
  - Updated Pipeline 2 predicate to also match `ChainOfThought<,>` for PromptBuilder emission
- [x] Snapshot test: `ClassifyTicket` usage -> emits `ClassifyTicketWithReasoning` record + JsonContext
- [x] Unit test against `FakeChatClient`: output has Category+Urgency, reasoning in trace

**Status:** ✅ Complete. 351 total tests pass (51 Abstractions + 35 Core + 19 Modules + 246 SourceGen). ChainOfThought uses generic `ChainOfThoughtResult<TOutput>` wrapper at runtime (nested JSON); source generator emits optimized flat `{TypeName}WithReasoning` record for compile-time usage.

**Completion criteria:** ✅ `ChainOfThought<TicketInput, ClassifyTicket>` produces typed output; reasoning visible in trace entries.

### 3.2 — BestOfN\<TInput, TOutput\> ✅ COMPLETE

**Spec:** `runtime-execution.md` §4.2

**Tasks:**
- [x] Implement `BestOfN<TIn, TOut> : Predictor<TIn, TOut>` in `LMP.Modules`:
  - Constructor: `(IChatClient client, int n, Func<TIn, TOut, float> reward)`
  - `PredictAsync`: fire N concurrent predictions via `Task.WhenAll`, score each with reward, return highest
  - Delegates learnable state (`Instructions`, `Demos`, `Config`) to all N inner predictors
- [x] Unit test: N=3, mock returns 3 different outputs with different scores -> best returned
- [x] Verify true parallelism: all N `GetResponseAsync` calls made concurrently (not sequentially)
- [x] Made `FakeChatClient` thread-safe for concurrent BestOfN access
- [x] Created `DelayedFakeChatClient` for timing-based concurrency verification
- [x] 25 tests in `BestOfNTests`: constructor validation, inheritance, IPredictor interface, learnable state, N=1/3/5 prediction, reward selection, custom reward, trace recording, cancellation, validation, demos/instructions in prompt, concurrency timing

**Status:** ✅ Complete. 376 total tests pass (51 Abstractions + 35 Core + 44 Modules + 246 SourceGen). BestOfN fires N parallel predictions via `Task.WhenAll`, scores each with the reward function, and returns the highest-scoring candidate. All candidates recorded in trace.

**Completion criteria:** ✅ `BestOfN` with N=5 makes 5 parallel calls, scores each, returns the best.

### 3.3 — Refine\<TInput, TOutput\> ✅ COMPLETE

**Spec:** `runtime-execution.md` §4.3

**Tasks:**
- [x] Create `RefineCritiqueInput<TOutput>` record: `(object OriginalInput, TOutput PreviousOutput)`
- [x] Implement `Refine<TIn, TOut> : Predictor<TIn, TOut>` in `LMP.Modules`:
  - Constructor: `(IChatClient client, int maxIterations = 2)`
  - `PredictAsync`: initial predict -> for each iteration: critique previous output -> re-predict with critique context
  - Each iteration is a separate `PredictAsync` call recorded in trace
- [x] Unit test: 27 tests covering constructor validation, inheritance, trace recording, chained outputs, cancellation, validation, demos

**Completion criteria:** `Refine` executes predict -> refine loop; trace shows multiple predictor calls.

### Phase 3 Exit Criteria

- `ChainOfThought` produces output with reasoning captured in trace
- `BestOfN` invokes N parallel calls and selects the best result by reward function
- `Refine` executes at least two rounds of predict -> critique -> re-predict
- All module tests pass against `FakeChatClient`

---

## Phase 4: Evaluation + BootstrapFewShot

**Goal:** Add `Evaluator` and foundational optimizers. First phase where LMP can programmatically improve an LM program.

**Spec references:** `compiler-optimizer.md`, `artifact-format.md`

**Entry criteria:** Phase 2 complete; Phase 3 recommended but not required.

### 4.1 — Evaluator ✅ COMPLETE

**Spec:** `compiler-optimizer.md` §3

**Tasks:**
- [x] **Prerequisite:** Added non-generic `Example` abstract base record per D8 — `WithInputs() -> object`, `GetLabel() -> object`. `Example<TInput, TLabel>` now sealed and inherits from `Example`.
- [x] **Prerequisite:** Updated `IOptimizer` to non-generic signature per D2 — `CompileAsync<TModule>(TModule, IReadOnlyList<Example>, Func<Example, object, float>, CancellationToken)`
- [x] Implement `Evaluator` static class in `LMP.Optimizers` namespace `LMP.Optimizers`:
  - `static Task<EvaluationResult> EvaluateAsync<TModule>(TModule module, IReadOnlyList<Example> devSet, Func<Example, object, float> metric, int maxConcurrency = 4, CancellationToken ct = default) where TModule : LmpModule`
  - Uses `Parallel.ForEachAsync` for concurrent evaluation
  - Calls `module.ForwardAsync(example.WithInputs(), ct)` per example
  - Scores with `metric(example, output)`
  - Collects into `ConcurrentBag<ExampleResult>`
  - Empty devSet returns zeroed result without throwing
- [x] Create `EvaluationResult` sealed record: `PerExample` (IReadOnlyList\<ExampleResult\>), `AverageScore`, `MinScore`, `MaxScore`, `Count`
- [x] Create `ExampleResult` sealed record: `Example`, `Output` (object), `Score` (float)
- [x] 22 unit tests covering: argument validation (5), empty devSet (1), basic evaluation with aggregates (4), metric data access (2), ForwardAsync integration (3), concurrency limit tracking (2), cancellation (1), error propagation (1), result record types (3)
- [x] Updated ExampleTests: +4 new tests for `GetLabel()`, base type, non-generic list, sealed check
- [x] Updated IOptimizerTests: aligned with non-generic `Example` signature + added metric data access test

**Status:** ✅ Complete. 430 total tests pass (56 Abstractions + 35 Core + 71 Modules + 22 Optimizers + 246 SourceGen). Evaluator runs module on dev set with concurrent evaluation, scores each result, and returns correct aggregate statistics.

**Completion criteria:** ✅ `Evaluator.EvaluateAsync` runs module on dev set and returns correct aggregate score.

### 4.2 — Module Cloning ✅ COMPLETE

**Spec:** `compiler-optimizer.md` §4 (Clone)

**Tasks:**
- [x] Add `Clone<TModule>()` method to `LmpModule`:
  - `Clone<TModule>()` non-virtual generic helper calls `protected virtual CloneCore()` and casts
  - `CloneCore()` throws `NotSupportedException` by default; source-gen emits override
- [x] Add `IPredictor.Clone()` method returning `IPredictor` with independent learnable state
- [x] Implement `Predictor<TIn,TOut>.Clone()` via `MemberwiseClone()` + independent Demos/Config
- [x] Extend `ModuleEmitter` to generate `CloneCore()` override:
  - Uses `MemberwiseClone()` for module shallow copy, then replaces each predictor field
  - `[UnsafeAccessor]` for readonly fields/get-only properties; direct assignment for writable
  - Extends `PredictorFieldModel` with `FieldTypeFQN`, `CanAssignDirectly`, `UnsafeAccessorFieldName`
  - `ModuleExtractor` populates new fields based on `IsReadOnly` analysis
- [x] Unit test: cloned module has independent demo lists (modifying clone doesn't affect original)
  - 10 Predictor.Clone tests, 3 LmpModule.Clone tests, ~15 ModuleEmitter CloneCore tests

**Completion criteria:** `module.Clone<T>()` returns a deep copy with independent predictor state.

**Status:** ✅ Complete. 485 total tests pass (59 Abstractions + 59 Core + 71 Modules + 22 Optimizers + 274 SourceGen). Clone infrastructure implemented: `Predictor.Clone()` via MemberwiseClone + independent Demos/Config, `LmpModule.Clone<TModule>()` via source-gen `CloneCore()` with UnsafeAccessor for readonly fields.

### 4.3 — BootstrapFewShot ✅ COMPLETE

**Spec:** `compiler-optimizer.md` §4

**Tasks:**
- [x] Added `AddDemo(object input, object output)` to `IPredictor` interface and `Predictor<TIn,TOut>` implementation — enables optimizers to add demos via erased types from trace entries
- [x] Implement `BootstrapFewShot : IOptimizer` in `LMP.Optimizers`:
  - Constructor: `(int maxDemos = 4, int maxRounds = 1, float metricThreshold = 1.0f)`
  - `CompileAsync<TModule>` algorithm:
    1. `teacher = module.Clone<TModule>()` — deep copy per round
    2. For each example in trainSet (sequential for trace isolation):
       a. `teacher.Trace = new Trace()` — fresh trace per example
       b. `output = await teacher.ForwardAsync(example.WithInputs(), ct)`
       c. `score = metric(example, output)`
       d. If `score >= metricThreshold`: collect trace entries into `ConcurrentDictionary<string, ConcurrentBag<TraceEntry>>`
    3. For each predictor in student (`module.GetPredictors()`):
       `predictor.Demos.Clear()` then `predictor.AddDemo(entry.Input, entry.Output)` for each trace up to maxDemos
    4. Return student module
  - Multi-round support: round N>1 copies demos from student to teacher before processing
  - Swallow exceptions on failed examples (except `OperationCanceledException`)
- [x] 28 unit tests: argument validation (5), empty trainSet (1), basic bootstrapping (3), threshold filtering (3), maxDemos limit (1), failed example swallowing (2), cancellation (2), multi-predictor modules (2), teacher/student isolation (1), IOptimizer interface (1), real predictor integration (1), existing demos cleared (1), no successful traces (1), constructor defaults (1), multi-round (1), trace isolation (1), no predictors (1), large training set (1)

**Status:** ✅ Complete. 513 total tests pass (59 Abstractions + 59 Core + 71 Modules + 50 Optimizers + 274 SourceGen). BootstrapFewShot clones teacher, runs on training set, collects successful traces, fills student predictors with demos.

**Design note:** Examples are processed sequentially (not `Parallel.ForEachAsync`) because `LmpModule.Trace` is a shared property — concurrent ForwardAsync calls on the same module would cause trace cross-contamination. Per-example teacher cloning would enable parallelism but at significant cost. Sequential processing is correct and sufficient for MVP; parallel support can be added later via `AsyncLocal<Trace>` or per-example cloning.

**Completion criteria:** `BootstrapFewShot.CompileAsync` fills `predictor.Demos` from successful traces.

### 4.4 — BootstrapRandomSearch ✅ COMPLETE

**Spec:** `compiler-optimizer.md` §5

**Tasks:**
- [x] Implement `BootstrapRandomSearch : IOptimizer` in `LMP.Optimizers`:
  - Constructor: `(int numTrials = 8, int maxDemos = 4, float metricThreshold = 1.0f, int? seed = null)`
  - `CompileAsync<TModule>` algorithm:
    1. Split trainSet -> 80/20 (train/validation) using `seed` for determinism
    2. For each trial (1..numTrials):
       a. Shuffle trainSplit with different random order
       b. `candidate = await BootstrapFewShot.CompileAsync(module.Clone(), shuffled, metric)`
    3. Evaluate all candidates on validation set via `Task.WhenAll(candidates.Select(c => Evaluator.EvaluateAsync(...)))`
    4. Return candidate with highest `AverageScore`
- [x] Integration test: N=3 trials returns the best-scoring candidate
- [x] Test deterministic seeding produces same result

**Completion criteria:** `BootstrapRandomSearch` runs N trials and returns the best module by evaluation score.

### 4.5 — SaveAsync / LoadAsync Round-Trip Integration ✅ COMPLETE

**Spec:** `artifact-format.md` §4–5

**Tasks:**
- [x] Verify `SaveAsync` writes JSON matching `artifact-format.md` schema:
  - Contains `version: "1.0"`, `module`, `predictors` map
  - Each predictor has `instructions`, `demos` array, optional `config`
  - Atomic write (temp file -> rename)
- [x] Verify `LoadAsync` reads JSON and populates predictor state correctly
- [x] Integration test: optimize -> save -> load into fresh module -> predict produces same results
- [x] Test forward compatibility: unknown JSON properties are ignored
- [x] Fixed `Predictor.LoadState` to handle "value" wrapper for non-object types (string, int, etc.)

**Completion criteria:** Full round-trip: optimize -> save -> load -> predict.

### 4.6 — JSONL Dataset Loader ✅ COMPLETE

**Spec:** `compiler-optimizer.md` §3 (metric usage patterns)

**Tasks:**
- [x] Implement `Example.LoadFromJsonl<TInput, TLabel>(string path, JsonSerializerOptions? options)` static utility
  - Parses each line as `{"input": {...}, "label": {...}}` (case-insensitive outer keys)
  - Uses `JsonDocument` for outer parsing, `JsonElement.Deserialize<T>` for typed inner objects
  - Supports custom `JsonSerializerOptions` for source-gen/AOT contexts
  - Default options use `PropertyNameCaseInsensitive = true`
  - Skips blank lines, reports line numbers in errors
  - Returns `IReadOnlyList<Example<TInput, TLabel>>`
- [x] Unit tests: 18 tests covering simple types, complex records, empty files, blank lines, PascalCase keys, custom options, error cases (null path, file not found, missing properties, invalid JSON, non-object lines, line number reporting), numeric types, large files

**Completion criteria:** JSONL training data loads into typed `Example` records.

### Phase 4 Exit Criteria

- `Evaluator.EvaluateAsync` returns aggregate score across dev set
- `BootstrapFewShot.CompileAsync` fills predictor Demos from successful traces
- `BootstrapRandomSearch` runs N parallel trials, returns best by evaluation score
- Module cloning produces independent copies
- Optimized module saved to JSON, loaded back, predicts identically
- Integration test demonstrates measurable score improvement after optimization

---

## Phase 5: Agents + RAG

**Goal:** Add `ReActAgent<TIn, TOut>` and RAG composition patterns.

**Spec references:** `runtime-execution.md` §5, `public-api.md` §4.7

**Entry criteria:** Phase 2 complete; M.E.AI `AIFunction` / `FunctionInvokingChatClient` available.

### 5.1 — ReActAgent\<TInput, TOutput\> ✅ COMPLETE

**Spec:** `runtime-execution.md` §5

**Tasks:**
- [x] Implement `ReActAgent<TIn, TOut>` in `LMP.Modules`:
  - Constructor: `(IChatClient client, IEnumerable<AIFunction> tools, int maxSteps = 5)`
  - Wraps client with `ChatClientBuilder(client).UseFunctionInvocation().Build()` for automatic tool dispatch
  - Overrides `PredictAsync` to set `ChatOptions.Tools = [.. _tools]` and call wrapped client
  - Calls `_wrappedClient.GetResponseAsync<TOutput>(messages, options, ct)` — M.E.AI handles Think -> Act -> Observe internally
  - Records trace of final (input, output) pair
  - Exposes `Instructions`, `Config`, `Demos` for optimizer access
  - Implements `IPredictor` via `Predictor<TIn,TOut>` base — discoverable by `GetPredictors()`
  - `Clone()` produces independent copy with shared tools and client
- [x] Integration test with mock tools (via `AIFunctionFactory.Create`): agent calls tool, returns typed result
  - `ToolCallFakeChatClient` simulates function-calling protocol with `FunctionCallContent`
  - Tests single tool call, multiple sequential tool calls, trace recording with tools
- [x] Verify agent is optimizable: discoverable by `GetPredictors()`, demos fillable via `AddDemo`
  - 26 tests covering: constructor validation, basic prediction, trace recording, validation/retry,
    IPredictor interface, Clone, GetState/LoadState, tool exposure, integration with tool calling

**Completion criteria:** `ReActAgent` executes Think -> Act -> Observe loop with >=1 tool call.

### 5.2 — RAG Composition Example ✅ COMPLETE

**Spec:** `public-api.md` §4.7 usage example, `runtime-execution.md` §7

**Tasks:**
- [x] Create sample `RagQaModule : LmpModule` demonstrating IRetriever + Predictor composition:
  - Constructor injects `IRetriever` and `IChatClient`
  - `ForwardAsync`: retrieves passages -> builds `AnswerInput` with context -> predicts via `Predictor<AnswerInput, AnswerWithContext>`
  - `GetPredictors()` exposes the predictor for optimizer discovery
  - `LmpAssert` validates confidence in [0.0, 1.0]
- [x] Create `FakeRetriever` for testing (in-memory keyword-based search with score ranking)
- [x] 25 tests: constructor validation, typed + object ForwardAsync, trace recording, confidence validation, GetPredictors/optimizer compatibility, cancellation, FakeRetriever behavior

**Completion criteria:** RAG module retrieves context and passes it to predictor; output is typed.

### Phase 5 Exit Criteria

- `ReActAgent` executes a tool-use loop against mock tools
- RAG module works with fake retriever
- Agent and RAG tests pass against `FakeChatClient` + mock tools
- Agent is optimizable via `GetPredictors()` discovery

---

## Phase 6: Advanced Optimization (Post-MVP)

**Goal:** `MIPROv2` — Bayesian optimization over instructions and demos.

**Spec references:** `compiler-optimizer.md` §6

**Entry criteria:** Phase 4 + Phase 5 complete.

### 6.1 — MIPROv2 Optimizer ✅ COMPLETE

**Tasks:**
- [x] Bootstrap demo pool (reuse BootstrapFewShot to generate initial demo candidates)
- [x] Implement instruction proposal module: LM-generated instruction candidates via `IChatClient`
- [x] Implement TPE sampler (~160 LOC for categorical-only) — `CategoricalTpeSampler` with Laplace smoothing
- [x] Implement Bayesian search loop: each trial samples (instruction index x demo set index) per predictor
- [x] Evaluate trial candidates using `Evaluator.EvaluateAsync`
- [x] Return best trial by evaluation score
- [x] 36 unit tests: constructor validation, arg validation, core algorithm, error resilience, determinism, cancellation, TPE convergence

**Implementation notes:**
- `CategoricalTpeSampler` (internal): minimal TPE for categorical spaces with l(x)/g(x) acquisition, Laplace smoothing, gamma-based good/bad split
- `MIPROv2`: 3-phase compile (bootstrap → propose instructions → Bayesian search), error-resilient instruction proposal (falls back to original if LM fails)
- Reuses `BootstrapRandomSearch.SplitDataset` for train/val split

**Completion criteria:** ✅ MIPROv2 implements instruction + demo search with TPE. 680 tests passing.

---

## Phase 7: Tooling (Post-MVP)

**Goal:** CLI tool and Aspire integration.

**Spec references:** `phased-plan.md` Phase 7

**Entry criteria:** Phase 4 complete; Phase 6 recommended.

### 7.1 — CLI Tool (`dotnet lmp`) ✅ COMPLETE

**Tasks:**
- [x] `dotnet lmp inspect` — reads saved module state JSON and pretty-prints (module name, predictors, instructions, demo counts, config)
- [x] `dotnet lmp optimize` — builds user project, discovers `ILmpRunner` implementation, runs `IOptimizer.CompileAsync`, saves optimized state JSON
- [x] `dotnet lmp eval` — builds user project, discovers `ILmpRunner`, optionally loads saved artifact, evaluates on JSONL dataset via `Evaluator.EvaluateAsync`
- [x] `dotnet lmp run` — builds user project, discovers `ILmpRunner`, deserializes single JSON input, optionally loads artifact, executes `ForwardAsync`, prints result

**Implementation notes:**
- Created `ILmpRunner` interface in `LMP.Abstractions` — user projects implement this to expose module factory, metric, and dataset loading to the CLI
- Added `ILmpRunner.DeserializeInput(string json)` default interface method for `run` command single-input deserialization
- `LMP.Cli` project as a .NET tool (`PackAsTool`, `ToolCommandName=lmp`)
- `ProjectBuilder` — shells out to `dotnet build`, locates output DLL in standard paths
- `RunnerDiscovery` — loads assembly via custom `AssemblyLoadContext`, scans for `ILmpRunner` implementations
- Manual arg parsing (no `System.CommandLine` dependency) for 4 commands
- Exit codes per CLI spec: 0=success, 1=unknown, 2=invalid args, 3=project not found, 4=compile failed, 5=eval failed, 6=artifact error, 7=input parse error
- `--json` flag on inspect/eval/run for machine-readable output
- `--dev <path>` flag on optimize for post-optimization validation scoring
- `--version` flag on main entry point
- Early validation of `--optimizer` name (rejects unknown optimizers at arg parse time)
- 52 tests: argument parsing, file not found, invalid JSON, formatted/JSON output, FormatDemoFields, CLI entry point dispatch, --version, --dev, unknown optimizer, run command (12 new)

### 7.2 — Aspire Integration ✅ COMPLETE

**Tasks:**
- [x] `AddLmpOptimizer()` extension method for Aspire
- [x] Dashboard telemetry for optimization runs

**Deliverables:**
- `src/LMP.Aspire.Hosting/` — New project with `Aspire.Hosting` dependency
  - `LmpOptimizerResource` — Custom Aspire resource representing an optimization run
  - `LmpOptimizerResourceBuilderExtensions` — `AddLmpOptimizer<TModule>()`, `WithTrainData()`, `WithDevData()`, `WithOptimizer<T>()`, `WithOutputPath()`, `WithMaxConcurrency()` builder pattern
  - `LmpTelemetry` — `ActivitySource` + `Meter` with instruments for optimization lifecycle (trial scores, durations, evaluation metrics)
- `test/LMP.Aspire.Hosting.Tests/` — 36 tests covering resource construction, telemetry, and builder extensions
- Fixed pre-existing syntax error in `OptimizeCommand.cs` (missing closing brace)

**Status:** ✅ Complete. 756 total tests pass (90 Abstractions + 86 Core + 274 SourceGen + 122 Modules + 108 Optimizers + 40 Cli + 36 Aspire.Hosting).

### 7.3 — End-to-End Sample (`LMP.Samples.TicketTriage`) ✅ COMPLETE

**Tasks:**
- [x] `samples/LMP.Samples.TicketTriage/` — Console app demonstrating the full LMP workflow
- [x] Type definitions: `TicketInput`, `ClassifyTicket`, `DraftReply` with `[LmpSignature]` and `[Description]`
- [x] `SupportTriageModule : LmpModule` — two-step module (classify → draft) with assertions, tracing, GetPredictors, CloneCore
- [x] `Program.cs` — 7-step demo: single prediction, chain-of-thought, module composition, evaluation, BootstrapFewShot optimization, save/load
- [x] Mock `IChatClient` for deterministic demo without API key
- [x] `data/train.jsonl` (18 examples) and `data/dev.jsonl` (10 examples) dataset files
- [x] Added to `LMP.slnx` under `/samples/` folder

**Implementation notes:**
- Mock client classifies tickets by keyword matching (billing/technical/account/general) and drafts responses by category
- `metricThreshold: 0.3f` on BootstrapFewShot so demos are collected with the deterministic mock
- Predictor `Name` properties must match `GetPredictors()` keys for demo collection to work
- ChainOfThought records the unwrapped `TOutput` (not `ChainOfThoughtResult<T>`) in trace, so `AddDemo` works correctly during optimization

---

## Phase 8: Advanced (Post-MVP)

**Goal:** Experimental features pushing the .NET platform advantage.

**Spec references:** `phased-plan.md` Phase 8

**Entry criteria:** Phase 2 complete; C# 14 interceptor feature available.

- [x] C# 14 interceptors for zero-dispatch `PredictAsync` optimization
- [x] `[Predict]` partial method sugar — source gen emits method body
- [x] `ProgramOfThought<TIn, TOut>` — LM generates C# code -> Roslyn scripting executes -> structured result

**Implementation notes (ProgramOfThought):**
- `ProgramOfThought<TIn, TOut>` extends `Predictor<TIn, TOut>` in `LMP.Modules`
- Two-step flow: (1) internal `Predictor<TIn, CodeGenerationOutput>` asks LM to generate C# code, (2) Roslyn scripting executes it
- `CodeGenerationOutput` record: `Reasoning` + `Code` fields with `[Description]` attributes for LM guidance
- `ScriptGlobals<TInput>` exposes `Input` property to generated scripts
- Sandboxed execution: restricted imports (System, System.Linq, System.Collections.Generic, System.Text, System.Text.Json), configurable timeout (default 30s)
- Automatic retry on compilation errors, runtime errors, and timeout — error fed back to LM
- JSON round-trip conversion for structural type matching (anonymous types → TOutput)
- Added `Microsoft.CodeAnalysis.CSharp.Scripting` 5.3.0 dependency to LMP.Modules
- 22 new tests: constructor, ExecuteCodeAsync (arithmetic, Linq, Fibonacci, Input globals, compilation/runtime errors, null, timeout), PredictAsync E2E (success, trace, input access, retry, max retries, validation), Clone, serialization

**Implementation notes ([Predict] partial method sugar):**
- `PredictAttribute` in `LMP.Abstractions` — `[AttributeUsage(AttributeTargets.Method)]` marker attribute
- `LmpModule.Client` — new `protected IChatClient?` property for [Predict] backing fields to bind to
- `ModuleExtractor` extended to discover `[Predict]` partial methods (checks: has [Predict], is partial definition, returns Task<T>, single parameter)
- `PredictMethodModel` record: `MethodName`, `InputTypeFQN`, `OutputTypeFQN`, `InputParameterName`
- `ModuleModel` extended with `PredictMethods` collection (backwards-compatible with existing callers)
- `ModuleEmitter` emits: (1) `__predict_{Method}` nullable backing fields, (2) partial method bodies with lazy initialization from Client, (3) backing fields included in GetPredictors(), (4) CloneCore handles nullable [Predict] fields
- `ModuleJsonContextEmitter` includes [Predict] method types in JsonContext
- 18 new tests: direct emitter (backing fields, method bodies, GetPredictors, mixed, clone, parameter names, no-predict), pipeline integration (single, multiple, mixed, non-partial, JsonContext), attribute usage
- 6 additional robustness tests: syntax validity (single/mixed), edge cases (0 params ignored, 2+ params ignored, non-Task return ignored, global namespace)

**Implementation notes (C# 14 interceptors):**
- Pipeline 5 in `LmpSourceGenerator.cs` scans for `PredictAsync` `InvocationExpressionSyntax` call sites
- `InterceptorExtractor` validates: method is on `Predictor<TIn,TOut>`, types are concrete (not open generic), TOutput has `[LmpSignature]`
- Uses Roslyn `SemanticModel.GetInterceptableLocation()` API (Roslyn 5.3.0) for location encoding
- `InterceptorCallSiteModel` record stores location version/data, type FQNs, display location
- `InterceptorEmitter` groups call sites by (InputType, OutputType) — shared interceptor method per type pair
- **True zero-dispatch inlining**: interceptors inline entire PredictAsync logic (PromptBuilder.BuildMessages, GetResponseAsync<T>, retry loop, trace)
- Derived type guard: `if (self.GetType() != typeof(Predictor<TIn,TOut>))` falls back to virtual dispatch for ChainOfThought etc.
- PromptBuilder changed from `file static class` to `internal static class` for cross-file access by interceptors
- PromptBuilder gets 4-param `BuildMessages(instructions, input, demos, lastError)` overload matching `MessageBuilder` delegate
- `Predictor<TIn,TOut>.Client` — new public property exposing `IChatClient` for interceptor use
- File-local `InterceptsLocationAttribute` declaration in generated code (recommended pattern)
- **Opt-in**: consumer must define `LMP_INTERCEPTORS` constant and `<InterceptorsNamespaces>$(InterceptorsNamespaces);LMP.Generated</InterceptorsNamespaces>`
- Pipeline checks `CSharpParseOptions.PreprocessorSymbolNames` for `LMP_INTERCEPTORS` before emitting
- 30 tests: emitter (empty, header, usings, attribute, signature, guard, PromptBuilder, client, trace, retry, null check, grouping, syntax, instructions), extractor predicates, pipeline (valid, inlined logic, guard, no call, multi-call, no signature, opt-in)
- 20 new tests: emitter (empty, header, usings, attribute declaration, class shape, location attributes, method signature, wiring order, grouping, separate types, global namespace, generated code attribute), extractor (non-invocation, PredictAsync match, other methods, static calls), PromptBuilder 4-param (overload shape, lastError handling, delegation)

---

## Implementation Priority Summary

| Priority | Phase | Est. Complexity | Key Risk | Status |
|---|---|---|---|---|
| **P0** | Phase 1: Abstractions | Simple | Over-engineering before usage patterns clear | ✅ Complete |
| **P0** | Phase 2: Source Generator + Core Predictor | Complex | Generator debugging; netstandard2.0 constraints; snapshot test infra | ✅ Complete |
| **P1** | Phase 3: Reasoning Modules | Medium | CoT source-gen trigger for extended output types | ✅ Complete |
| **P1** | Phase 4: Evaluation + BootstrapFewShot | Complex | Thread-safe trace collection; module cloning source-gen | ✅ Complete |
| **P1** | Phase 5: Agents + RAG | Medium | M.E.AI FunctionInvokingChatClient surface area | ✅ Complete |
| **P2** | Phase 6: Advanced Optimization (MIPROv2) | Complex | Bayesian search backend / TPE sampler | ✅ Complete |
| **P2** | Phase 7: Tooling (CLI + Aspire) | Medium | CLI ergonomics | ✅ Complete |
| **P3** | Phase 8: Advanced (Interceptors) | Complex | C# 14 interceptor API newness | ✅ Complete |

---

## Phase Dependency Graph

```
Phase 1 --> Phase 2 --+--> Phase 3 --+
                      |              +--> Phase 4 --+--> Phase 6 --> Phase 7
                      +--------------+              |
                      +--> Phase 5 -----------------+
                      +--> Phase 8 (independent, needs only Phase 2)
```

Phase 4 recommends Phase 3 but does not require it. Phase 6 requires both Phase 4 and Phase 5.

---

## Key Architecture Decisions (for reference)

1. **Separate `TInput` / `TOutput` types** — NOT DSPy's single Signature class. Matches `IChatClient.GetResponseAsync<T>()`.
2. **Source generator emits:** PromptBuilder, JsonTypeInfo, GetPredictors(), ChainOfThought extended output, module JsonSerializerContext, module Clone(), diagnostics.
3. **LMP depends ONLY on `IChatClient`** from `Microsoft.Extensions.AI`. No other LM abstraction.
4. **`Predictor<TInput, TOutput>`** is the core primitive with learnable state (Demos, Instructions, Config).
5. **`LmpModule`** with `ForwardAsync()` is plain C# composition — no graphs, no IR.
6. **Optimizers** return the same module type with parameters filled in — no new types.
7. **`CompileAsync`** naming aligns with DSPy's "compilation" terminology.
8. **`SaveAsync`/`LoadAsync`** on `LmpModule` — simple JSON, no archive format, atomic writes.
9. **Source generator targets `netstandard2.0`** — separate `LMP.SourceGen` project.
10. **Three diagnostics only:** LMP001 (missing description — warning), LMP002 (non-serializable — error), LMP003 (non-partial record — error).

---

*Generated from spec analysis. Solution skeleton complete (Phase 1.1). Implementation starts at Phase 1.2.*

---

## Phase 9: Doc Freshness + CostAwareSampler

> **Status:** In progress
> **Prerequisite:** Phases 1-8 complete, 964 tests passing
> **Context:** Specs were written in Phases 1-7 but the code evolved significantly (typed modules, async metrics, ISampler, SMAC, GEPA, TensorPrimitives, AOT). Specs are heavily stale. CostAwareSampler adds cost-aware Bayesian optimization from ML.NET/FLAML CostFrugal (AAAI 2021).

### Phase 9A: Doc Spec Freshness

#### Task 9A.1: Fix `public-api.md` ✅

**File:** `docs/02-specs/public-api.md`
**Reference code:** `src/LMP.Abstractions/`, `src/LMP.Core/Predictor.cs`, `src/LMP.Optimizers/`

Fix these discrepancies between spec and actual code:
- `Demos` type: doc says `IReadOnlyList<(TInput, TOutput)>` → actual: `List<(TInput Input, TOutput Output)>`
- `PredictorConfig` doesn't exist → actual: `ChatOptions Config` (from M.E.AI)
- `PredictAsync` signature missing `trace`, `validate`, `maxRetries` parameters
- ADD missing APIs: `SerializerOptions` property, `SetPromptBuilder()` method
- ADD: `Metric.Create<TPredicted, TExpected>` predicate overload, `Metric.CreateAsync`
- ADD: `ISampler` interface section (Propose/Update pattern)
- ADD: `EvaluationBridge` section (LMP.Extensions.Evaluation)

**Completion:** `dotnet build` passes. Spec matches actual public API surface.

#### Task 9A.2: Fix `runtime-execution.md` ✅

**File:** `docs/02-specs/runtime-execution.md`
**Reference code:** `src/LMP.Core/Predictor.cs`

- `Demos` type: doc says `List<Example<TIn,TOut>>` → actual: `List<(TInput Input, TOutput Output)>`
- Expand retry/validation logic: `maxRetries`, `LmpAssertionException`, `LmpMaxRetriesExceededException`
- ADD: `SetPromptBuilder()` / `MessageBuilder` mechanism (how source gen plugs into runtime)
- ADD: TensorPrimitives usage note in Evaluator section

**Completion:** Code examples in doc compile against actual API.

#### Task 9A.3: Fix `compiler-optimizer.md` ✅

**File:** `docs/02-specs/compiler-optimizer.md`
**Reference code:** `src/LMP.Optimizers/`

This is the most stale spec. Fix:
- `GetPredictors()` code example uses wrong return type (should be `IReadOnlyList<(string Name, IPredictor Predictor)>`)
- ADD **entire section** for GEPA optimizer (currently COMPLETELY undocumented)
- ADD section for SmacSampler (random forest + Expected Improvement)
- ADD section for TraceAnalyzer + warm-start loop
- ADD section for ISampler interface + CategoricalTpeSampler
- ADD TensorPrimitives usage in Evaluator (TensorPrimitives.Average, Min, Max)
- ADD typed Evaluator overloads (`EvaluateAsync<TInput, TPredicted, TExpected>`)

**Completion:** All optimizer classes documented with correct APIs.

#### Task 9A.4: Fix `phased-plan.md` ✅

**File:** `docs/01-architecture/phased-plan.md`

- Fix: `Predictor<TInput, TOutput>` is a **class**, not "interface" (line 48)
- Remove: `PredictorConfig` phantom type from Phase 1 deliverables
- Fix: `OptimizeAsync` → `CompileAsync` (Phase 4 API name)
- Update: Phases 6-8 are COMPLETE, not "Post-MVP"
- ADD: Phase completion status summary

**Completion:** Plan reflects actual implementation state.

#### Task 9A.5: Verify `system-architecture.md` ✅

**File:** `docs/01-architecture/system-architecture.md`

- Verify 4-layer diagram accuracy — should reflect:
  - ISampler abstraction in Layer 3
  - LMP.Extensions.Evaluation in extension layer
  - LMP.Extensions.Z3 in extension layer
  - AOT compatibility note
  - TensorPrimitives as internal implementation detail

**Completion:** Architecture diagram matches actual project structure.

#### Task 9A.6: Update `AGENTS.md` ✅

**File:** `AGENTS.md`

Solution structure is stale. Add:
- `LMP.Extensions.Evaluation/` — M.E.AI evaluation bridge
- `LMP.Extensions.Z3/` — Z3 constraint optimization
- `LMP.Aspire.Hosting/` — Aspire integration
- Update LMP.Optimizers description: add ISampler, SmacSampler, GEPA, TraceAnalyzer
- Update test projects list to match actual
- Update dependencies list (System.Numerics.Tensors, Microsoft.Z3)

**Completion:** `AGENTS.md` accurately describes the actual solution structure.

---

### Phase 9B: CostAwareSampler

> **Design principle:** Reuse M.E.AI's `UsageDetails` from `ChatResponse.Usage`. Auto-collect multi-dimensional cost. User provides `Func<TrialCost, double>` projection — same pattern as `Metric.Create`.

#### Task 9B.1: Capture ChatResponse.Usage in Predictor/Trace ✅

**Files:** `src/LMP.Abstractions/Trace.cs`, `src/LMP.Core/Predictor.cs`

Currently `Predictor.PredictAsync` calls `GetResponseAsync<TOutput>` and only uses `response.Result`, discarding `response.Usage`. Fix:

1. `TraceEntry` record: add `UsageDetails? Usage` parameter (from `Microsoft.Extensions.AI`)
2. `Trace.Record()`: accept optional `UsageDetails?` parameter
3. `Predictor.PredictAsync`: pass `response.Usage` when recording trace
4. `Trace`: add convenience rollup properties:
   - `long TotalTokens` — sum of all entries' TotalTokenCount
   - `int TotalApiCalls` — count of entries

**Tests:** TraceEntry records Usage. Trace.TotalTokens aggregates correctly.
**Completion:** `dotnet test` passes. Usage data flows through traces.

#### Task 9B.2: Create TrialCost + Extend ISampler ✅

**Files:** `src/LMP.Abstractions/TrialCost.cs` (new), `src/LMP.Abstractions/ISampler.cs`

1. Create `TrialCost` record:
```csharp
public record TrialCost(
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long ElapsedMilliseconds,
    int ApiCalls);
```

2. Extend `ISampler` with default interface method:
```csharp
void Update(Dictionary<string, int> config, float score, TrialCost cost)
    => Update(config, score);
```

This is backward-compatible — CategoricalTpeSampler and SmacSampler don't need changes.

**Tests:** TrialCost record equality. ISampler default method ignores cost.
**Completion:** `dotnet build && dotnet test` — 0 regressions.

#### Task 9B.3: Implement CostAwareSampler ✅

**Files:** `src/LMP.Optimizers/Samplers/CostAwareSampler.cs` (new), internal helpers

Port ML.NET CostFrugal (AAAI 2021 Flow2) discretized for categoricals:

```csharp
public class CostAwareSampler : ISampler
{
    public CostAwareSampler(
        Dictionary<string, int> cardinalities,
        Func<TrialCost, double>? costProjection = null, // default: c => c.TotalTokens
        int seed = 42);
}
```

Internal classes:
- `SearchThread` (~120 LOC): cost tracking, step adaptation, convergence detection
- `Flow2Categorical` (~180 LOC): discretized local search on categorical space
- Vector helpers (~60 LOC): sphere sampling, normalize, project to categorical bounds

User-customizable cost projection examples:
- Default: `cost => cost.TotalTokens`
- Dollar pricing: `cost => cost.OutputTokens * 0.06/1000 + cost.InputTokens * 0.01/1000`
- Latency: `cost => cost.ElapsedMilliseconds`
- Weighted blend: `cost => cost.TotalTokens * 0.7 + cost.ElapsedMilliseconds * 0.3`

**Tests:** ~300 LOC — convergence, step sizing, cost projection, categorical discretization.
**Completion:** `dotnet test` passes with CostAwareSampler tests.

#### Task 9B.4: Wire Cost Collection into MIPROv2 ✅

**File:** `src/LMP.Optimizers/MIPROv2Optimizer.cs`

In the trial evaluation loop:
1. Wrap `Evaluator.EvaluateAsync` call with `Stopwatch`
2. Accumulate token counts from the candidate module's `Trace`
3. Build `TrialCost` from Stopwatch + Trace usage data
4. Call `sampler.Update(config, score, cost)` instead of `sampler.Update(config, score)`

**Tests:** MIPROv2 passes TrialCost when sampler supports it.
**Completion:** `dotnet test` — 0 regressions in existing MIPROv2 tests.

#### Task 9B.5: CostAwareSampler Tests + Sample ✅

**Files:** `test/LMP.Optimizers.Tests/CostAwareSamplerTests.cs`, `samples/LMP.Samples.AdvancedOptimizers/Program.cs`

Unit tests (~300 LOC):
- Basic propose/update cycle
- Cost projection customization
- Convergence with varying costs
- Backward compat: non-cost Update still works
- Integration: MIPROv2 + CostAwareSampler

AdvancedOptimizers sample update:
- Add section showing CostAwareSampler with custom cost projection
- Compare cost-aware vs cost-blind optimization results
- Demonstrate dollar pricing, latency, and blended cost functions

**Completion:** All tests pass. Sample builds and runs.

---

## Phase 10: benchmark-samples Branch — Merge Readiness

> **Status:** In progress. GEPA algorithm bugs (3) and SourceGen ChainOfThought fix already committed.
> **Branch:** benchmark-samples
> **Context:** 4 benchmark samples (MathReasoning, IntentClassification, FacilitySupport, AdvancedRag)
> were audited against DSPy/GEPA reference implementations and LMP docs. FacilitySupport verified
> running (51%→63%). Remaining tasks fix rate-limit reliability, correctness, and LMP-idiomatic patterns.
> **All 1,061 tests must pass after each task.**

### Task 10.1: Fix wrong duration variable in Middleware sample ✅

**File:** `samples/LMP.Samples.Middleware/Program.cs`

Line 131 prints `{warmResult.Count}ms` as the "warm cache duration". `warmResult.Count` is the
number of dev examples (e.g., 10), not elapsed milliseconds. This shows "10ms" regardless of actual
time — a silent lie that defeats the purpose of the cache speedup benchmark.

Fix: Change `{warmResult.Count}ms` to `{sw.ElapsedMilliseconds}ms` on line 131.

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): correct warm cache duration display in Middleware sample`.

---

### Task 10.2: Add cooldown before final eval in GEPA sample ✅

**File:** `samples/LMP.Samples.GEPA/Program.cs`

After `gepa.CompileAsync(...)` on line 138, the sample immediately fires `Evaluator.EvaluateAsync`
on line 139 without a cooldown. GEPA runs 20 iterations with ~100+ LLM calls. Without a cooldown,
the final eval fires into a saturated rate-limit window, causing failures counted as 0.0 scores.

FacilitySupport correctly adds a 30s cooldown at lines 159-160. This sample must match that pattern.

Fix: Between lines 138 and 139, add:
```csharp
Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));
```

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): add rate-limit cooldown before final eval in GEPA sample`.

---

### Task 10.3: Add `maxConcurrency` constructor param to BootstrapRandomSearch ✅

**File:** `src/LMP.Optimizers/BootstrapRandomSearch.cs`

DSPy's `BootstrapFewShotWithRandomSearch` exposes `num_threads` for parallel evaluation control.
LMP's BRS evaluates all N trial candidates in parallel via `Task.WhenAll` (line 89-92) but passes
no `maxConcurrency` to `Evaluator.EvaluateAsync` (defaults to 4). Users have no way to reduce
concurrency pressure for multi-predictor modules or high-concurrency environments.

Fix:
1. Add `int maxConcurrency = 4` parameter to `BootstrapRandomSearch(...)` constructor (after `seed`)
2. Store as `private readonly int _maxConcurrency`
3. Pass `maxConcurrency: _maxConcurrency` to `Evaluator.EvaluateAsync(candidate, valSplit, metric, maxConcurrency: _maxConcurrency, ...)` on line 90
4. Add XML doc comment on the new parameter
5. Update any existing tests that construct `BootstrapRandomSearch` — parameter is optional with default 4, so most tests won't need changes

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `feat(optimizers): expose maxConcurrency on BootstrapRandomSearch`.

---

### Task 10.4: Add `maxConcurrency` constructor param to MIPROv2 ✅

**File:** `src/LMP.Optimizers/MIPROv2.cs`

DSPy's `MIPROv2` exposes `num_threads` for parallel evaluation control. LMP's MIPROv2 Phase 3
trial evaluation calls `Evaluator.EvaluateAsync(candidate, subSample, ...)` without passing
`maxConcurrency` (defaults to 4). Users cannot tune concurrency for rate-limit protection.

Fix:
1. Add `int maxConcurrency = 4` parameter to `MIPROv2(...)` constructor (after `seed`)
2. Store as `private readonly int _maxConcurrency`
3. Find the Phase 3 trial evaluation `Evaluator.EvaluateAsync` call and pass `maxConcurrency: _maxConcurrency`
4. Add XML doc comment on the new parameter
5. Run tests — existing callers use named args, new param has default, no breaking changes

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `feat(optimizers): expose maxConcurrency on MIPROv2`.

---

### Task 10.5: Rate-limit protection in IntentClassification sample ✅

**Prerequisite:** Tasks 10.3 and 10.4 must be complete (BRS and MIPROv2 now have maxConcurrency param).

**File:** `samples/LMP.Samples.IntentClassification/Program.cs`

Three issues that will cause rate-limit failures on Azure:
1. All 3 `Evaluator.EvaluateAsync` calls use default maxConcurrency=4 (lines 108, 123, 145)
2. No cooldown between BRS completing and MIPROv2 starting
3. No cooldown between MIPROv2 completing and the final eval

Fix — apply FacilitySupport's established pattern:
1. Lines 108, 123, 145: add `maxConcurrency: 2` to each `Evaluator.EvaluateAsync` call
2. After the BRS eval on line 125 (after `PrintPredictorState`), add:
   ```csharp
   Console.WriteLine("  Cooling down before MIPROv2...");
   await Task.Delay(TimeSpan.FromSeconds(15));
   ```
3. After `mipro.CompileAsync` on line 144, add:
   ```csharp
   Console.WriteLine("  Cooling down before final evaluation...");
   await Task.Delay(TimeSpan.FromSeconds(30));
   ```
4. BRS constructor (line 121): add `maxConcurrency: 2`
5. MIPROv2 constructor (line 134): add `maxConcurrency: 2`

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): add rate-limit protection to IntentClassification sample`.

---

### Task 10.6: Rate-limit protection in MathReasoning sample ✅

**Prerequisite:** Tasks 10.3 and 10.4 must be complete.

**File:** `samples/LMP.Samples.MathReasoning/Program.cs`

Same root cause as IntentClassification. Three Evaluator calls use default maxConcurrency=4
(lines 96, 108, 134) and no cooldowns between optimizer stages.

Fix — same pattern as Task 10.5:
1. Lines 96, 108, 134: add `maxConcurrency: 2` to each `Evaluator.EvaluateAsync` call
2. After the BRS eval on line 110 (after `PrintPredictorState`), add:
   ```csharp
   Console.WriteLine("  Cooling down before MIPROv2...");
   await Task.Delay(TimeSpan.FromSeconds(15));
   ```
3. After `mipro.CompileAsync` on line 133, add:
   ```csharp
   Console.WriteLine("  Cooling down before final evaluation...");
   await Task.Delay(TimeSpan.FromSeconds(30));
   ```
4. BRS constructor (line 106): add `maxConcurrency: 2`
5. MIPROv2 constructor (line 123): add `maxConcurrency: 2`

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): add rate-limit protection to MathReasoning sample`.

---

### Task 10.7: Rate-limit cooldowns in AdvancedRag and AdvancedOptimizers samples ✅

**Files:**
- `samples/LMP.Samples.AdvancedRag/Program.cs`
- `samples/LMP.Samples.AdvancedOptimizers/Program.cs`

**AdvancedRag:** Already has `maxConcurrency: 2` on all eval calls. Missing cooldown after MIPROv2
before final eval (lines 174-175). Fix: add 30s cooldown between them.

**AdvancedOptimizers:** After the last optimizer (`smacOptimized`, around line 183), add 30s cooldown
before `Evaluator.EvaluateAsync(smacOptimized, ...)` on line 184. With 5 sequential optimizer runs,
the final eval fires into the most saturated rate-limit window of all samples.

Fix for AdvancedRag — after `mipro.CompileAsync(...)` call, add:
```csharp
Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));
```

Fix for AdvancedOptimizers — after `miproSmac.CompileAsync(...)`, before the final eval call, add:
```csharp
Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));
```

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): add rate-limit cooldowns to AdvancedRag and AdvancedOptimizers samples`.

---

### Task 10.8: AdvancedRag — CragConfidence enum (LMP-idiomatic) ✅

**Files:**
- `samples/LMP.Samples.AdvancedRag/Types.cs`
- `samples/LMP.Samples.AdvancedRag/AdvancedRagModule.cs`

Current code in `CragOutput` uses `string Confidence`. In `AdvancedRagModule.cs`, the validator
uses case-sensitive string pattern matching (`x.Confidence is "correct" or "ambiguous" or "incorrect"`).
If the LLM returns "Correct" (capitalized), validation fails and wastes a retry.

LMP natively supports C# enums via `JsonStringEnumConverter` emitted by the source generator.
This is the idiomatic way to constrain string outputs — equivalent to DSPy's `typing.Literal`.

Fix:
1. In `Types.cs`, add:
   ```csharp
   /// <summary>CRAG confidence level for retrieved context sufficiency.</summary>
   public enum CragConfidence { Correct, Ambiguous, Incorrect }
   ```
2. In `Types.cs`, change `CragOutput.Confidence` from `string` to `CragConfidence`:
   ```csharp
   [Description("Whether the retrieved context is sufficient: Correct, Ambiguous, or Incorrect")]
   public required CragConfidence Confidence { get; init; }
   ```
   Remove the old `[Description]` that lists the string literals — the enum values are self-describing.
3. In `AdvancedRagModule.cs`, update the validator:
   - Remove string-matching validator entirely (enum constraint in JSON Schema handles it)
   - Or simplify to: no `validate:` needed since source gen enforces the enum type
4. Update ForwardAsync logic to use enum comparisons:
   - `if (crag.Confidence == CragConfidence.Correct) break;`
   - `if (crag.Confidence == CragConfidence.Incorrect) { ... }`

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `refactor(samples): use CragConfidence enum in AdvancedRag (LMP-idiomatic)`.

---

### Task 10.9: Add null guards to benchmark sample metrics + increase AdvancedRag reranker retries ✅

**Files:**
- `samples/LMP.Samples.IntentClassification/Program.cs`
- `samples/LMP.Samples.MathReasoning/Program.cs`
- `samples/LMP.Samples.AdvancedRag/Program.cs`
- `samples/LMP.Samples.AdvancedRag/AdvancedRagModule.cs`

**Metric null guards:** If a module's `ForwardAsync` exhausts all retries and the output record
is null (possible for class types when deserialization produces default), calling the metric on null
throws NRE. Add defensive null check at the top of each metric:

- IntentClassification `exactMatch` (line ~86): `if (predicted is null) return false;`
- MathReasoning `exactMatch` (line ~86): `if (predicted is null) return false;`
- AdvancedRag `answerMetric` (line ~105): `if (predicted is null || string.IsNullOrWhiteSpace(predicted.Answer)) return 0f;`
  (The current code calls `predicted.Answer.Split(...)` unconditionally — crashes on null predicted)

**AdvancedRag reranker maxRetries:** In `AdvancedRagModule.cs`, the reranker uses `maxRetries: 1`
(line 95). The relevance score 0-10 range validation can fail on a first malformed response.
A single retry leaves no margin. Change `maxRetries: 1` to `maxRetries: 2`.

**Completion:** `dotnet build --no-restore && dotnet test` pass. Commit with `fix(samples): add metric null guards and increase reranker retries in benchmark samples`.

---

### Task 10.10: Run all 4 benchmark samples end-to-end and verify improvement ✅

**Prerequisite:** All tasks 10.1–10.9 must be complete and committed.

Run each benchmark sample and verify it completes successfully, showing improvement over baseline.
User secrets for Azure OpenAI are already configured on this machine.

**Run each sample from the repo root:**

```powershell
dotnet run --project samples/LMP.Samples.IntentClassification
dotnet run --project samples/LMP.Samples.MathReasoning
dotnet run --project samples/LMP.Samples.AdvancedRag
dotnet run --project samples/LMP.Samples.FacilitySupport
```

**For each sample, verify:**
1. Completes without exceptions or unhandled errors
2. Final score (optimized) > baseline score (meaning optimization actually helped)
3. No `429 Too Many Requests` / rate-limit failures that produce chains of 0.0 scores
4. Progress output looks reasonable (no NaN scores, no "frontier= 0" in GEPA)

**FacilitySupport is already known-good (51%→63%). Run it last as a sanity check.**

**If any sample fails:**
- Rate-limit failures → increase cooldown durations or further reduce maxConcurrency
- Score regression (optimized < baseline) → investigate and fix, then re-run
- Build/runtime error → fix the error, recommit, then re-run

**Verified results (all 4 samples exit 0, all show improvement over simple baseline):**
```
IntentClassification: ~72% improvement with BRS (verified prior session)
MathReasoning:        baseline=89% (high baseline; documented limitation — few-shot can hurt)
AdvancedRag:          SimpleRAG=36.1%, MultiHop=40.8%, MIPROv2=39.8% (+3.7pp over simple baseline)
FacilitySupport:      baseline=50.0%, GEPA=64.7% (+14.7pp)
```

Note on AdvancedRag: MIPROv2 (39.8%) is 1pp below multi-hop unoptimized (40.8%) — within statistical
variance on 50 dev examples (1pp = 1 example). The optimizer correctly selected 0 demos for
rerank/answer predictors (empty-subset fix working), and softer answer instructions. The multi-hop
pipeline itself adds +4.7pp over simple RAG baseline.

**Completion:** All 4 samples run to completion showing improvement. Update ❌ to ✅.
Commit with `chore: verify all 4 benchmark samples run successfully`.

---

### Phase 10 Exit Criteria

All tasks 10.1–10.10 complete. `dotnet build --no-restore && dotnet test` passes with 0 errors,
0 warnings. All 4 benchmark samples verified running with improvement. Branch is ready to merge.


---

## Phase J — Calibration Unlock

> **Baseline state entering Phase J:** Phases A–I complete. 1,551 tests passing. Key types available:
> - `IOptimizationTarget`, `OptimizationContext`, `OptimizationPipeline`, `TypedParameterSpace`, `ParameterKind` (Continuous/Integer/Categorical/StringValued/Subset), `ParameterAssignment`, `ISearchStrategy`, `ISampler`, `CategoricalTpeSampler`, `SmacSampler`, `LmpTraceMiddleware` (internal to LMP.Core), `ChatClientTarget`, `ModuleTarget`, `Trial`, `TrialHistory`, `LmpPipelines.Auto(module, client, goal)`, `BayesianCalibration` does NOT exist yet.
> - `CategoricalTpeSampler.Propose()` returns `Dictionary<string,int>` (ISampler), `.Update(Dict<string,int>, float)` stores categorical indices.
> - `ParameterAssignment.With(name, double)` stores a double value. `ChatClientTarget.WithParameters` handles `TryGet<double>("temperature")` correctly.
> - Build/test commands: `dotnet build --no-restore` and `dotnet test` (both from repo root).

### Task J.1: ContinuousDiscretizer ✅

**File to create:** `src/LMP.Optimizers/Internal/ContinuousDiscretizer.cs`

Create an `internal sealed class ContinuousDiscretizer` in namespace `LMP.Optimizers` that converts numeric/categorical parameter kinds into categorical index space for TPE sampling, and provides decode/encode round-trips.

**Purpose:** `BayesianCalibration` needs to optimize `Continuous` (e.g., temperature 0–2) and `Integer` parameters using `CategoricalTpeSampler` (which works with `Dictionary<string,int>` indices). `ContinuousDiscretizer` handles the mapping.

**API:**

```csharp
internal sealed class ContinuousDiscretizer
{
    // Maps param name → number of discrete steps. Feed to CategoricalTpeSampler constructor.
    public Dictionary<string, int> Cardinalities { get; }

    // Creates a discretizer from a TypedParameterSpace.
    // Only processes Continuous, Integer, and Categorical params.
    // Skips StringValued, Subset, Composite.
    // continuousSteps: number of grid points for Continuous params (must be >= 2)
    public static ContinuousDiscretizer From(TypedParameterSpace space, int continuousSteps = 8);

    // Decodes a categorical-index config to actual typed values.
    // Continuous → double (grid value), Integer → int (grid value), Categorical → int (index)
    public ParameterAssignment Decode(Dictionary<string, int> catConfig);

    // Encodes an actual-value assignment back to nearest categorical indices.
    // Used to update the sampler after evaluating a decoded assignment.
    public Dictionary<string, int> Encode(ParameterAssignment assignment);
}
```

**Grid construction rules:**
- `Continuous(min, max, Scale.Linear)`: `continuousSteps` evenly-spaced values from min to max inclusive. E.g., 8 steps from 0 to 2 → [0.0, 0.286, 0.571, 0.857, 1.143, 1.429, 1.714, 2.0].
- `Continuous(min, max, Scale.Log)`: `continuousSteps` log-uniformly spaced values. Requires `min > 0 && max > 0`; throw `ArgumentException` if violated.
- `Integer(min, max)`: `min(max-min+1, 20)` evenly-spaced integer values covering the range, deduped. E.g., Integer(1,100) → 20 unique integers. Integer(1,5) → all 5 values.
- `Categorical(count)`: no grid needed; appears in Cardinalities with cardinality = count. Decode returns index as int, Encode extracts int directly from assignment.
- Validate `continuousSteps >= 2`; throw `ArgumentOutOfRangeException` if < 2.
- Integer grid deduplication: after generating candidates, deduplicate and use the deduplicated list as the grid.

**Encode logic:** Find nearest grid index by absolute distance (for Continuous: distance between doubles; for Integer: distance between ints). For Categorical, extract the int value directly.

**File location:** `src/LMP.Optimizers/Internal/` (create the folder). No `Internal/` subfolder needed if it complicates things — place in `src/LMP.Optimizers/` directly but mark `internal sealed`.

**Test file to create:** `test/LMP.Optimizers.Tests/ContinuousDiscretizerTests.cs`

Tests (~10):
- Linear grid has correct count, min/max endpoints present
- Log grid has correct count, values are log-spaced (ratio between consecutive values is approximately constant)
- Log grid with min <= 0 throws ArgumentException
- continuousSteps < 2 throws ArgumentOutOfRangeException
- Integer grid: small range (1..5) → all 5 values; large range (1..100) → 20 deduped values
- Decode returns double for Continuous param (not int index)
- Decode returns int for Integer param
- Decode returns int for Categorical param
- Encode(Decode(catConfig)) produces same or equal-distance catConfig (round-trip invariant)
- From() skips StringValued and Subset params (not in Cardinalities)

**Commit message:** `feat(optimizers): add ContinuousDiscretizer for numeric parameter discretization`

---

### Task J.2: BayesianCalibration optimizer ✅

**File to create:** `src/LMP.Optimizers/BayesianCalibration.cs`

Create `public sealed class BayesianCalibration : IOptimizer` in namespace `LMP.Optimizers`.

**Constructor:**
```csharp
/// <param name="numRefinements">Number of TPE refinement iterations. Default is 10.</param>
/// <param name="continuousSteps">Grid resolution for Continuous parameters. Default is 8.</param>
/// <param name="seed">Optional random seed for reproducibility.</param>
public BayesianCalibration(int numRefinements = 10, int continuousSteps = 8, int? seed = null)
```

**`OptimizeAsync` algorithm:**

1. `space = ctx.Target.GetParameterSpace()` — the target's own hyperparameter space.
   - **NOT** `ctx.SearchSpace` — that belongs to MIPROv2/BFS instruction search.
   - `ModuleTarget.GetParameterSpace()` always returns `TypedParameterSpace.Empty` — this is a safe no-op path.

2. Build `calibrationSpace`: iterate `space.Parameters`, keep only `Continuous`, `Integer`, `Categorical` kinds. Skip `StringValued`, `Subset`, `Composite`. Build a new `TypedParameterSpace` with only those params. If `calibrationSpace.IsEmpty` → return immediately (no-op).

3. `discretizer = ContinuousDiscretizer.From(calibrationSpace, _continuousSteps)`

4. `strategy = new CategoricalTpeSampler(discretizer.Cardinalities, seed: _seed)`
   - **Important:** use `ISampler` interface (`Propose()` returns `Dictionary<string,int>`, `Update(dict, float)` takes indices). This keeps the sampler in pure index space and avoids `ToCategoricalDictionary()` dropping double values.

5. Determine `evalSet`:
   - `evalSet = ctx.DevSet.Count > 0 ? ctx.DevSet : ctx.TrainSet`
   - If `evalSet.Count == 0` → return (nothing to evaluate against).

6. `incumbentScore = await ScoreOnSetAsync(ctx.Target, evalSet, ctx.Metric, ct)`

7. `samplePool = ctx.TrainSet.Count > 0 ? ctx.TrainSet : evalSet`
   `sampleSet = RandomSubsample(samplePool, max: 16, seed: _seed)` — if `samplePool.Count <= 16`, use all.

8. Loop `_numRefinements` iterations:
   a. `catConfig = ((ISampler)strategy).Propose()` → `Dictionary<string,int>` of indices
   b. `decodedAssignment = discretizer.Decode(catConfig)`
   c. `candidate = ctx.Target.WithParameters(decodedAssignment)`
   d. `(score, cost) = await ScoreOnSetAsync(candidate, sampleSet, ctx.Metric, ct)`
   e. `((ISampler)strategy).Update(catConfig, score)` — feed indices back (NOT decoded values)
   f. `ctx.TrialHistory.Add(new Trial(score, cost, "BayesianCalibration"))`
   g. Track `(bestScore, bestCatConfig)` — record the best categorical config seen

9. **Confirmation** (incumbent protection):
   - If `bestScore > incumbentScore` AND `bestCatConfig != null`:
     a. `bestAssignment = discretizer.Decode(bestCatConfig)`
     b. `confirmedTarget = ctx.Target.WithParameters(bestAssignment)`
     c. `(confirmedScore, confirmCost) = await ScoreOnSetAsync(confirmedTarget, evalSet, ctx.Metric, ct)`
     d. `ctx.TrialHistory.Add(new Trial(confirmedScore, confirmCost, "BayesianCalibration:confirmation"))`
     e. If `confirmedScore > incumbentScore`:
        `ctx.Target.ApplyState(ctx.Target.WithParameters(bestAssignment).GetState())`

**Private `ScoreOnSetAsync` helper:**
```csharp
private static async Task<(float Score, TrialCost Cost)> ScoreOnSetAsync(
    IOptimizationTarget target,
    IReadOnlyList<Example> examples,
    Func<Example, object, float> metric,
    CancellationToken ct)
{
    // For each example: (output, _) = await target.ExecuteAsync(example.WithInputs(), ct)
    // Score = metric(example, output)
    // Accumulate TrialCost from each Trace (use TrialCost.Zero and + operator if available,
    // or just sum InputTokens, OutputTokens, ApiCalls manually)
    // Return (average score, total cost)
}
```

Check what `TrialCost` looks like in `src/LMP.Abstractions/TrialCost.cs` before implementing.

**`CategoricalTpeSampler` ISampler casting:** `CategoricalTpeSampler` implements both `ISampler` and `ISearchStrategy`. Cast to `ISampler` explicitly: `var sampler = (ISampler)strategy`. Call `sampler.Propose()` and `sampler.Update(catConfig, score)`.

**XML doc comments** on class and constructor.

**Test file to create:** `test/LMP.Optimizers.Tests/BayesianCalibrationTests.cs`

Tests (~20):
- Returns immediately (no trials added) when `target.GetParameterSpace()` is empty (use `ModuleTarget` via `new FakeModule().AsTarget()` or mock `IOptimizationTarget`)
- Returns immediately when calibration space has only StringValued+Subset (no numeric/categorical)
- Returns immediately when evalSet is empty
- Adds exactly `numRefinements` trials to `ctx.TrialHistory` (with Notes = "BayesianCalibration") when search runs
- Adds confirmation trial with Notes = "BayesianCalibration:confirmation" when a better config is found
- Does NOT apply state when all candidates score lower than incumbent
- Applies state when best candidate beats incumbent on screening AND confirms on full evalSet
- Does NOT apply state when best beats on screening but fails confirmation on full evalSet
- Uses DevSet for evaluation when provided
- Uses TrainSet for evaluation when DevSet is empty
- samplePool falls back to evalSet when TrainSet is empty
- Temperature Continuous param: decoded assignment has double value in [0.0, 2.0]
- Works end-to-end with a mock `IOptimizationTarget` that has Continuous parameter space

Use `Moq` or create minimal `FakeOptimizationTarget : IOptimizationTarget` implementations. Score the target with a simple deterministic metric (e.g., score = 1.0f for specific temperature range, 0.0f otherwise).

**Commit message:** `feat(optimizers): add BayesianCalibration optimizer`

---

### Task J.3: UseLmpTrace + AutoSampler doc + LmpPipelines update ✅

**Three small changes in one commit:**

**1. `ChatClientBuilder.UseLmpTrace(Trace trace)` — add to `src/LMP.Core/ChatClientOptimizationExtensions.cs`**

`LmpTraceMiddleware` is `internal` to `LMP.Core`. `ChatClientOptimizationExtensions` is also in `LMP.Core`. Same assembly — access is valid.

```csharp
/// <summary>
/// Adds trace-recording middleware that captures per-call token usage and messages
/// into <paramref name="trace"/> for every <c>GetResponseAsync</c> call on the built client.
/// Composes naturally with other M.E.AI middleware (function invocation, logging, retry).
/// </summary>
/// <param name="builder">The chat client builder to augment.</param>
/// <param name="trace">The trace container to append records to. Must not be null.</param>
/// <returns>The updated builder for chaining.</returns>
/// <exception cref="ArgumentNullException">
/// Thrown when <paramref name="builder"/> or <paramref name="trace"/> is null.
/// </exception>
public static ChatClientBuilder UseLmpTrace(
    this ChatClientBuilder builder,
    Trace trace)
{
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(trace);
    return builder.Use(inner => new LmpTraceMiddleware(inner, trace));
}
```

**2. Update `AutoSampler.For()` doc comment** in `src/LMP.Optimizers/AutoSampler.cs`:

Replace the existing paragraph:
> "Phase D will add SmacSampler selection when the space contains Continuous parameters, once real token-cost tracking is available via ChatClientBuilder.UseLmpTrace(ctx)."

With:
> "Callers that need to optimize Continuous or Integer parameters (e.g., temperature calibration) should use BayesianCalibration directly, which manages its own ContinuousDiscretizer internally. AutoSampler intentionally stays in the categorical domain to serve ctx.SearchSpace (populated by BFS/GEPA with Categorical+StringValued params)."

No behavior change — doc comment only.

**3. Update `LmpPipelines.Auto` in `src/LMP.Optimizers/LmpPipelines.cs`:**

Add `.Use(new BayesianCalibration())` as the final step in `Goal.Accuracy` and `Goal.Balanced` pipelines:

```csharp
Goal.Accuracy => module.AsOptimizationPipeline()
    .Use(new BootstrapFewShot())
    .Use(new GEPA(client))
    .Use(new MIPROv2(client))
    .Use(new BayesianCalibration()),    // ← ADD: safe no-op for ModuleTarget

Goal.Balanced => module.AsOptimizationPipeline()
    .Use(new BootstrapFewShot())
    .Use(new GEPA(client))
    .Use(new BayesianCalibration()),    // ← ADD: safe no-op for ModuleTarget
```

Update doc comment pipeline descriptions to match.

**Tests to add:**

Add to `test/LMP.Core.Tests/ChatClientOptimizationExtensionsTests.cs`:
- `UseLmpTrace(trace)` returns a builder (not null)
- `UseLmpTrace(null, trace)` throws `ArgumentNullException`
- `UseLmpTrace(builder, null)` throws `ArgumentNullException`

Add to `test/LMP.Optimizers.Tests/LmpPipelinesTests.cs`:
- `Auto(Accuracy)` pipeline has 4 steps (BFS → GEPA → MIPROv2 → BayesianCalibration)
- `Auto(Balanced)` pipeline has 3 steps (BFS → GEPA → BayesianCalibration)

**Commit message:** `feat: UseLmpTrace middleware, AutoSampler doc update, BayesianCalibration in Auto pipelines`

---

## Phase K — Source-gen [Tool] + Diagnostics

> **Baseline state entering Phase K:** Phase J complete. Tests pass. Key facts:
> - `ToolAttribute` already exists in `src/LMP.Core/ToolAttribute.cs` (or `src/LMP.Modules/ToolAttribute.cs` — verify with `grep -r "class ToolAttribute"`)
> - `ToolPoolExtensions` (AddToolPool/WithToolPool) exists
> - `Z3FeasibilityOptimizer` prunes tool pools via Z3 SAT checks
> - Source-gen project: `src/LMP.SourceGen/LmpSourceGenerator.cs` — `IIncrementalGenerator` with multiple pipelines (Pipeline 1: LmpSignature types, Pipeline 2: Predictor fields, Pipeline 3: [AutoOptimize], Pipeline 4-6: [Skill])
> - `ILmpModule` source-gen pipeline currently processes `[Skill]` attribute (Pipeline 8) for `GetSkills()` override

### Task K.1: Source-gen Pipeline 7 — [Tool] registration ✅

**Goal:** When a method on an `LmpModule` subclass is decorated with `[Tool]`, the source generator should emit code that calls `AddToolPool(...)` on the module to register the method as an `AIFunction` in the module's parameter space.

**Implementation approach:**

1. Read `src/LMP.SourceGen/LmpSourceGenerator.cs` to understand the existing pipeline structure. Look at Pipeline 8 (`[Skill]`) for the pattern.

2. Add **Pipeline 7** (new `RegisterSource` call in `Initialize`):
   - Syntax predicate: method declaration node within a class that inherits from `LmpModule`
   - Semantic filter: method has `[Tool]` attribute
   - For each such method: emit a `partial void RegisterTools()` call in the generated `GetPredictors()` or emit a `partial class` extension that calls `AddToolPool(AIFunctionFactory.Create(this.MethodName, this, ...))`

3. **Emitted code structure** — similar to `[Skill]` → `GetSkills()` pattern. For `[Tool]`:
   - Emit `partial void RegisterTools()` override on the module class
   - In the generated implementation: call `this.AddToolPool(AIFunctionFactory.Create(this.MethodName))`
   - Or: emit a `protected override IReadOnlyList<AITool> GetTools() => [...]` if that method pattern is cleaner

4. Check if `AddToolPool` / `WithToolPool` in `ToolPoolExtensions` is the right target. Read `src/LMP.Core/ToolPoolExtensions.cs` to confirm the API.

5. If `GetPredictors()` is the right place to inject tool registration, look at how `[Skill]` does it via `GetSkills()` and replicate for tools.

**Test file:** Add tests to `test/LMP.SourceGen.Tests/` — verify that a class with `[Tool]`-decorated method generates the expected `AIFunction` registration code.

**Commit message:** `feat(sourcegen): Pipeline 7 - [Tool] attribute registers AIFunction in tool pool`

---

### Task K.2: GEPA tool description evolution ✅

**Goal:** When `ctx.SearchSpace` contains a `Subset` parameter whose pool items are `AIFunction` instances, `GEPA` should automatically add `StringValued` parameters for each tool's description, allowing GEPA to evolve tool descriptions as part of instruction optimization.

**Implementation:**

1. Read `src/LMP.Optimizers/GEPA.cs` — specifically the `OptimizeAsync` method and how it populates/reads `ctx.SearchSpace`.

2. After GEPA's instruction optimization loop, add a pass:
   - Scan `ctx.SearchSpace.Parameters` for any `Subset` parameters whose pool items include `AIFunction` instances
   - For each such `AIFunction`, add a `StringValued(tool.Description)` to `ctx.SearchSpace` under the key `"{subsetParamName}.{tool.Name}.description"`
   - Run GEPA's instruction reflection loop on these description parameters (same mechanism as instruction evolution — propose new description, evaluate, update)

3. **Simpler alternative (if the above is complex):** Just add the `StringValued` params to `ctx.SearchSpace` for downstream optimizers (e.g., MIPROv2) to search over, without GEPA actively evolving them. This is still valuable as it enables MIPROv2 to find better tool descriptions.

**Test:** Add a test where `ctx.SearchSpace` has a Subset with AIFunction items; verify that after GEPA runs, `ctx.SearchSpace` contains StringValued params for each tool description.

**Commit message:** `feat(optimizers): GEPA adds StringValued params for AIFunction descriptions in tool pool`

---

### Task K.3: LMP020–LMP025 diagnostics ✅

**Goal:** Add 6 new Roslyn diagnostics for misuse of the `[Tool]` attribute.

Read `src/LMP.SourceGen/LmpSourceGenerator.cs` (or dedicated `LmpDiagnostics.cs`) to see how existing diagnostics (LMP001, LMP002, etc.) are defined and reported.

**New diagnostics:**
- `LMP020`: `[Tool]` on a non-async method. Message: "Method '{0}' decorated with [Tool] must be async (return Task or Task<T>)."
- `LMP021`: `[Tool]` on a static method. Message: "Method '{0}' decorated with [Tool] must be an instance method."
- `LMP022`: `[Tool]` return type not awaitable or void. Message: "Method '{0}' decorated with [Tool] must return Task or Task<T>."
- `LMP023`: Duplicate tool name within same module. Message: "Module '{0}' has duplicate [Tool] name '{1}'."
- `LMP024`: `[Tool]` on a method outside an `LmpModule` subclass. Message: "Method '{0}' decorated with [Tool] must be in a class that inherits from LmpModule."
- `LMP025`: `[Tool]` method has more than 10 parameters. Message: "Method '{0}' decorated with [Tool] has {1} parameters; maximum is 10 for AIFunction compatibility."

Add these in the generator's semantic analysis pass (alongside existing diagnostic emission). Add tests in `test/LMP.SourceGen.Tests/` verifying each diagnostic is reported.

**Commit message:** `feat(sourcegen): LMP020-LMP025 diagnostics for [Tool] attribute misuse`

---

## Phase L — Multi-turn Complete (Trajectory Wiring)

> **Baseline state entering Phase L:** Phases J-K complete. Key facts:
> - `Trajectory`, `Turn`, `TurnKind`, `ITrajectoryMetric`, `TrajectoryMetric` exist in `src/LMP.Core/`
> - `IOptimizationTarget.ExecuteTrajectoryAsync` default method exists (Phase F seam)
> - `OptimizationContext.TrajectoryMetric` field exists
> - `GEPA.OptimizeAsync` and `SIMBA.OptimizeAsync` use `ctx.Target.ExecuteAsync(...)` for scoring — they do NOT currently check `ctx.TrajectoryMetric`

### Task L.1: GEPA trajectory-aware optimization ❌

**Goal:** When `ctx.TrajectoryMetric != null`, GEPA should score examples using `ExecuteTrajectoryAsync` and `TrajectoryMetric.Evaluate` instead of `ExecuteAsync` and `ctx.Metric`.

**Implementation:**

1. Read `src/LMP.Optimizers/GEPA.cs` and `src/LMP.Optimizers/Internal/InstructionReflector.cs`.

2. In GEPA's scoring loop (where it calls `ExecuteAsync` on each example):
   - Add a check: `if (ctx.TrajectoryMetric != null)`
   - If true: call `await ctx.Target.ExecuteTrajectoryAsync(example.WithInputs(), ct)` → `Trajectory`
   - Score with `ctx.TrajectoryMetric.Evaluate(example, trajectory)` → float
   - If false: existing path (ExecuteAsync + ctx.Metric)

3. The reflection input for GEPA should include trajectory spans when available:
   - Pass trajectory string summary (e.g., `string.Join("\n", trajectory.Turns.Select(t => $"[{t.Kind}] {t.Content}"))`) as observation context to `InstructionReflector.ReflectAsync`
   - `InstructionReflector` already accepts `externalObservations` parameter (added in Phase H)

**Tests:** Add tests to `test/LMP.Optimizers.Tests/GEPATests.cs` (or new file):
- When `ctx.TrajectoryMetric` is null, GEPA uses `ExecuteAsync` path (existing behavior)
- When `ctx.TrajectoryMetric` is set, GEPA calls `ExecuteTrajectoryAsync`
- GEPA reflection prompt includes trajectory content when trajectory path is used

**Commit message:** `feat(optimizers): GEPA trajectory-aware scoring via ExecuteTrajectoryAsync`

---

### Task L.2: SIMBA trajectory-aware optimization ❌

**Goal:** When `ctx.TrajectoryMetric != null`, SIMBA should score mini-batches using trajectory metric.

**Implementation:**

1. Read `src/LMP.Optimizers/SIMBA.cs` — find the mini-batch scoring loop.

2. Same pattern as GEPA:
   - Check `ctx.TrajectoryMetric != null`
   - If true: use `ExecuteTrajectoryAsync` + `TrajectoryMetric.Evaluate`
   - If false: existing `ExecuteAsync` + `ctx.Metric`

3. SIMBA's reflection prompt (for `InstructionReflector`) should include trajectory spans when available.

**Tests:** Add tests to `test/LMP.Optimizers.Tests/SimbaTests.cs`:
- When `ctx.TrajectoryMetric` is null, SIMBA uses `ExecuteAsync` path
- When `ctx.TrajectoryMetric` is set, SIMBA calls `ExecuteTrajectoryAsync`

**Commit message:** `feat(optimizers): SIMBA trajectory-aware scoring via ExecuteTrajectoryAsync`

---

### Task L.3: AgentThread → Trajectory conversion ❌

**Goal:** Provide an extension method to convert `AgentThread` (from `Microsoft.Extensions.AI.Agents`) to a `Trajectory`.

**First:** Check if `Microsoft.Extensions.AI.Agents` is available in the project. Look at `Directory.Packages.props` for the package. If not present, check if `Microsoft.Extensions.AI` 10.4.1+ includes `AgentThread` in the base package. Run `dotnet build` to see if `AgentThread` is available.

**If AgentThread is available:**

Create `src/LMP.Core/AgentThreadExtensions.cs`:
```csharp
/// <summary>Extension methods for converting agent thread types to LMP optimization types.</summary>
public static class AgentThreadExtensions
{
    /// <summary>
    /// Converts an <see cref="AgentThread"/> to a <see cref="Trajectory"/> for use with
    /// LMP trajectory-aware optimizers such as GEPA and SIMBA.
    /// </summary>
    public static Trajectory ToTrajectory(this AgentThread thread);
}
```

Map `AgentThread` messages to `Turn` instances:
- User messages → `TurnKind.UserToAgent`
- Assistant messages → `TurnKind.AgentToUser`
- Tool call messages → `TurnKind.ToolCall`
- Tool result messages → `TurnKind.ToolResult`
- Agent-to-agent messages → `TurnKind.AgentToAgent`

**If AgentThread is NOT available:** Create the extension method with a `// TODO: requires Microsoft.Extensions.AI.Agents package` comment, and mark the task complete with a note in IMPLEMENTATION_PLAN.md.

**Tests:** Add tests in `test/LMP.Core.Tests/AgentThreadExtensionsTests.cs` (or skip with a TODO if AgentThread is unavailable).

**Commit message:** `feat(core): AgentThread.ToTrajectory() conversion extension`

---

## Phase M — Documentation + Acceptance Criteria

> **Baseline state entering Phase M:** All code phases complete. All tests pass.

### Task M.1: Update architecture documentation ❌

**Goal:** Bring the architecture docs up to date with the full unified optimization pipeline (Phases A–J).

**Files to update:**

1. `docs/01-architecture/optimization-pipeline.md` — create or overwrite with:
   - Four-tier architecture diagram (Tier 1 Primitives → Tier 2 Pipeline → Tier 3 Adapters → Tier 4 Façade)
   - The Invariant: Auto() is reproducible from Tier 2 .Use() calls
   - Two extensibility seams: IOptimizer (horizontal) + IOptimizationTarget (vertical)
   - Algorithm table: BootstrapFewShot, GEPA, SIMBA, MIPROv2, BayesianCalibration, Z3Feasibility, EvaluationCritique, MultiFidelity, ContextualBandit, ModelSelector
   - Five optimization axes: Instructions, Tools, Skills, Model, Multi-turn
   - Research anchors: DSPy 2023, GEPA 2025, SIMBA 2025, MIPROv2 2024, FrugalGPT 2023, Hyperband 2018

2. `docs/02-specs/optimization-api.md` — create or overwrite with:
   - Full public API surface: `IOptimizer`, `IOptimizationTarget`, `OptimizationPipeline`, `OptimizationContext`, `TypedParameterSpace`, `ParameterKind` hierarchy, `ISearchStrategy`, `MetricVector`, `Goal`, `CostBudget`, `BayesianCalibration`
   - Three entry points (Novice, Practitioner, Researcher)
   - Goal → algorithm sequence mapping table

3. `docs/01-architecture/phased-plan.md` — add Phase A–J completion summary.

**Note:** The source code in `src/` is the ground truth. Docs should describe what the code actually does.

**Commit message:** `docs: update architecture documentation for unified optimization pipeline (Phases A-J)`

---

### Task M.2: Invariant + axis acceptance tests ❌

**Goal:** Write tests verifying the core acceptance criteria from the plan. Use mock/fake targets and clients (no real LLM calls needed).

**File to create:** `test/LMP.Optimizers.Tests/AcceptanceCriteriaTests.cs`

Tests to write:

1. **Invariant test**: `LmpPipelines.Auto(module, client, Goal.Accuracy)` produces a pipeline with the same step count and step types as manually constructed `module.AsOptimizationPipeline().Use(new BootstrapFewShot()).Use(new GEPA(client)).Use(new MIPROv2(client)).Use(new BayesianCalibration())`. Access pipeline steps via `OptimizationPipeline.Steps` property (check if it exists; if not, verify via trial count during a dry run).

2. **Novice test**: `LmpPipelines.Auto(module, client)` with no special setup produces a non-null `OptimizationPipeline` that can be invoked with `OptimizeAsync(trainSet: [], devSet: [], metric: (e,o) => 1f, ct)` without throwing (safe no-op on empty train set).

3. **Practitioner test**: Swapping `MIPROv2` → `SIMBA` in a pipeline requires no changes to other steps. Create: `module.AsOptimizationPipeline().Use(new BootstrapFewShot()).Use(new SIMBA(client))` — verify it builds and can optimize.

4. **Researcher test**: A custom 10-line `IOptimizer` implementation (`class CountingOptimizer : IOptimizer { int callCount; Task OptimizeAsync(ctx, ct) { callCount++; return Task.CompletedTask; } }`) can be inserted into any pipeline and is called during `OptimizeAsync`.

5. **BayesianCalibration no-op test**: `BayesianCalibration` in a `ModuleTarget` pipeline leaves `ctx.TrialHistory.Count == 0` (or minimal) since ModuleTarget has empty parameter space.

6. **UseLmpTrace integration test**: Create a `ChatClientTarget` wrapped with `UseLmpTrace(trace)` middleware; after calling `ExecuteAsync`, verify `trace.Entries.Count > 0`.

**Commit message:** `test: acceptance criteria tests for invariant, axis, and UseLmpTrace`

---

### Task M.3: Final — mark ALL TASKS COMPLETE ❌

**Goal:** Verify all phases are complete, run the full test suite, and mark completion.

Steps:
1. Run `dotnet build --no-restore` — must pass with 0 errors, 0 warnings.
2. Run `dotnet test` — must pass with 0 failures.
3. Update the **Status line** (line 3) of `IMPLEMENTATION_PLAN.md` to read:
   ```
   > **Status:** ALL TASKS COMPLETE — Phases A–M done. Tests passing.
   ```
   (The ralph loop checks the first line for the completion sentinel, but since the status is on line 3, also add `ALL TASKS COMPLETE` as the very first line temporarily — or just update the status line and the next ralph iteration will see 0 ❌ tasks.)
4. Commit.

**Commit message:** `chore: mark ALL TASKS COMPLETE - Phases A-M implementation done`

