# Advanced Optimizers

| Technique | `ISampler` · `SmacSampler` · `TraceAnalyzer` · `CostAwareSampler` |
|---|---|
| **Difficulty** | Advanced |
| **Task** | Support-ticket triage (classify → draft reply) |
| **Prerequisites** | Azure OpenAI resource with a chat deployment |

---

## What You'll Learn

* How the **`ISampler`** interface lets you swap search strategies inside MIPROv2 without
  changing any other code.
* When to choose **TPE** (independent parameters) vs. **SMAC / Random Forest** (parameter
  interactions) vs. **CostAwareSampler** (budget-sensitive optimization).
* How **`TraceAnalyzer`** extracts per-value posteriors and interaction strengths from trial
  history — and how those posteriors enable **warm-start transfer** to a new optimization run.
* How **`CostAwareSampler`** uses a FLAML Flow2–style adaptive step size to penalize expensive
  configurations, with **pluggable cost projections** (tokens, dollars, latency, or blends).

---

## The Problem

MIPROv2 searches over **(instruction variant × demo subset)** pairs for each predictor in your
pipeline. The default TPE (Tree-Structured Parzen Estimator) sampler works well when parameters
are roughly independent, but real pipelines often have **interactions** — the best instruction for
step 1 depends on which demos are shown in step 2.

Different problems call for different search strategies:

| Strategy | Best When |
|---|---|
| **TPE** (default) | Parameters are roughly independent; you want a fast baseline |
| **SmacSampler** (SMAC/RF) | Parameters interact; the RF surrogate captures joint effects |
| **CostAwareSampler** (FLAML Flow2) | You have a token or dollar budget; quality alone isn't enough |

This sample runs **all three** on the same task, analyzes the trial traces, and shows how to
transfer knowledge from one run to the next.

---

## How It Works

```
                       ┌─────────────────────────────────────────┐
                       │              MIPROv2                     │
                       │                                         │
                       │  Phase 1  Bootstrap demo pool            │
                       │  Phase 2  Propose instruction variants   │
                       │  Phase 3  Bayesian search over           │
                       │           (instr × demos) per predictor  │
                       │                                         │
                       │      ┌──────────┐                       │
                       │      │ ISampler │◄── samplerFactory      │
                       │      └────┬─────┘                       │
                       │           │                             │
                       │     Propose() / Update(config, score)   │
                       └───────────┼─────────────────────────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              │                    │                    │
      ┌───────▼──────┐   ┌────────▼───────┐  ┌────────▼──────────┐
      │  TPE Sampler  │   │  SmacSampler   │  │ CostAwareSampler  │
      │  (default)    │   │  RF surrogate  │  │  FLAML Flow2      │
      │  independent  │   │  + Expected    │  │  adaptive step    │
      │  marginals    │   │  Improvement   │  │  + cost penalty   │
      └───────────────┘   └────────────────┘  └───────────────────┘
```

### Key concepts

1. **Pluggable samplers** — MIPROv2 accepts a `samplerFactory` delegate. Pass
   `cards => new SmacSampler(cards)` and the entire search strategy changes; the rest of
   the pipeline stays the same.
2. **Trial history** — After optimization, `mipro.LastTrialHistory` exposes every
   `(config, score, cost?)` triple for analysis.
3. **TraceAnalyzer** — Static helpers that turn raw trial history into actionable insights:
   *posteriors* (mean ± stderr per parameter value) and *interactions* (ANOVA residual
   analysis between parameter pairs).
4. **Warm-start transfer** — Feed posteriors from a prior run into a new sampler via
   `TraceAnalyzer.WarmStart()`, injecting synthetic trials that bias early exploration toward
   previously successful regions.
5. **Cost-aware optimization** — `CostAwareSampler` tracks a `TrialCost` record
   (tokens, latency, API calls) and applies a configurable *cost projection* function so the
   optimizer penalizes expensive configurations.

---

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 9+ SDK | `dotnet --version` |
| Azure OpenAI resource | A deployed chat model (e.g. `gpt-4.1-nano`) |
| Authentication | `DefaultAzureCredential` — `az login` locally, or managed identity in Azure |

### Configure secrets

```bash
cd samples/LMP.Samples.AdvancedOptimizers

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "YOUR_DEPLOYMENT_NAME"
```

> **Never** commit endpoints or keys to source control. The sample uses
> `DefaultAzureCredential`, which picks up `az login` sessions, environment variables,
> or managed identity automatically.

---

## Run It

```bash
dotnet run --project samples/LMP.Samples.AdvancedOptimizers
```

The sample runs seven steps sequentially: baseline → TPE → TraceAnalyzer → SMAC →
CostAwareSampler (four projection variants) → warm-start → final comparison.

Expect **~3–5 minutes** depending on model latency and trial count.

---

## Code Walkthrough

### 1 — Types & Module (`Types.cs`, `SupportTriageModule.cs`)

The pipeline is a two-step LM program:

```
TicketInput ──► [classify] ──► ClassifyTicket ──► [draft] ──► DraftReply
  "I was charged         { Category: "billing"       "Thank you for
   twice..."               Urgency: 4 }               reaching out..."
```

```csharp
// Types.cs — Strongly typed signatures with descriptions
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account, general")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}
```

The module chains two `Predictor<,>` calls with a validation constraint:

```csharp
// SupportTriageModule.cs
var classification = await _classify.PredictAsync(
    input,
    trace: Trace,
    validate: result =>
        LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
            "Urgency must be between 1 and 5"),
    cancellationToken: cancellationToken);
```

### 2 — The `ISampler` Interface

Every sampler implements two methods:

```csharp
public interface ISampler
{
    int TrialCount { get; }

    // Propose the next (parameter → category-index) configuration
    Dictionary<string, int> Propose();

    // Record the result so future proposals improve
    void Update(Dictionary<string, int> config, float score);

    // Overload for cost-aware samplers
    void Update(Dictionary<string, int> config, float score, TrialCost cost);
}
```

MIPROv2 calls `Propose()` at the start of each trial and `Update()` at the end. The sampler
decides *how* to explore the search space — TPE, random forest, or cost-penalized flow — but
MIPROv2 doesn't need to know which.

### 3 — MIPROv2 + Default TPE Sampler

```csharp
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 4,
    metricThreshold: 0.3f,
    seed: 42);
// No samplerFactory → defaults to CategoricalTpeSampler

var tpeOptimized = await mipro.CompileAsync(tpeModule, trainSet, untypedMetric);
```

After optimization, grab the trial history for analysis:

```csharp
var tpeHistory = mipro.LastTrialHistory;
// IReadOnlyList<TrialResult> — each entry is (Config, Score, Cost?)
```

### 4 — TraceAnalyzer: Posteriors & Interactions

**Posteriors** — For each parameter value, what was the average score?

```csharp
var cardinalities = new Dictionary<string, int>
{
    ["classify_instr"] = 4, ["classify_demos"] = 4,
    ["draft_instr"]    = 4, ["draft_demos"]    = 4
};

var posteriors = TraceAnalyzer.ComputePosteriors(tpeHistory, cardinalities);
// posteriors["classify_instr"][2] => ParameterPosterior(Mean, StandardError, Count)
```

Each `ParameterPosterior` tells you: *"When classify_instr = 2, the mean score was 0.72 ± 0.05
across 4 trials."* High mean + low standard error = high confidence that this value is good.

**Interactions** — Do two parameters have synergy (or conflict)?

```csharp
var interactions = TraceAnalyzer.DetectInteractions(tpeHistory);
// interactions[("classify_instr", "draft_demos")] => 0.0023
```

This uses ANOVA-style residual analysis. A large value means the joint effect of the two
parameters is *not* explained by their individual effects — they interact. If you see strong
interactions, consider switching to SmacSampler, which captures joint effects through its
random-forest surrogate.

### 5 — SmacSampler (SMAC / Random Forest)

```csharp
var miproSmac = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    // ...same hyperparams...
    samplerFactory: cards => new SmacSampler(cards, numTrees: 10, seed: 42));
```

SmacSampler fits a random forest over the trial history and uses **Expected Improvement (EI)**
as the acquisition function. It proposes candidates via local search (one-mutation neighborhoods
around the best-known configs) plus random EI search, then picks the candidate with the highest
EI.

| Parameter | Default | Meaning |
|---|---|---|
| `numTrees` | 10 | Trees in the random forest ensemble |
| `numInitialTrials` | max(2 × params, 6) | Uniform-random trials before switching to SMAC |
| `numRandomEISearch` | 100 | Random configs evaluated for EI per proposal |

### 6 — CostAwareSampler with Custom Cost Projections

`CostAwareSampler` wraps FLAML's Flow2 algorithm: it maps categorical parameters to a
continuous space, applies Gaussian perturbations with an adaptive step size, and penalizes
configurations whose cost exceeds 1.5× the running average.

The **cost projection** function maps the multi-dimensional `TrialCost` to a single scalar:

```csharp
// Default: total tokens
cards => new CostAwareSampler(cards, seed: 42)

// Dollar pricing
cards => new CostAwareSampler(cards,
    costProjection: c => c.OutputTokens * 0.06 / 1000
                       + c.InputTokens  * 0.01 / 1000,
    seed: 42)

// Latency
cards => new CostAwareSampler(cards,
    costProjection: c => c.ElapsedMilliseconds,
    seed: 42)

// Blended (quality + latency)
cards => new CostAwareSampler(cards,
    costProjection: c => c.TotalTokens * 0.7
                       + c.ElapsedMilliseconds * 0.3,
    seed: 42)
```

`TrialCost` carries five dimensions — mix and match to express your real cost model:

| Field | Type | Meaning |
|---|---|---|
| `TotalTokens` | `long` | Input + output tokens across all LM calls |
| `InputTokens` | `long` | Prompt tokens only |
| `OutputTokens` | `long` | Completion tokens only |
| `ElapsedMilliseconds` | `long` | Wall-clock duration |
| `ApiCalls` | `int` | Number of LM API calls |

### 7 — Warm-Start Transfer

After analyzing one run, you can **bootstrap** a new sampler with prior knowledge:

```csharp
// 1. Compute posteriors from the TPE run
var posteriors = TraceAnalyzer.ComputePosteriors(tpeHistory, cardinalities);

// 2. Create a fresh SmacSampler
var warmSampler = new SmacSampler(cardinalities, numTrees: 10, seed: 123);

// 3. Inject synthetic trials derived from the posteriors
TraceAnalyzer.WarmStart(warmSampler, posteriors, numSyntheticTrials: 5);

// 4. Use the warm-started sampler in a new MIPROv2 run
var miproWarm = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    samplerFactory: _ => warmSampler);   // ← already pre-loaded
```

`WarmStart` generates synthetic `(config, score)` pairs from the posterior means and feeds
them into the sampler via `Update()`. The sampler's surrogate model starts with a rough map of
the search space instead of exploring from scratch — especially useful when:

* You've optimized a similar task before and want to **transfer** knowledge.
* You're running incremental optimization sessions (e.g., nightly retraining).
* You want to switch sampler type (TPE → SMAC) without losing prior signal.

---

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — Advanced Optimizers Demo (Azure OpenAI)      ║
║   ISampler · SmacSampler · TraceAnalyzer              ║
║   CostAwareSampler — Cost-Frugal Optimization         ║
╚══════════════════════════════════════════════════════╝

Step 1: Baseline Evaluation (no optimization)
  Score: 42.0%

Step 2: MIPROv2 + TPE Sampler (default)
  Score: 68.0%
  Trials collected: 10

Step 3: TraceAnalyzer — Post-Optimization Analysis
  3a) Parameter Posteriors (mean score ± stderr per choice):
    [classify_instr]
      Choice 2: 0.720 ± 0.050  (n=4)
      Choice 0: 0.640 ± 0.080  (n=3)
      ...
  3b) Parameter Interactions (ANOVA residual analysis):
    ~ Moderate: classify_instr × draft_demos = 0.0023
    · Weak: classify_demos × draft_instr = 0.0005
    ...

Step 4: MIPROv2 + SmacSampler (SMAC/RF)
  Score: 72.0%

Step 5: CostAwareSampler — Cost-Frugal Optimization
  5a) Default projection (TotalTokens):     Score: 66.0%
  5b) Dollar pricing projection:            Score: 64.0%
  5c) Latency projection:                   Score: 62.0%
  5d) Blended projection:                   Score: 65.0%

Step 6: Warm-Start Transfer Learning
  Warm-started sampler now has 5 synthetic trials
  Score: 74.0%

╔══════════════════════════════════════════════════════════╗
║   Results Comparison                                     ║
╠══════════════════════════════════════════════════════════╣
║   Baseline (no opt):             42.0%                   ║
║   MIPROv2 + TPE:                 68.0%                   ║
║   MIPROv2 + SmacSampler:         72.0%                   ║
║   MIPROv2 + CostAware (tokens):  66.0%                   ║
║   MIPROv2 + CostAware (dollar):  64.0%                   ║
║   MIPROv2 + CostAware (latency): 62.0%                   ║
║   MIPROv2 + CostAware (blended): 65.0%                   ║
║   MIPROv2 + Warm-Start SMAC:     74.0%                   ║
╚══════════════════════════════════════════════════════════╝
```

> Exact scores depend on the model, temperature, and random seed. The relative ordering
> (baseline < TPE ≤ SMAC, cost-aware trades quality for savings, warm-start ≥ cold-start)
> is the important pattern.

---

## Key Takeaways

1. **`ISampler` is the extension point.** Implement `Propose()` + `Update()` to plug any
   search algorithm into MIPROv2 — no changes to the optimizer or the module.

2. **TPE is a strong default.** It's fast and works well when parameters don't interact much.
   Switch to SmacSampler when TraceAnalyzer reveals strong interactions.

3. **TraceAnalyzer turns trials into knowledge.** Posteriors show which parameter values are
   best; interactions show which parameters need to be optimized jointly. Both are cheap to
   compute after the fact.

4. **Warm-start avoids cold starts.** Transferring posteriors into a new sampler gives it a
   head start, especially valuable for iterative or cross-task optimization.

5. **CostAwareSampler balances quality and cost.** By plugging a cost projection
   (tokens, dollars, latency, or a blend), you get configurations that are *good enough*
   without blowing your budget. The adaptive step size automatically explores more when costs
   are low and tightens when they're high.

---

## Next Steps

* **[AutoOptimize]** — If choosing a sampler manually feels like too much, look at the
  AutoOptimize sample, which automatically selects a sampler strategy based on search-space
  analysis and budget constraints.
