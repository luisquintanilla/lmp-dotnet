# T1 — Optimization API redesign (pre-alpha breaking changes)

T1 collapses the optimization-target surface so that **the type you have is the
target you optimize**. There is no longer an adapter layer between
`LmpModule` / `IChatClient` and the optimizer pipeline. Pipeline composition
moved from a static factory (`ChainTarget.For`) to fluent extensions
(`.Then(...)` / `Pipeline<TIn, TOut>`), and `OptimizationContext.Bag` was
replaced by typed `Diagnostics`.

## Find / replace

| Before                                      | After                                                                    |
| ------------------------------------------- | ------------------------------------------------------------------------ |
| `ModuleTarget.For(module)`                  | `module` (LmpModule now implements IOptimizationTarget directly)         |
| `module.AsOptimizationTarget()`             | `module` (extension removed; the IS-A relationship makes it redundant)   |
| `module.GetState()` (inside an optimizer)   | `module.GetModuleState()` (typed `ModuleState`); `GetState()` now returns `TargetState` from the interface |
| `ChainTarget.For(a, b)`                     | `a.Then(b)`                                                              |
| `ChainTarget.For(a, b, c)`                  | `a.Then(b).Then(c)` or `new Pipeline<TIn, TOut> { a, b, c }`             |
| `ChatClientTarget.For(client, ...)`         | `client.AsOptimizationTarget(b => b.With...())`                          |
| `new ChatClientTarget(...)`                 | `client.AsOptimizationTarget(b => b.With...())` (constructor is internal) |
| `ctx.Bag["baseline"]`                       | `ctx.Diagnostics.BaselineScore`                                          |
| `ctx.Bag["whatever"]`                       | `ctx.Diagnostics.Snapshots["whatever"]`                                  |

## Before / after

### 1. Optimizing an `LmpModule`

```csharp
// Before
var ctx = new OptimizationContext { Target = ModuleTarget.For(module), TrainSet = trainSet, Metric = metric };
await new BootstrapFewShot().OptimizeAsync(ctx);

// After
var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };
await new BootstrapFewShot().OptimizeAsync(ctx);
```

### 2. Composing two targets

```csharp
// Before
var pipeline = ChainTarget.For(classifyTarget, draftTarget);

// After
var pipeline = classifyTarget.Then(draftTarget);

// Or with explicit IO marker types and collection-init
var pipeline = new Pipeline<string, Draft> { classifyTarget, draftTarget };
```

### 3. Adapting an `IChatClient`

```csharp
// Before
var target = ChatClientTarget.For(
    client,
    systemPrompt: "Answer concisely.",
    temperature: 0.7f,
    tools: [searchTool, calcTool]);

// After
var target = client.AsOptimizationTarget(b => b
    .WithSystemPrompt("Answer concisely.")
    .WithTemperature(0.7f)
    .WithTools([searchTool, calcTool]));
```

### 4. Cross-step state in optimizers

```csharp
// Before
ctx.Bag["baseline"] = baselineScore;
if (ctx.Bag.TryGetValue("baseline", out var b) && b is float f) { /* ... */ }
ctx.Bag["lmp.bandit:skills:alphas"] = alphas;

// After
ctx.Diagnostics.BaselineScore = baselineScore;
if (ctx.Diagnostics.BaselineScore is { } f) { /* ... */ }
ctx.Diagnostics.Snapshots["lmp.bandit:skills:alphas"] = alphas;
```

## Notes

- `ChainTarget`'s constructor is now `internal`. Compose via `.Then()` or
  `Pipeline<TIn, TOut>`; the underlying `ChainTarget` remains observable (it is
  what `.Then()` returns), so casts like `(ChainTarget)t1.Then(t2)` still work
  when you need to inspect `Targets`.
- `Pipeline<TIn, TOut>` carries the input/output types as documentation
  markers only. Runtime enforcement of the generic input/output contract lands
  in T3.
- `LmpModule.WithParameters(non-empty)` throws `NotSupportedException` citing
  T2: "TypedParameterSpace assignment is implemented in T2 (fractal predictor
  params)." `WithParameters(ParameterAssignment.Empty)` clones the module.
- `OptimizationContext.Diagnostics.Snapshots` is intentionally narrower than
  the old `Bag`: it is a typed safety valve for cross-step opaque payloads
  (Z3 feasibility sets, ContextualBandit posteriors). Prefer adding a typed
  field to `OptimizationDiagnostics` over a snapshot key when the data is
  reusable.
