# Phased Implementation Plan

> **Derived from:** Spec Section 16 (Phased Implementation Plan), with acceptance criteria from Section 17.
>
> **Audience:** Developers implementing the LMP framework, phase by phase.

---

## Phase Dependency Graph

```
Phase 0: Repository Skeleton
    │
    ▼
Phase 1: Core Abstractions
    │
    ├──────────────────┐
    ▼                  ▼
Phase 2: Minimal    Phase 3: Source Generator
Runtime               for Signatures
    │                  │
    │    ┌─────────────┘
    │    ▼
    │  Phase 4: Program Metadata
    │  Generation + Analyzers
    │    │
    ▼    ▼
Phase 5: Multi-Step Runtime ◄── requires Phase 2 + Phase 4
    │
    ▼
Phase 6: Evaluation Integration
    │
    ▼
Phase 7: Compiler MVP
    │
    ▼
Phase 8: Artifact Save / Load
    │
    ▼
Phase 9: CLI + Demo Polish
```

> **Junior Dev Note:** Phases 2 and 3 can proceed in parallel because they don't depend on each other. Phase 2 needs runtime execution; Phase 3 needs source generation. Different people can work on them simultaneously.

---

## "Start Here" Guidance

**Day 1, Hour 1:** Start with Phase 0. Create the solution, all projects, and verify `dotnet build` passes with zero errors. This takes 30–60 minutes and gives everyone a shared foundation.

**Day 1, Hour 2–4:** Move to Phase 1. Define the core IR records (`SignatureDescriptor`, `FieldDescriptor`, `ProgramDescriptor`, `StepDescriptor`), attributes (`[LmpSignature]`, `[Input]`, `[Output]`), and the base `LmpProgram<TIn, TOut>` class. Write unit tests for record equality and `with` expressions.

**Day 2:** Split into two tracks. Track A starts Phase 2 (get a single Predict step executing against `FakeChatClient`). Track B starts Phase 3 (get the source generator discovering `[LmpSignature]` types and emitting `.g.cs` files).

---

## Phase 0: Repository Skeleton

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| Solution file (`LMP.sln`) | Simple | Contains all projects |
| All 7 src projects (empty) | Simple | `.csproj` files with correct TFMs and dependencies |
| All 5 test projects (empty) | Simple | xUnit projects with correct project references |
| `Directory.Build.props` | Simple | Shared build properties, nullable enabled, warnings as errors |
| `Directory.Packages.props` | Simple | Central package version management |
| `global.json` | Simple | Pin .NET SDK version |
| `.editorconfig` | Simple | Code style enforcement |
| `/data` directory with sample JSONL | Simple | 5–10 sample training/validation records |
| Sample project placeholder | Simple | `LMP.Samples.SupportTriage` with `Console.WriteLine("Hello LMP")` |

### Acceptance Criteria

**Done when:**

- `dotnet restore LMP.sln` succeeds with zero warnings
- `dotnet build LMP.sln` succeeds with zero errors
- `dotnet test LMP.sln` runs (even if zero tests exist yet)
- `dotnet run --project src/LMP.Samples.SupportTriage` prints "Hello LMP"
- Every project has the correct `<ProjectReference>` entries matching the dependency rules in the repo-layout doc

### Dependencies

None — this is the starting point.

### Recommended Order

1. Create `global.json` and `Directory.Build.props`
2. Create `LMP.Abstractions.csproj` (leaf dependency)
3. Create remaining projects in topological order
4. Create test projects
5. Add `LMP.sln` and verify full build

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Wrong TFM for Roslyn project (must be `netstandard2.0`) | High | Document this explicitly; add a build-time check |
| Circular dependency introduced accidentally | Medium | Verify build order by building each project individually |

---

## Phase 1: Core Abstractions

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `LmpSignatureAttribute` | Simple | `[LmpSignature(Instructions = "...")]` |
| `InputAttribute`, `OutputAttribute` | Simple | Field direction markers with `Description` property |
| `LmpProgramAttribute` | Simple | `[LmpProgram("program-name")]` |
| `LmpProgram<TIn, TOut>` base class | Medium | Abstract `Build()` method, virtual `RunAsync()` |
| `ProgramGraph` + `Step` static factory | Medium | Fluent graph builder with `StartWith().Then().Return()` |
| All IR descriptor records (Section 8) | Medium | `SignatureDescriptor`, `FieldDescriptor`, `ProgramDescriptor`, `StepDescriptor`, `EdgeDescriptor`, `TunableParameterDescriptor`, `EvaluationAttachmentDescriptor`, `VariantDescriptor`, `TrialResultDescriptor`, `CompileReportDescriptor`, `RuntimeTraceDescriptor` |
| `ConstraintDescriptor` and enums | Simple | `ConstraintOperator`, `ConstraintSeverity`, `ConstraintScope` |
| `IDocumentRetriever` interface | Simple | Retrieval abstraction |
| Unit tests for all records | Simple | Equality, `with` expressions, immutability |

### Acceptance Criteria

**Done when:**

- The canonical `TriageTicket` signature compiles using `[LmpSignature]`, `[Input]`, `[Output]`
- The canonical `SupportTriageProgram` compiles using `LmpProgram<TIn, TOut>` and `ProgramGraph`
- All IR descriptor records are instantiable and pass structural equality tests
- `ConstraintDescriptor` can represent `policy_pass_rate == 1.0`

### Dependencies

Phase 0 (skeleton must exist).

### Recommended Order

1. Attributes first (`LmpSignatureAttribute`, `InputAttribute`, `OutputAttribute`, `LmpProgramAttribute`)
2. IR records next (start with `FieldDescriptor`, then `SignatureDescriptor`, then build up)
3. `ProgramGraph` + `Step` factory
4. `LmpProgram<TIn, TOut>` base class
5. Tests throughout

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Over-engineering the graph builder | High | Keep it fluent but simple. `StartWith().Then().Return()` is sufficient for MVP. Don't add LINQ-like operators yet. |
| IR record explosion | Medium | Only implement the 12 records from spec Section 8. Resist adding "just in case" records. |

---

## Phase 2: Minimal Runtime

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `ProgramRunner` | Medium | Compiles IR to TPL Dataflow blocks (`TransformBlock`, `ActionBlock`, `BroadcastBlock`, `JoinBlock`) for step execution |
| `StepExecutor` for Predict | Complex | Prompt assembly, `IChatClient` call, structured output parsing |
| `ExecutionContext` | Medium | Stores step outputs, scores, trace data |
| Structured output parser | Medium | JSON → typed signature output |
| `RuntimeTraceCollector` | Simple | Accumulates `RuntimeTraceStepDescriptor` records |
| Integration test with `FakeChatClient` | Medium | One Predict step, one assert on output |

### Acceptance Criteria

**Done when:**

- A single-step program (one Predict step with `TriageTicket` signature) runs against `FakeChatClient`
- The output is correctly parsed into the typed `TriageTicket` object
- A runtime trace is produced with step timing and model metadata
- The test passes deterministically in CI

### Dependencies

Phase 1 (needs IR records, attributes, `ProgramGraph`).

**Key NuGet Dependencies:** `System.Threading.Tasks.Dataflow` (TPL Dataflow for graph execution), `Microsoft.Extensions.Resilience` (composable retry + circuit breaker + timeout for LM calls).

### Recommended Order

1. `ExecutionContext` (data container)
2. Structured output parser (JSON → record)
3. `StepExecutor` for Predict (prompt assembly → `IChatClient` → parse)
4. `ProgramRunner` (single-step case only)
5. `RuntimeTraceCollector`
6. Integration test

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Prompt assembly is underspecified | High | Start with a simple template: system instruction + field descriptions + user input as JSON. Don't optimize the prompt format yet. |
| Structured output parsing fails on edge cases | High | Use `System.Text.Json` with lenient options. Add fallback parsing for boolean strings ("True"/"true"/"TRUE"). |
| `IChatClient` API surface confusion | Medium | Reference the actual `Microsoft.Extensions.AI` docs. Use `ChatClientBuilder` pattern from the spec. |

---

## Phase 3: Source Generator for Signatures

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `LmpSignatureGenerator` : `IIncrementalGenerator` | Complex | Discovers `[LmpSignature]` types, emits `<Name>.g.cs` |
| Signature descriptor code emission | Medium | Generates `SignatureDescriptor` instantiation as C# source |
| `file class` scoping | Simple | Generated types use `file class` to stay out of public API |
| Golden/snapshot tests | Medium | Verify deterministic output with Verify library |
| Diagnostic for generator failures | Simple | Report when a signature can't be lowered |

### Acceptance Criteria

**Done when:**

- Building `LMP.Samples.SupportTriage` produces `TriageTicket.g.cs` in `obj/Generated/`
- The generated descriptor matches the snapshot test exactly
- Rebuilding produces byte-identical output (determinism)
- Generator errors produce a diagnostic, not a crash

### Dependencies

Phase 1 (needs attribute definitions and IR records).

**Does NOT depend on Phase 2** — generators work at build time, runtime is irrelevant.

### Recommended Order

1. Minimal `IIncrementalGenerator` that discovers `[LmpSignature]` types
2. Extract field metadata from the semantic model
3. Emit `SignatureDescriptor` as generated C# source
4. Implement Tier 1 (convention-based) and Tier 2 (`[BindFrom]`) binding descriptor emission
5. Implement C# 14 interceptor-based Tier 3 lambda binding (interceptors are stable in C# 14)
6. Add snapshot test
7. Add diagnostic for unsupported types

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Generator debugging is painful | High | Add a `#if LMP_GENERATOR_DEBUG` dump mode that writes intermediate state to a file. Use `Debugger.Launch()` for attach-and-step debugging. |
| `netstandard2.0` constraint limits available APIs | High | No `Span<T>`, no `ImmutableArray` builders, limited LINQ. Use `StringBuilder` and arrays. Test thoroughly. Note: C# 14 implicit Span conversions do not apply to `netstandard2.0` generators. |
| Non-deterministic output ordering | Medium | Sort fields alphabetically before emitting. Use `INamedTypeSymbol.GetMembers()` which returns declaration order (stable). |
| Interceptor API surface is new | Medium | C# 14 interceptors are stable but the team should reference the latest Roslyn interceptor docs. Start with a spike to validate the lambda-to-binding rewrite before committing to the full Tier 3 implementation. |

---

## Phase 4: Program Metadata Generation + Analyzers

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `LmpProgramGenerator` | Complex | Discovers `[LmpProgram]` types, emits `<Name>.g.cs` with `ProgramDescriptor` |
| Search-space descriptor generation | Medium | Extract tunable parameters from `Build()` method |
| LMP001: Missing field description | Simple | Analyzer + test |
| LMP002: Missing/empty instructions | Simple | Analyzer + test |
| LMP003: Duplicate step name | Medium | Analyzer + test |
| LMP004: Non-deterministic step name | Medium | Analyzer that checks step name is a constant |
| LMP005: Unsupported output type | Simple | Analyzer + test |
| LMP006: Cyclic graph | Complex | Analyzer for self-referencing steps |
| LMP007: Missing [BindFrom] on ambiguous input | Medium | Analyzer + test |
| Code fix: add missing description | Simple | Lightbulb fix for LMP001 |

### Acceptance Criteria

**Done when:**

- `SupportTriageProgram.g.cs` is generated with a valid `ProgramDescriptor`
- All 7 diagnostics (LMP001–LMP007) fire correctly on invalid input
- At least one code fix (LMP001) works in IDE
- Analyzer tests use `Microsoft.CodeAnalysis.Testing`

### Dependencies

Phase 1 (IR records), Phase 3 (signature generator pattern to reuse).

### Recommended Order

1. LMP001 + LMP002 analyzers (simplest, build confidence)
2. `LmpProgramGenerator` (reuse patterns from Phase 3)
3. LMP003–LMP007 analyzers
4. Code fix for LMP001
5. Snapshot tests for program generator

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Analyzing `Build()` method body is hard | High | For MVP, the program generator can use attribute metadata and step registration rather than parsing method bodies. Full `Build()` analysis can wait. |
| Analyzer false positives | Medium | Every analyzer must have both positive and negative test cases. |

---

## Phase 5: Multi-Step Runtime

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| Retrieve step executor | Medium | Calls `IDocumentRetriever`, stores docs in context |
| Evaluate step executor | Medium | Runs evaluator, stores scores |
| If step executor | Simple | Checks condition, skips or executes branch |
| Repair step executor | Complex | Re-runs Predict with feedback context |
| Context addressing (`ctx.OutputOf()`, `ctx.ScoreOf()`, `ctx.Latest<T>()`) | Medium | Type-safe step output access |
| Multi-step graph execution | Medium | Topological sort, sequential execution |
| Full triage program integration test | Complex | All 6 steps: retrieve-kb, retrieve-policy, triage, groundedness, policy, repair |

### Acceptance Criteria

**Done when:**

- The canonical `SupportTriageProgram` runs end-to-end with `FakeChatClient` and `FakeDocumentRetriever`
- Context addressing works (step B reads step A's output)
- Evaluate steps produce scores visible in the trace
- If step conditionally triggers Repair
- Full trace contains entries for all executed steps

### Dependencies

Phase 2 (Predict step works), Phase 4 (program descriptors generated).

### Recommended Order

1. Retrieve step executor
2. Context addressing extensions
3. Evaluate step executor
4. If step executor
5. Repair step executor (most complex — depends on evaluate + predict)
6. Multi-step integration test
7. Full triage program test

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Repair step is underspecified | High | Start with a simple implementation: re-run the same Predict step with evaluation feedback appended to the prompt. Don't build a sophisticated retry/backtrack system yet. |
| Context addressing type safety | Medium | Use generic methods with step identity tokens. `ctx.OutputOf(retrieveKb)` returns the concrete type. |

---

## Phase 6: Evaluation Integration

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| JSONL dataset loader | Simple | Parse `support-triage-train.jsonl` into typed records |
| `EvaluationRunner` | Medium | Run program on each example, collect scores |
| Score aggregation | Simple | Weighted average, per-metric summaries |
| `CustomPolicyEvaluator` | Medium | Domain-specific policy compliance checker |
| Integration with `Microsoft.Extensions.AI.Evaluation` | Medium | Use `GroundednessEvaluator` from the library |

### Acceptance Criteria

**Done when:**

- The sample program can be evaluated on a 10-example JSONL dataset
- Evaluation produces per-metric scores and a weighted aggregate
- Evaluation results match expected values when using `FakeChatClient`

### Dependencies

Phase 5 (multi-step runtime must work end-to-end).

### Recommended Order

1. JSONL dataset loader
2. `EvaluationRunner` (loop over examples, run program, collect outputs)
3. Simple metric calculators (exact match for severity, routing)
4. Score aggregation
5. `CustomPolicyEvaluator`
6. Wire in `GroundednessEvaluator` from M.E.AI.Evaluation

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| `Microsoft.Extensions.AI.Evaluation` API surface may change | Medium | Abstract behind an `IEvaluator` interface in `LMP.Abstractions`. Swap implementations without changing framework code. |
| JSONL parsing edge cases | Low | Use `System.Text.Json` with strict mode. Fail fast on malformed lines. |

---

## Phase 7: Compiler MVP

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `CompileSpec` fluent builder | Medium | `.Optimize()`, `.ScoreWith()`, `.Constrain()`, `.UseOptimizer()` |
| Search-space extraction | Medium | Build from generated descriptors |
| `RandomSearchBackend` : `IOptimizerBackend` | Simple | Random candidate proposal within bounds |
| Trial execution loop | Complex | Apply candidate → run on training set → evaluate → check constraints |
| Constraint checker | Medium | Hard constraint enforcement, rejection tracking |
| Selection logic | Medium | Reject invalid → rank by score → break ties deterministically |
| `CompileReport` generation | Simple | Trial count, valid/rejected counts, best variant |
| Weighted objective scoring | Simple | `Metrics.Weighted(...)` aggregation via `TensorPrimitives` SIMD-accelerated ops (cosine similarity, sum, average) |

### Acceptance Criteria

**Done when:**

- The compiler can run 10+ trials on the triage program
- At least one constraint-violating candidate is correctly rejected
- The best valid variant is selected by objective score
- A `CompileReport` is produced with trial counts and rejection reasons
- The compiler searches over ≥3 tunable dimensions

### Dependencies

Phase 6 (evaluation must work for scoring trials).

### Recommended Order

1. `CompileSpec` builder
2. Search-space extraction from descriptors
3. `RandomSearchBackend`
4. Trial execution loop (the core)
5. Constraint checker
6. Selection logic
7. `CompileReport` generation
8. Integration test: full compile with mock client

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Trial execution is slow even with mocks | Medium | Use `CancellationToken` + timeout. Add a `maxTrials` budget. Log trial progress. |
| Constraint model is too simple for real use | Low | MVP constraints are lambda predicates (`Func<TrialResultDescriptor, bool>`). Z3-assisted constraint solving is an optional advanced backend post-MVP. Don't over-engineer. |

---

## Phase 8: Artifact Save / Load

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `CompiledArtifact` record | Simple | Program ID, variant ID, selected parameters, metrics, hash, approval state |
| `ArtifactSerializer` | Medium | AOT-safe JSON serialization with `JsonSerializerContext` |
| `ArtifactLoader` : `ICompiledArtifactLoader` | Medium | Load from file, validate compatibility |
| Runtime artifact application | Medium | `program.ApplyArtifact(artifact)` applies selected parameters |
| Provenance hash (`XxHash128`) | Simple | Hash of base program for compatibility checking |
| Artifact snapshot tests | Simple | Verify JSON schema stability |

### Acceptance Criteria

**Done when:**

- A compiled artifact can be saved to disk as JSON
- The artifact can be loaded back and deserialized without data loss
- The runtime can execute a program using artifact-pinned parameters
- Provenance hash is verified on load (incompatible artifact → clear error)
- Artifact JSON matches the canonical shape from spec Section 6

### Dependencies

Phase 7 (compiler must produce a winning variant to serialize).

### Recommended Order

1. `CompiledArtifact` record
2. `ArtifactSerializer` with `JsonSerializerContext`
3. `ArtifactLoader`
4. Provenance hash generation and verification
5. `program.ApplyArtifact()` in runtime
6. Round-trip test
7. Snapshot test for JSON schema

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Polymorphic serialization is tricky | Medium | Use `[JsonDerivedType]` discriminators. Test every step kind round-trips correctly. |
| AOT compatibility issues with `System.Text.Json` | Low | Use source-generated `JsonSerializerContext`. Avoid reflection-based serialization. |

---

## Phase 9: CLI + Demo Polish

### Deliverables

| Deliverable | Complexity | Description |
|-------------|-----------|-------------|
| `dotnet lmp compile` command | Medium | Calls `IProgramCompiler`, writes artifact |
| `dotnet lmp run` command | Medium | Loads program, optionally applies artifact, runs input |
| `dotnet lmp eval` command | Medium | Loads dataset, runs evaluation, prints report |
| `dotnet lmp inspect-artifact` command | Simple | Pretty-prints artifact JSON |
| Sample data files | Simple | 10–20 training examples, 10 validation examples |
| Demo script validation | Simple | Run through the entire demo script end-to-end |
| README update | Simple | Getting started, quick demo instructions |

### Acceptance Criteria

**Done when:**

- All 4 CLI commands work from the terminal
- The complete demo script (steps 1–7) executes without errors
- Mock mode works without any API key
- The demo can be presented in under 15 minutes

### Dependencies

Phase 8 (all framework features must work).

### Recommended Order

1. `System.CommandLine` setup in `LMP.Cli`
2. `compile` command (most complex, most impactful)
3. `run` command
4. `eval` command
5. `inspect-artifact` command
6. Sample data files
7. Full demo dry run
8. README

### Risk Register

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| CLI ergonomics are poor | Medium | Use `System.CommandLine` auto-help. Add `--help` examples for each command. Test with someone unfamiliar with the project. |
| Demo fails during presentation | Medium | Always have mock mode as a fallback. Run the full demo script at least twice before presenting. |

---

## Summary Table

| Phase | Name | Est. Complexity | Key Risk | Depends On |
|-------|------|----------------|----------|------------|
| 0 | Repository Skeleton | Simple | Wrong TFM for Roslyn | — |
| 1 | Core Abstractions | Medium | Over-engineering graph builder | 0 |
| 2 | Minimal Runtime | Medium | Prompt assembly underspecified | 1 |
| 3 | Source Generator | Complex | Generator debugging pain | 1 |
| 4 | Program Gen + Analyzers | Complex | `Build()` method analysis | 1, 3 |
| 5 | Multi-Step Runtime | Complex | Repair step underspecified | 2, 4 |
| 6 | Evaluation Integration | Medium | M.E.AI.Evaluation API flux | 5 |
| 7 | Compiler MVP | Complex | Trial execution slowness | 6 |
| 8 | Artifact Save / Load | Medium | Polymorphic serialization | 7 |
| 9 | CLI + Demo Polish | Medium | Demo reliability | 8 |

> **Junior Dev Note:** Don't skip phases. Each phase builds on the previous one. If Phase 2 has a shaky foundation (e.g., the structured output parser is fragile), Phase 5 will be painful. Take the time to write tests in each phase — they're your safety net for the next phase.

---

## Post-MVP Extensions

| Extension | Package | Description |
|-----------|---------|-------------|
| **MSBuild targets** | `LMP.Tasks` (ships inside `LMP.Runtime` NuGet) | `LmpEmitIr`, `LmpValidateGraph`, `LmpEmbedArtifact` MSBuild targets. Run during `dotnet build` (Layer 2 of Three-Layer Build Architecture). |
| **NuGet artifact packaging** | `dotnet lmp pack` (CLI command) | Package compiled artifacts as NuGet packages for distribution via Azure Artifacts, GitHub Packages, or private feeds. |
| **LMP.Sdk** | `LMP.Sdk` | Custom MSBuild SDK: `<Project Sdk="LMP.Sdk/1.0.0">`. Consolidates source generator + MSBuild targets + runtime into one-line reference. |
| **Aspire hosting** | `LMP.Aspire.Hosting` | `AddLmpCompiler()` integrates the compile loop as an Aspire resource with dashboard telemetry. |
| **Z3 constraint solving** | `LMP.Constraints.Z3` | Optional advanced backend for formal constraint verification. MVP uses lambda predicates. |
| **Convention-based discovery** | Part of `LMP.Runtime` | Auto-discover `ILmProgram<TIn, TOut>` implementations, generate DI registration. No manual `AddLmpProgram<T>()` calls. |

### Post-MVP Phasing

**Phase 10: MSBuild Integration (immediately after MVP)**
- Implement `EmitIrTask` and `ValidateGraphTask` MSBuild tasks
- Add `buildTransitive/` folder to LMP.Runtime NuGet package
- Add `LmpEmitIr`, `LmpValidateGraph`, `LmpEmbedArtifact` targets
- `dotnet build` catches structural errors without calling LM APIs
- Precedent: EF Core `OptimizeDbContext` task pattern

**Phase 11: NuGet Artifact Distribution**
- Add `dotnet lmp pack` CLI command
- Package compiled artifacts as NuGet packages with `contentFiles/` and `.targets`
- Auto-register artifact path in DI via package .targets file
- Enterprise distribution via Azure Artifacts / GitHub Packages

**Phase 12: LMP.Sdk + Convention Discovery**
- Consolidate source gen + MSBuild targets + runtime into custom MSBuild SDK
- `<Project Sdk="LMP.Sdk/1.0.0">` → zero-config, one-line setup
- Convention-based program discovery: auto-discover `ILmProgram<TIn, TOut>`, generate `AddLmpPrograms()`
- Requires stabilized package structure from Phases 10–11

**Phase 13: Aspire Hosting**
- Ship `LMP.Aspire.Hosting` package with `AddLmpCompiler()` extension
- Integrates compile loop as an Aspire resource with dashboard telemetry
- Provisions LM provider connection, evaluation dataset, OTEL dashboard
