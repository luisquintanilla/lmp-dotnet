# Build-Time Auto-Optimize Specification

> **Target:** .NET 10 / C# 14
> **Status:** Experimental (feature branch `auto-optimize`)
> **Audience:** Implementer — a developer should be able to build the auto-optimize system from this document alone.

---

## 1. Motivation

LMP modules contain learnable parameters (instructions, few-shot demos, LM config) that are tuned via optimizers like MIPROv2. Today, optimization is a **runtime** activity — the developer calls `optimizer.CompileAsync(module, trainSet, metric)` in their own code and persists results via `module.SaveAsync()`.

Build-time auto-optimize moves this into the **compilation pipeline**:

```
dotnet build -p:LmpAutoOptimize=true
→ optimizer searches over program variants
→ writes winning state as C# source
→ compiler includes it
→ optimized module baked into the binary
```

The developer writes a stable module, adds one attribute, and the build system handles the rest.

### Why build-time?

| Aspect | Runtime optimization | Build-time optimization |
|--------|---------------------|------------------------|
| When it runs | Developer invokes `CompileAsync()` explicitly | MSBuild target runs before compilation |
| Result format | JSON (`ModuleState`) loaded at runtime | C# source compiled into binary |
| Runtime cost | File I/O to load state | Zero (state is string literals in IL) |
| AOT compatibility | Requires file on disk at runtime | Fully AOT-safe (no file I/O) |
| Reproducibility | Depends on JSON file presence | Lock file (`.g.cs`) in source control |
| CI/CD | Manual script to run optimizer | Native MSBuild property |

### Precedent

This follows established .NET patterns:

- **gRPC-dotnet:** `protoc` generates `.cs` from `.proto` files via MSBuild `<Exec>` target. Generated code compiled directly by Roslyn.
- **EF Core Migrations:** `dotnet ef migrations add` generates `.cs` migration files checked into source control. `dotnet ef database update` applies them.
- **OpenAPI generators:** CLI tool generates `.cs` client code from OpenAPI specs.

All produce **C# as the artifact** — not JSON, not XML, not intermediate formats.

---

## 2. Developer Experience

### Before auto-optimize (existing workflow)

```csharp
public partial class QAModule : LmpModule
{
    private readonly Predictor<QAInput, QAOutput> _qa;

    public QAModule(IChatClient client) : base(client)
    {
        _qa = new Predictor<QAInput, QAOutput>(client);
    }
}

// Manual optimization:
var module = new QAModule(client);
var optimizer = new MIPROv2(client, numTrials: 20);
var optimized = await optimizer.CompileAsync(module, trainSet, metric);
await optimized.SaveAsync("optimized.json");

// Production:
var module = new QAModule(client);
await module.LoadAsync("optimized.json");  // runtime file I/O
```

### After auto-optimize

```csharp
[AutoOptimize(                              // ← one attribute added
    Metric = typeof(ExactMatchMetric),
    TrainSet = "Data/train.jsonl",
    BudgetSeconds = 60)]
public partial class QAModule : LmpModule   // ← everything else identical
{
    private readonly Predictor<QAInput, QAOutput> _qa;

    public QAModule(IChatClient client) : base(client)
    {
        _qa = new Predictor<QAInput, QAOutput>(client);
    }
}

// Usage: UNCHANGED
var module = new QAModule(client);
var result = await module.ForwardAsync(input);
// No LoadAsync(), no file paths, no runtime I/O
```

### Developer journey

```
1. dotnet add package LMP.Build               ← one time (adds MSBuild targets)
2. Add [AutoOptimize(...)] to module           ← one attribute
3. dotnet build -p:LmpAutoOptimize=true        ← triggers optimization
4. git add Generated/QAModule.Optimized.g.cs   ← commit the artifact
5. dotnet build                                ← normal build compiles optimized state
```

### Re-optimization

When training data or budget changes:

```
dotnet build -p:LmpAutoOptimize=true        ← re-run
git diff Generated/QAModule.Optimized.g.cs  ← review changes in readable C#
git commit                                  ← done
```

### What the developer NEVER needs to do

- Open, edit, or understand the `.g.cs` file
- Change how they instantiate or use the module
- Remove existing runtime optimization code (it still works, just becomes redundant)
- Learn new APIs beyond `[AutoOptimize]`

---

## 3. Architecture Decision: C# as the Artifact

### The key insight

The optimizer's output is **data** (instructions, demos, config), not algorithms. In .NET, data expressed as C# string literals compiles into the binary with zero runtime overhead. Therefore: **the optimizer should write C#, not JSON.**

### Comparison of approaches

| Approach | Flow | Pros | Cons |
|----------|------|------|------|
| **JSON lock file** | Optimizer → JSON → Source gen reads → Emits C# | Familiar format | Source gen File I/O race conditions; extra layer of indirection |
| **C# as artifact** ✅ | Optimizer → writes `.g.cs` → Compiler includes | No race conditions; diffable; no intermediate format; proven (gRPC) | Generated code in source tree |

### Why NOT JSON lock files

1. **Race condition risk:** Source generators run inside Roslyn. Reading files from `AdditionalTextsProvider` requires MSBuild `<AdditionalFiles>` items that are evaluated at project load time — before the MSBuild target writes the lock file. Direct File I/O in generators works but is fragile.
2. **Extra indirection:** JSON → parse → emit C# → compile. Why not skip the middle?
3. **Not diffable in the same way:** JSON diffs are harder to review than C# diffs.

### Why C# as artifact

1. **Battle-tested pattern:** gRPC has done this since .NET Core 3.1.
2. **Zero runtime overhead:** String literals and object initializers compile to IL directly.
3. **AOT-safe:** No file I/O, no reflection, no dynamic loading.
4. **Diffable:** The generated file is readable C# — reviewable in PRs, auditable over time.
5. **Deterministic:** Same `.g.cs` in source control = same compiled binary.
6. **Simple:** The optimizer is just a CLI tool that writes a `.cs` file. MSBuild includes it. Done.

---

## 4. Mechanism: Partial Void Methods

The system uses C#'s `partial void` method rules to achieve zero-impact when optimization hasn't run.

### How it works

**Source gen Pipeline 3** (ModuleEmitter) detects `[AutoOptimize]` and emits:

```csharp
// Generated by source gen (always emitted for [AutoOptimize] modules)
public partial class QAModule
{
    partial void ApplyOptimizedState();   // declaration only

    public QAModule(IChatClient client) : base(client)
    {
        _qa = new Predictor<QAInput, QAOutput>(client);
        ApplyOptimizedState();             // call site
    }
}
```

**If the optimizer has NOT run:**
- No implementation of `ApplyOptimizedState()` exists
- C# compiler removes BOTH the declaration and the call site (partial void rules)
- Module behaves identically to a non-optimized module
- **Zero impact. Zero overhead. Zero errors.**

**If the optimizer HAS run**, it wrote `Generated/QAModule.Optimized.g.cs`:

```csharp
// <auto-generated>
// LMP Auto-Optimize | Score: 0.914 | Strategy: MIPROv2
// Source: sha256:abc123 | Dataset: sha256:def456 | Metric: sha256:789012
// Generated: 2026-04-10T14:00:00Z
// </auto-generated>

namespace MyApp;

public partial class QAModule
{
    partial void ApplyOptimizedState()
    {
        _qa.Instructions = """
            Given a question about a technical topic, provide
            a concise, factual answer. Focus on accuracy.
            """;

        _qa.AddDemo(
            input: new QAInput { Question = "What is dependency injection?" },
            output: new QAOutput
            {
                Answer = "Dependency injection is a design pattern where objects receive their dependencies from external sources rather than creating them internally."
            });

        _qa.AddDemo(
            input: new QAInput { Question = "What is LINQ?" },
            output: new QAOutput
            {
                Answer = "LINQ (Language Integrated Query) is a set of C# language features for querying and transforming data from any source using a consistent syntax."
            });

        _qa.Config = new ChatOptions { Temperature = 0.7f };
    }
}
```

The compiler merges both partial class parts. The call in the constructor executes the implementation.

### C# partial void rules (C# specification)

- `partial void` methods with no implementation: declaration and all call sites are **removed** by the compiler
- `partial void` methods must return void, have no access modifier, and no out parameters
- Multiple partial class declarations can exist across any number of source files
- The compiler merges them regardless of origin (hand-written, source-generated, tool-generated)

---

## 5. Architecture Decision: Inline Model (not Delegation)

The original proposal offered two source generation models:

### Delegation model (rejected)

```csharp
// Generates a SEPARATE type per candidate
public class QAModule__Cand_F33 : IImpl { ... }

// Dispatch via static field
private static readonly IImpl _impl = new QAModule__Cand_F33();
public partial Task<string> PredictAsync(string input) => _impl.PredictAsync(input);
```

### Inline model (chosen) ✅

```csharp
// Optimized state emitted DIRECTLY into the partial class
partial void ApplyOptimizedState()
{
    _qa.Instructions = "...";
    _qa.AddDemo(...);
}
```

### Why inline

1. **What gets "compiled" is data, not algorithms.** The optimizer finds the best instructions, demos, config, and strategy. `ChainOfThought`, `Refine`, `BestOfN` are existing runtime types in `LMP.Modules` — the optimizer emits the right constructor call + data, not new algorithm code.

2. **Existing source gen is already inline.** All 5 existing pipelines emit directly into partial classes. No delegation types anywhere in LMP. Consistency matters.

3. **Delegation is over-engineered.** `Predictor<TIn,TOut>` IS the contract. `ChainOfThought<TIn,TOut> : Predictor<TIn,TOut>` — inheritance handles polymorphism. No need for a separate `IImpl` interface.

4. **Inline scales naturally across search levels:**

```csharp
// Level 1 — State injection: string literals
partial void ApplyOptimizedState()
{
    _qa.Instructions = "Given a question, provide a concise answer...";
    _qa.AddDemo(new Demo { Input = ..., Output = ... });
}

// Level 2 — Strategy selection: constructor replacement
partial void ApplyOptimizedState()
{
    _qa = new ChainOfThought<QAInput, QAOutput>(_client);
    _qa.Instructions = "Reason step by step...";
}

// Level 3 — Structural composition: nested construction
partial void ApplyOptimizedState()
{
    var inner = new ChainOfThought<QAInput, QAOutput>(_client);
    inner.Instructions = "Reason step by step...";
    _qa = new Refine<QAInput, QAOutput>(inner, _client, maxIterations: 2);
}
```

---

## 6. Search Space Levels

### Level 1: Parameters (Phase 0-1)

Optimizes **learnable state** per predictor:
- Instructions (system prompt text)
- Few-shot demonstrations (input/output pairs)
- LM config (temperature, max tokens)

This is what MIPROv2 already searches today. The auto-optimize system simply runs it at build time instead of runtime.

### Level 2: Strategy (Phase 2)

Optimizes **which strategy** each predictor uses:
- Basic `Predictor<TIn, TOut>` (single LLM call)
- `ChainOfThought<TIn, TOut>` (adds reasoning step)
- `BestOfN<TIn, TOut>` (N parallel calls, select best)
- `Refine<TIn, TOut>` (iterative refinement loop)

All strategies inherit from `Predictor<TIn, TOut>` — the external contract is preserved.

### Level 3: Structure (Phase 3, future)

Optimizes **pipeline topology** via TPOT-style mutations:
- Add verifier after predictor
- Add retriever before predictor
- Compose strategies (e.g., `Refine(ChainOfThought(...))`)

**Type safety is preserved** because all mutations produce types that satisfy the `Predictor<TIn, TOut>` contract. The user's `ForwardAsync` signature never changes.

---

## 7. Generated File Format

### Location

`Generated/{ModuleName}.Optimized.g.cs` in the project directory.

### Header comments

```csharp
// <auto-generated>
// LMP Auto-Optimize | Score: {score} | Strategy: {optimizer}
// Source: sha256:{hash} | Dataset: sha256:{hash} | Metric: sha256:{hash}
// Generated: {ISO 8601 timestamp}
// </auto-generated>
```

Hashes are used for staleness detection. The optimizer reads its own prior output, compares hashes to current state, and skips re-optimization if unchanged.

### Source control

The `.g.cs` file **should be committed**. It is the optimization lock — same file = same compiled behavior. This mirrors `packages.lock.json` or EF Core migration files.

---

## 8. Staleness Detection

The optimizer computes SHA256 hashes of:
1. **Source files** — the module's `.cs` file and its `[LmpSignature]` types
2. **Training dataset** — the file referenced by `TrainSet`
3. **Metric assembly** — the type referenced by `Metric`

If a `Generated/{Module}.Optimized.g.cs` already exists and its header hashes match the current state, optimization is **skipped**. This makes `dotnet build -p:LmpAutoOptimize=true` idempotent after the first run.

---

## 9. MSBuild Integration

### Target

```xml
<Target Name="LmpAutoOptimize"
        BeforeTargets="CoreCompile"
        Condition="'$(LmpAutoOptimize)' == 'true' and '$(DesignTimeBuild)' != 'true'">
  <Exec Command="dotnet lmp auto-optimize --project &quot;$(MSBuildProjectFullPath)&quot;" />
</Target>
```

### Key properties

| Property | Default | Description |
|----------|---------|-------------|
| `LmpAutoOptimize` | `false` | Enable build-time optimization |
| `DesignTimeBuild` | (set by IDE) | Skipped — IDE design-time builds must not run the optimizer |

### Normal builds

When `LmpAutoOptimize` is not set (default), the MSBuild target does nothing. If a `.g.cs` file exists in `Generated/`, the compiler simply includes it as part of normal compilation.

---

## 10. Backward Compatibility

### Non-automated workflow: zero changes

If users don't use `[AutoOptimize]`:
- No `partial void ApplyOptimizedState()` emitted by source gen
- No MSBuild target fires
- No generated files created
- Source gen Pipelines 1–5 run exactly as today
- `MIPROv2.CompileAsync()` still works at runtime
- `module.SaveAsync()` / `module.LoadAsync()` still work

**The auto-optimize feature is completely additive. Zero breaking changes.**

### Runtime optimization coexistence

Users can have BOTH auto-optimize and runtime optimization:
- `[AutoOptimize]` bakes the best-known state at build time
- `CompileAsync()` can further optimize at runtime (e.g., with fresh data)
- `LoadAsync()` can override the baked-in state from a file

The partial void method runs in the constructor. Any subsequent `LoadAsync()` call overwrites it.

### JSON format preserved

`ModuleState` JSON format is unchanged. `SaveAsync()` and `LoadAsync()` continue to use it. The auto-optimize system generates C# but does not modify the JSON serialization path.

---

## 11. CI/Production Guidance

### Development (inner loop)

```
dotnet build                                ← fast, reads existing .g.cs
dotnet build -p:LmpAutoOptimize=true        ← runs optimizer (slower, costs API calls)
```

Optimization should NOT run on every build. It's opt-in.

### CI/CD (recommended for production)

```yaml
# GitHub Actions example
- name: Optimize LMP modules
  run: dotnet build -p:LmpAutoOptimize=true
  env:
    AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}

- name: Commit optimization artifacts
  run: |
    git add Generated/*.g.cs
    git commit -m "chore: update LMP optimization artifacts" || true
    git push
```

### Cost awareness

Real optimization takes 2–20 minutes and costs $1–10 in API calls depending on budget and model. The staleness detection ensures re-optimization only runs when source, data, or metric actually change.

---

## 12. Implementation Phases

### Phase 0: Foundation

1. `[AutoOptimize]` attribute in `LMP.Abstractions`
2. Source gen Pipeline 3 modification: emit `partial void ApplyOptimizedState()` declaration + call
3. Spike: hand-write a `.g.cs` file, verify partial method merge works

### Phase 1: Build integration

4. CLI `auto-optimize` subcommand: discovers modules, runs optimizer, writes `.g.cs`
5. `LMP.Build` package: MSBuild `.targets` file
6. Staleness detection via SHA256 hashes
7. Diagnostics (LMP1001–LMP1006)
8. End-to-end integration test

### Phase 2: Strategy automation (future)

9. Level 2 strategy variant enumeration
10. Multi-fidelity evaluation (Successive Halving)

### Phase 3: Structural search (future research)

11. Pipeline graph representation + TPOT-style mutations
