# Unified Optimization Pipeline — Architecture

> **Status:** Design (Phase A–H)
> **Prior art:** AutoGluon, FLAML, scikit-learn pipelines, Keras-on-TF, Optuna
> **Research anchors:** DSPy (Khattab 2023), GEPA (arxiv:2507.19457), SIMBA (DSPy 2025),
>   MIPROv2 (Opsahl-Ong 2024), FrugalGPT (arxiv:2305.05176), OPRO (Google arxiv:2309.09027)

---

## Thesis

One optimization pipeline. Any target. Axis-aware steps. Two extensibility seams:

- **Horizontal** — add a new algorithm: one class implementing `IOptimizer`. No core changes.
- **Vertical** — bring any LM program: one adapter implementing `IOptimizationTarget`. No rewrite.

---

## Four-Tier Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Tier 4 — AutoML Façade                                                      │
│   Lmp.Optimize.AutoAsync(app, trainSet, goal: Goal.Accuracy)                │
│   module.OptimizeAsync(trainSet, devSet, metric)                            │
│   "takes what you have, returns what you had, only better"                  │
├─────────────────────────────────────────────────────────────────────────────┤
│ Tier 3 — Target Adapters                                                    │
│   IChatClient, AIAgent, Predictor, Func<,>                                  │
│   "bring your existing app, no rewrite required"                            │
├─────────────────────────────────────────────────────────────────────────────┤
│ Tier 2 — Pipeline & Algorithms                                              │
│   OptimizationPipeline, IOptimizer, OptimizationContext                     │
│   BootstrapFewShot, GEPA, MIPROv2, SIMBA, Z3Feasibility, …                 │
│   "professionals compose and extend"                                        │
├─────────────────────────────────────────────────────────────────────────────┤
│ Tier 1 — Primitives                                                         │
│   ISearchStrategy, IMetric, Trace, Trajectory, TypedParameterSpace          │
│   "researchers plug in new science"                                         │
└─────────────────────────────────────────────────────────────────────────────┘
```

### The Invariant (enforced by test)

> Tier 4's one-liner is **literally** Tier 2's pipeline constructed with defaults.
> There are no private escape hatches.
> A user can print the pipeline the façade built, copy it into Tier 2 code, tweak one step,
> and graduate without learning a new API.

This is "progressive disclosure of complexity" from Keras.

---

## Three Entry Points

### Tier 4 — Novice

```csharp
var optimized = await Lmp.Optimize.AutoAsync(
    myChatClient,          // or myModule, or myAgent — anything with an adapter
    trainSet, devSet,
    metric,
    goal: Goal.Accuracy,   // or: Goal.Speed, Goal.Cost, Goal.Balanced
    ct);
```

`Goal` drives stage selection transparently (no optimizer name appears at Tier 4):

| Goal | Stage sequence |
|------|---------------|
| `Accuracy` | `Z3Feasibility → BootstrapFewShot → GEPA → MIPROv2 → BayesianCalibration` |
| `Speed` | `BootstrapFewShot → RouteLLM → MultiFidelity` |
| `Cost` | `BootstrapFewShot → MIPROv2 (CostAwareSampler) → RouteLLM` |
| `Balanced` | `Z3Feasibility → BootstrapFewShot → GEPA → RouteLLM (Pareto)` |

### Tier 2 — Practitioner

```csharp
var result = await module
    .AsOptimizationPipeline()
    .UseTelemetry(activitySource)
    .WithBudget(b => b.MaxTokens(500_000).MaxSeconds(300))
    .Use(new BootstrapFewShot(maxDemos: 4))
    .Use(new GEPA(client, generations: 3))
    .Use(new MIPROv2(numTrials: 20, sampler: new SmacSampler(...)))
    .OptimizeAsync(trainSet, devSet, metric, ct);
```

### Tier 1 — Researcher

```csharp
public class NovaSampler : ISearchStrategy
{
    public ParameterAssignment Propose(TypedParameterSpace space) { ... }
    public void Update(ParameterAssignment a, float score, TrialCost cost) { ... }
}
var pipeline = module.AsOptimizationPipeline()
    .Use(new MIPROv2(sampler: new NovaSampler(...)));
```

---

## Two Extensibility Seams

### Horizontal — Add an Algorithm

Implement `IOptimizer`. Drop it into any pipeline with `.Use(new MyOptimizer())`. Ship on NuGet.
No LMP core changes. No registration. No base class. One interface, one method.

### Vertical — Add a Target Type

Implement `IOptimizationTarget`. Wrap any LM-backed component — `IChatClient`, `AIAgent`,
external API, or a `Func<TIn, TOut>`. The pipeline doesn't care about the target type.

---

## Five Optimization Axes

| Axis | What's Optimized | Key Algorithms | Status |
|------|-----------------|----------------|--------|
| **Instructions** | Prompt text, few-shot demos | BootstrapFewShot, GEPA, MIPROv2, SIMBA | Phase A–B |
| **Tools** | AITool pool, AIFunction descriptions | Z3Feasibility, MIPROv2, GEPA | Phase E |
| **Skills** | Skill routing, skill manifests | ContextualBandit, Z3Feasibility | Phase G |
| **Model + Hyperparameters** | Model selection, temperature | MultiFidelity, RouteLLM, CostAwareSampler | Phase H |
| **Multi-turn / Agent** | Trajectory quality | SIMBA, GEPA-on-trajectories | Phase F |

---

## AITool Hierarchy (confirmed stable from dotnet/extensions)

```
AITool (abstract, STABLE)                    ← ChatOptions.Tools = IList<AITool>
  ├── AIFunctionDeclaration (abstract, stable)
  │     └── AIFunction (abstract)            ← callable .NET methods (current LMP usage)
  ├── HostedFileSearchTool                   ← OpenAI file search
  ├── HostedWebSearchTool                    ← web search hosted tool
  ├── HostedCodeInterpreterTool              ← code execution
  └── HostedMcpServerTool (experimental)    ← MCP protocol tools
```

`TypedParameterSpace.Subset.Pool = IReadOnlyList<AITool>` — not `AIFunction` — so MCP tools
and all hosted tools are covered without any future code change.
`GEPA` evolves `AIFunction.Description` specifically; non-`AIFunction` tools use their own properties.

---

## `[AutoOptimize]` and `.g.cs` Artifacts — Unchanged Contract

```
OptimizationPipeline.OptimizeAsync(...)
    → OptimizationResult { Target, BaselineScore, OptimizedScore, Trials }
        → result.WriteArtifactAsync(options)               // new
            → ModuleTarget.WriteArtifactAsync(options)     // new
                → CSharpArtifactWriter (existing)
                    → Generated/{Module}.Optimized.g.cs    // same format; richer state later
```

Source-gen Pipeline 3 is **UNCHANGED**. The `.g.cs` format is the same.
The path from `module.OptimizeAsync()` to `result.WriteArtifactAsync()` is fully
continuous from the Tier 4 façade down — no hidden outputs.

### `.UseOptimized()` — Optimization as MEAI Middleware (Phase D)

```csharp
var client = azureClient.AsChatClient()
    .UseFunctionInvocation()
    .UseLogging()
    .UseOptimized(optimizationStudy);   // LMP optimization as just another MEAI concern
```

### `ChatClientBuilder.UseLmpTrace()` — Traces as MEAI Middleware (Phase B)

```csharp
// Instead of manual trace.Record() in Predictor, the middleware captures traces automatically:
var traced = chatClient
    .UseLmpTrace(ctx)       // composes with caching, OpenTelemetry, retries naturally
    .UseLogging();
```

---

## Microsoft.Extensions.AI.Evaluation — Three Integration Points

| Point | When | What |
|-------|------|------|
| **As metric** | Today (already works) | `EvaluationBridge` bridges M.E.AI `IEvaluator<T>` → LMP `Metric` |
| **As critique source** | Phase H (`EvaluationCritique`) | Evaluator rationale text → `ReflectionLog` → GEPA free reflection signal |
| **As acceptance gate** | Call-site pattern | Quality check before writing `.g.cs`; no framework changes needed |

The _multiplicative_ play: M.E.AI Evaluation generates structured rationale text (why a response
scores low on Coherence/Groundedness). `EvaluationCritique` injects those rationales into
`ctx.ReflectionLog`. GEPA reads `ReflectionLog` in its next pass — richer instruction evolution,
same LM call budget.

---

## Naming Convention

No `_Stage`, `_Step`, `_Optimizer`, or other suffixes. Algorithm classes are named by algorithm.

| Class | Algorithm |
|-------|-----------|
| `BootstrapFewShot` | Trace-mining teacher→student (DSPy) |
| `GEPA` | Reflection + Pareto evolution (arxiv:2507.19457) |
| `SIMBA` | Mini-batch stochastic ascent + self-reflection (DSPy 2025) |
| `MIPROv2` | Bayesian optimization over instruction × demo (Opsahl-Ong 2024) |
| `BayesianCalibration` | Posterior over trial history |
| `Z3Feasibility` | SMT constraint satisfaction (de Moura & Bjørner) |
| `EvaluationCritique` | M.E.AI.Evaluation → GEPA reflection bridge |
| `RouteLLM` | Cheap→expensive cascade (FrugalGPT / RouteLLM 2024) |
| `MultiFidelity` | Low-fidelity pruning (Hyperband, BOHB) |
| `ContextualBandit` | Per-request skill/tool routing (LinUCB, Thompson) |

Cross-cutting concerns (`telemetry`, `budget`) are pipeline methods, not standalone classes.

---

## Considered and Deferred

| Idea | Decision |
|------|---------|
| Infer.NET statistical layer | Too heavy for general use. Future opt-in package. |
| Interceptor-inlined optimized prompts | Compelling .NET-unique story — post-Phase-H. |
| Aspire fan-out for parallel MIPROv2 trials | Distributed trial execution — future Aspire capability. |
| Visual pipeline designer | Every framework that built one regretted it. Explicit no. |
| ArCHer RL | Research still consolidating. Deferred 2026+. |
| `OptimizationContext` as god-object | Guarded against: thin record of well-named fields; stages fail gracefully if a field is absent. |
