# Constraint-Based Demo Selection with Z3

**Technique:** SMT-solver-constrained few-shot example selection  
**Difficulty:** ⭐⭐⭐ Advanced

---

## What You'll Learn

- How random demo selection can leave entire input categories uncovered
- How the **Z3 SMT solver** selects few-shot demos that satisfy hard constraints (category coverage, exact cardinality, quality maximization)
- How to plug `Z3ConstrainedDemoSelector` into an LMP optimization pipeline alongside `BootstrapRandomSearch`
- How to write category extractors and token counters for your own domain

## The Problem

LMP optimizers like `BootstrapFewShot` and `BootstrapRandomSearch` pick few-shot demos **randomly** from a pool of successful traces. This works well on average, but it offers no structural guarantees:

| Scenario | Random Selection | What Can Go Wrong |
|---|---|---|
| Support ticket triage | Picks 4 demos from the pool | All 4 might be billing tickets — technical, account, and security categories get zero coverage |
| Multi-category classification | Picks top-scoring demos | High-scoring demos may cluster in one easy category |
| Token-constrained prompts | No token awareness | May select verbose demos that blow the context window |

When your application has **categorical diversity requirements** or **resource budgets**, you need a selector that treats these as hard constraints, not hopes.

## How It Works

`Z3ConstrainedDemoSelector` replaces the random selection step with a **satisfiability optimization problem** solved by [Z3](https://github.com/Z3Prover/z3), Microsoft's SMT (Satisfiability Modulo Theories) solver.

```
┌──────────────────────────────────────────────────────────────┐
│  Phase 1: Bootstrap Demo Pool (same as BootstrapFewShot)     │
│  ─ Run module on training data                               │
│  ─ Collect traces where metric ≥ threshold                   │
│  ─ Result: pool of (input, output, score) per predictor      │
└──────────────┬───────────────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────┐
│  Phase 2: Z3 Constrained Selection (per predictor)           │
│                                                              │
│  Variables:   d₀, d₁, …, dₙ  (Boolean: is demo i selected?)│
│                                                              │
│  Hard constraints:                                           │
│    • Σ dᵢ = maxDemos          (exact cardinality)            │
│    • ∀ category c: ∨ dᵢ       (≥1 demo per category)        │
│      where i ∈ category c                                    │
│                                                              │
│  Objectives:                                                 │
│    • Maximize  Σ (dᵢ × scoreᵢ)   (quality)                  │
│    • Minimize  Σ (dᵢ × tokensᵢ)  (token budget, optional)   │
└──────────────┬───────────────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────┐
│  Result: exactly maxDemos demos, every category covered,     │
│  highest quality within constraints, fewest tokens possible  │
└──────────────────────────────────────────────────────────────┘
```

The key insight: Z3 **guarantees** these properties. Random search cannot.

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 10 SDK | [Download](https://dotnet.microsoft.com/download) |
| Azure OpenAI resource | With a deployed chat model (e.g., `gpt-4.1-nano`) |
| Azure CLI / managed identity | `DefaultAzureCredential` — `az login` locally |

**Configure credentials** (one-time setup from this directory):

```bash
cd samples/LMP.Samples.Z3

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR_DEPLOYMENT_NAME"
```

> **Security:** Never commit endpoints or keys to source control. This sample uses `DefaultAzureCredential` — no API keys needed.

## Run It

```bash
dotnet run --project samples/LMP.Samples.Z3
```

The sample runs four steps sequentially and prints a comparison table at the end. Expect ~2-3 minutes depending on your model's throughput (51 training examples × multiple evaluation passes).

## Code Walkthrough

### 1. Domain Types (`Types.cs`)

The sample models a **support ticket triage** pipeline with three typed signatures:

```csharp
// Input: raw ticket text
public record TicketInput(string TicketText);

// Intermediate: classification result
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    public required string Category { get; init; }  // billing, technical, account, general
    public required int Urgency { get; init; }       // 1 (low) to 5 (critical)
}

// Output: customer-facing reply
[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    public required string ReplyText { get; init; }
}
```

### 2. Module Pipeline (`SupportTriageModule.cs`)

Two predictors chained: classify → draft.

```csharp
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public override async Task<DraftReply> ForwardAsync(TicketInput input, ...)
    {
        var classification = await _classify.PredictAsync(input, trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"));

        return await _draft.PredictAsync(classification, trace: Trace);
    }
}
```

Each predictor gets its own demo pool — Z3 selects demos independently for `classify` and `draft`.

### 3. Z3 Constrained Demo Selection (`Program.cs`)

The heart of the sample — configuring and running the Z3 selector:

```csharp
// Category extractor: inspects the ticket text to determine category
Func<object, string> categoryExtractor = input =>
{
    if (input is TicketInput ticket)
    {
        var text = ticket.TicketText.ToLowerInvariant();
        if (text.Contains("charg") || text.Contains("invoice") || text.Contains("bill"))
            return "billing";
        if (text.Contains("vpn") || text.Contains("api") || text.Contains("error"))
            return "technical";
        // ... more categories
    }
    return "unknown";
};

// Token counter: rough estimate for token budget optimization
Func<object, int> tokenCounter = input =>
    input is TicketInput ticket ? ticket.TicketText.Length / 4 : 10;

// Create and run the Z3 selector
var z3Selector = new Z3ConstrainedDemoSelector(
    categoryExtractor: categoryExtractor,
    tokenCounter: tokenCounter,
    maxDemos: 4,                // exactly 4 demos per predictor
    metricThreshold: 0.3f);     // minimum quality to enter the pool

var z3Optimized = await z3Selector.CompileAsync(z3Module, trainSet, metric);
```

**What the Z3 solver does internally** (in `Z3ConstrainedDemoSelector.cs`):

1. Creates a Boolean variable `d₀…dₙ` for each demo candidate
2. Adds a **cardinality constraint**: `Σ dᵢ = 4` (exactly 4 selected)
3. Groups demos by category and adds **coverage constraints**: `d₂ ∨ d₅ ∨ d₈` (at least one billing demo, etc.)
4. Sets the **primary objective**: maximize `Σ (dᵢ × scoreᵢ × 1000)` (scores scaled to integers)
5. Sets an optional **secondary objective**: minimize `Σ (dᵢ × tokensᵢ)` (prefer concise demos)
6. Calls `opt.Check()` — Z3 returns the provably optimal selection

### 4. The Comparison (`Program.cs`)

The sample runs three configurations head-to-head on the same dev set:

| Step | Optimizer | Demo Selection Strategy |
|---|---|---|
| 1 | None (baseline) | No demos — zero-shot |
| 2 | `BootstrapRandomSearch` | Random: 8 trials, pick best-scoring set of 4 demos |
| 3 | `Z3ConstrainedDemoSelector` | Constrained: exactly 4 demos, every category covered, quality maximized |

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — Z3 Constraint-Based Demo Selection           ║
╚══════════════════════════════════════════════════════╝

Step 1: Baseline (no optimization)
  Score: XX.X%

Step 2: BootstrapRandomSearch (random demo selection)
  Score: XX.X%
  Demos selected (random — no coverage guarantees):
    [classify] 4 demos
    [draft] 4 demos

Step 3: Z3ConstrainedDemoSelector
  Constraints:
    • Exactly 4 demos per predictor
    • At least 1 demo per ticket category
    • Maximize total quality score
    • Minimize token usage (secondary)
  Score: XX.X%
  Demos selected (Z3 — category coverage guaranteed):
    [classify] 4 demos
    [draft] 4 demos

╔══════════════════════════════════════════════════════╗
║   Results Comparison                                 ║
╠══════════════════════════════════════════════════════╣
║   Baseline (no opt):           XX.X%                 ║
║   BootstrapRandomSearch:       XX.X%                 ║
║   Z3 Constrained:              XX.X%                 ║
╠══════════════════════════════════════════════════════╣
║   Z3 guarantees structural properties:               ║
║   · Category coverage (≥1 demo per category)         ║
║   · Exact cardinality (exactly maxDemos selected)    ║
║   · Quality maximization within constraints          ║
║   · Token minimization (secondary objective)         ║
╚══════════════════════════════════════════════════════╝
```

> Exact scores depend on the model and run. The key observation: Z3 and random may score similarly overall, but Z3 **guarantees** balanced category coverage — critical for production systems where every ticket type must be handled well.

## Key Takeaways

1. **Random selection is structurally blind.** `BootstrapRandomSearch` optimizes total score but has no mechanism to ensure diversity. If billing tickets are easiest, all demos may be billing tickets.

2. **Z3 turns "should" into "must."** Category coverage, exact cardinality, and token budgets become hard constraints — the solver either satisfies all of them or reports the problem is infeasible.

3. **Same bootstrap, smarter selection.** `Z3ConstrainedDemoSelector` reuses LMP's bootstrap phase to build the demo pool. Only the selection step changes — no new LLM calls needed for the Z3 phase.

4. **Domain knowledge goes into extractors.** The `categoryExtractor` and `tokenCounter` functions are where you encode your domain — what "balanced" means for your application.

5. **Fallback is built in.** If the constraints are unsatisfiable (e.g., no security demos in the pool), the selector falls back to top-scoring demos rather than failing.

## Next Steps

| Sample | What It Adds |
|---|---|
| [**GEPA**](../LMP.Samples.GEPA/) | Evolutionary instruction optimization — GEPA **rewrites** predictor instructions by reflecting on failures, complementing Z3's demo selection |
| [**Advanced Optimizers**](../LMP.Samples.AdvancedOptimizers/) | `SmacSampler`, `TraceAnalyzer`, `CostAwareSampler` — explore different search strategies and cost-aware optimization |
