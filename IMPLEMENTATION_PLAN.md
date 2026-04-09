# LMP Implementation Plan

> **Status:** Phase 8 complete + samples — 852 tests passing. All phases done.
> **Target:** .NET 10 / C# 14
> **Authoritative specs:** `docs/01-architecture/`, `docs/02-specs/`, `AGENTS.md`
> **Last updated:** 2026-04-09

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
| `LMP.Cli` — CLI tool (`dotnet lmp`) | `cli.md` | :white_check_mark: Phase 7.1 complete (inspect, optimize, eval commands) |
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

### 1.2 — LmpSignatureAttribute

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

### 1.3 — IPredictor Interface and Artifact Records

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

### 1.4 — Example Base Class and Example\<TInput, TLabel\>

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

### 1.5 — Trace and TraceEntry

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

### 1.6 — Demo Record

Non-generic demo type used by optimizers when filling predictor Demos.

**Spec:** `compiler-optimizer.md` §4 (Demo Type)

**Tasks:**
- [x] Create `Demo` sealed record in `LMP.Abstractions`: `(object Input, object Output)`
- [x] Unit test: construction and equality

**Completion criteria:** `new Demo(inputObj, outputObj)` works and compares by value.

### 1.7 — Predictor\<TInput, TOutput\> Class Shell

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

### 1.8 — LmpModule Base Class

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

### 1.9 — LmpAssert / LmpSuggest / Exceptions

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

### 1.10 — IRetriever and IOptimizer Interfaces

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

### 3.3 — Refine\<TInput, TOutput\>

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

### 4.2 — Module Cloning

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

### 4.4 — BootstrapRandomSearch

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

**Implementation notes:**
- Created `ILmpRunner` interface in `LMP.Abstractions` — user projects implement this to expose module factory, metric, and dataset loading to the CLI
- `LMP.Cli` project as a .NET tool (`PackAsTool`, `ToolCommandName=lmp`)
- `ProjectBuilder` — shells out to `dotnet build`, locates output DLL in standard paths
- `RunnerDiscovery` — loads assembly via custom `AssemblyLoadContext`, scans for `ILmpRunner` implementations
- Manual arg parsing (no `System.CommandLine` dependency) for 3 commands
- Exit codes per CLI spec: 0=success, 1=unknown, 2=invalid args, 3=project not found, 4=compile failed, 5=eval failed, 6=artifact error, 7=input parse error
- `--json` flag on inspect/eval for machine-readable output
- `--dev <path>` flag on optimize for post-optimization validation scoring
- `--version` flag on main entry point
- Early validation of `--optimizer` name (rejects unknown optimizers at arg parse time)
- 40 tests: argument parsing, file not found, invalid JSON, formatted/JSON output, FormatDemoFields, CLI entry point dispatch, --version, --dev, unknown optimizer

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
- ChainOfThought records `ChainOfThoughtResult<T>` in trace (not `T`), so `AddDemo` cast fails when optimizing CoT predictors — sample uses plain Predictor in module, CoT shown standalone

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
