# MIPROv2 — Bayesian Instruction + Demo Optimization

| | |
|---|---|
| **Technique** | Bayesian instruction and demo optimization (MIPROv2) |
| **Difficulty** | Advanced |

## What You'll Learn

- How MIPROv2 jointly optimizes **instructions** *and* **few-shot demos** — something simpler optimizers can't do
- How a **proposal LM** generates diverse instruction variants for each predictor
- How **Tree-structured Parzen Estimator (TPE)** search efficiently explores the combinatorial space of instruction × demo-set configurations
- How to compare MIPROv2 against a demo-only baseline (`BootstrapRandomSearch`)

## The Problem

Hand-writing prompts is an art that doesn't scale. You can spend hours tweaking an instruction only to discover that a completely different phrasing works better — and that the *combination* of instruction wording and few-shot examples matters even more than either one alone.

`BootstrapRandomSearch` (covered in the AdvancedOptimizers sample) can find good **demos**, but it never touches the **instruction text**. If your starting instruction is weak, demos alone can only compensate so much.

MIPROv2 solves this by treating the problem as a structured search:

1. **Generate** — ask an LM to propose instruction alternatives
2. **Combine** — pair each instruction with different demo subsets
3. **Evaluate** — score every combination against a metric
4. **Focus** — use Bayesian optimization (TPE) to concentrate trials on the most promising region of the search space

## How It Works

```
┌─────────────────────────────────────────────────────────────────────┐
│                        MIPROv2 Pipeline                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Phase 1: Bootstrap Demo Pool                                       │
│  ┌───────────┐     ┌────────────────┐     ┌──────────────────────┐ │
│  │ Train data │ ──▶ │ BootstrapFewShot│ ──▶ │ Pool of good demos   │ │
│  └───────────┘     └────────────────┘     │ per predictor        │ │
│                                            └──────────────────────┘ │
│                                                                     │
│  Phase 2: Propose Instruction Variants                              │
│  ┌────────────────┐     ┌──────────────────────────────────────┐   │
│  │ Proposal LM     │ ──▶ │ N instruction candidates / predictor │   │
│  │ (sees current   │     │  (original + N-1 LM-generated)      │   │
│  │  instr + demos) │     └──────────────────────────────────────┘   │
│  └────────────────┘                                                 │
│                                                                     │
│  Phase 3: Bayesian TPE Search                                       │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ For each trial:                                                │ │
│  │   1. TPE proposes (instruction_idx, demo_subset_idx)           │ │
│  │      for EVERY predictor                                       │ │
│  │   2. Apply config → evaluate on validation split               │ │
│  │   3. Feed score back to TPE → update "good" vs "bad" models    │ │
│  │   4. Keep best candidate                                       │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                     │
│  Output: Module with optimized instructions AND demos               │
└─────────────────────────────────────────────────────────────────────┘
```

**Why TPE?** With 4 instruction candidates × 4 demo subsets × 2 predictors, there are 256 possible configurations. Exhaustive search is expensive. TPE models which configurations are "good" (top γ fraction) vs "bad" and proposes new trials that are more likely to land in the good region. The `gamma` parameter (default 0.25) controls this split — lower values make the search more exploitative.

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 9+ SDK | [Download](https://dotnet.microsoft.com/download) |
| Azure OpenAI resource | A deployment of `gpt-4o-mini` (or any chat model) |
| Azure credentials | `DefaultAzureCredential` — Azure CLI login, managed identity, etc. |

### Configure secrets

```bash
cd samples/LMP.Samples.MIPROv2

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "gpt-4o-mini"
```

> **Tip:** MIPROv2 uses a `proposalClient` for instruction generation. In this sample both the module and the proposal client share the same deployment, but in production you could point the proposal client at a cheaper model (e.g. `gpt-4.1-nano`) to reduce cost.

## Run It

```bash
dotnet run --project samples/LMP.Samples.MIPROv2
```

The sample runs through six steps:

1. **Baseline** — evaluate the module with no optimization
2. **BootstrapRandomSearch** — optimize demos only (8 trials)
3. **Capture original instructions** — print the starting instruction text
4. **MIPROv2** — optimize instructions + demos via Bayesian search (10 trials)
5. **Show optimized instructions** — print the rewritten instruction text
6. **Compare** — side-by-side results table

Expect the run to take a few minutes depending on your Azure OpenAI throughput limits.

## Code Walkthrough

### 1. Type definitions (`Types.cs`)

The pipeline has two steps, each with its own signature:

```csharp
// Input
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

// Intermediate — classify step output
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account, general")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

// Final — draft reply step output
[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}
```

`[LmpSignature]` provides the **default instruction** for each predictor. MIPROv2 will propose alternatives to this text.

### 2. Two-step module (`SupportTriageModule.cs`)

```csharp
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply>  _draft;

    public SupportTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client) { Name = "classify" };
        _draft    = new Predictor<ClassifyTicket, DraftReply>(client)  { Name = "draft" };
    }

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input, CancellationToken cancellationToken = default)
    {
        var classification = await _classify.PredictAsync(
            input, trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"),
            cancellationToken: cancellationToken);

        var reply = await _draft.PredictAsync(
            classification, trace: Trace, cancellationToken: cancellationToken);

        return reply;
    }
}
```

Each predictor has a **Name** — MIPROv2 uses these names to track instruction candidates and demo subsets per predictor independently.

### 3. MIPROv2 configuration (`Program.cs`)

```csharp
var mipro = new MIPROv2(
    proposalClient: client,          // LM that generates instruction candidates
    numTrials: 10,                   // Bayesian search iterations
    numInstructionCandidates: 4,     // 1 original + 3 LM-proposed per predictor
    numDemoSubsets: 4,               // Random subsets of the bootstrapped demo pool
    maxDemos: 4,                     // Max demos per predictor in each subset
    metricThreshold: 0.3f,           // Min score to include a trace as a demo
    gamma: 0.25,                     // TPE quantile — top 25% = "good" trials
    seed: 42);                       // Reproducibility
```

| Parameter | What it controls |
|---|---|
| `proposalClient` | The LM used in Phase 2 to generate instruction variants. Can be a different (cheaper) model. |
| `numTrials` | How many (instruction, demo-set) combos to evaluate. More = better but costlier. |
| `numInstructionCandidates` | Search space width for instructions. The original instruction is always candidate #0. |
| `numDemoSubsets` | Search space width for demo sets. Each subset is a random sample from the bootstrapped pool. |
| `maxDemos` | Upper bound on few-shot examples per predictor per subset. |
| `metricThreshold` | Only traces scoring ≥ this value during bootstrapping become candidate demos. |
| `gamma` | TPE split point. Lower = more exploitative; higher = more exploratory. |
| `seed` | Fixes randomness for reproducible runs. |

### 4. Compile and compare

```csharp
// Optimize
var mipOptimized = await mipro.CompileAsync(mipModule, trainSet, untypedMetric);

// Evaluate on held-out dev set
var mipScore = await Evaluator.EvaluateAsync(mipOptimized, devSet, metric);

// Inspect what MIPROv2 chose
foreach (var (name, pred) in mipOptimized.GetPredictors())
{
    Console.WriteLine($"[{name}] \"{pred.Instructions}\"");
    Console.WriteLine($"         Demos: {pred.Demos.Count}");
}
```

`CompileAsync` internally splits the training set 80/20 into bootstrap and validation splits, so you don't need to create a separate validation set.

### 5. The metric

The sample uses a structured rubric that scores three aspects:

| Component | Weight | What it checks |
|---|---|---|
| Category match | 0.4 | Does the reply mention the correct category keyword? |
| Keyword overlap | 0.4 | How many content keywords from the label appear in the prediction? |
| Tone | 0.2 | Does the reply start with an appropriate greeting? |

This produces a score in `[0, 1]` that the optimizer maximizes.

## Expected Output

```
╔══════════════════════════════════════════════════════════════╗
║   LMP — MIPROv2 Bayesian Optimization Demo                  ║
╚══════════════════════════════════════════════════════════════╝

Step 1: Evaluate Baseline (no optimization)
────────────────────────────────────────────
  Score: 45.0% (avg), 20.0% (min), 80.0% (max)

Step 2: BootstrapRandomSearch (optimizes demos only)
────────────────────────────────────────────────────
  Score: 58.0%

Step 3: Original Instructions (before MIPROv2)
───────────────────────────────────────────────
  [classify] "Classify a support ticket by category and urgency"
  [draft]    "Draft a helpful reply to the customer based on the ticket classification"

Step 4: MIPROv2 Bayesian Optimization
──────────────────────────────────────
  Score: 72.0%

Step 5: Optimized Instructions (after MIPROv2)
───────────────────────────────────────────────
  [classify] "Analyze the incoming support ticket and determine its primary
              category (billing, technical, account, or general) along with
              an urgency rating from 1 to 5..."
  [draft]    "Compose a professional, empathetic customer response that
              addresses the specific issue identified in the classification..."

╔══════════════════════════════════════════════════════════════╗
║   Results Comparison                                         ║
╠══════════════════════════════════════════════════════════════╣
║   Baseline (no opt):          45.0%                          ║
║   BootstrapRandomSearch:      58.0%                          ║
║   MIPROv2 (instr + demos):    72.0%                          ║
╚══════════════════════════════════════════════════════════════╝
```

> **Note:** Exact scores and instructions will vary between runs depending on your model and any non-determinism in LM responses. The key observation is the **relative improvement** from baseline → demo-only → instruction+demo optimization.

## Key Takeaways

1. **Instructions matter as much as demos.** `BootstrapRandomSearch` improves on the baseline by finding good demos, but MIPROv2 goes further by also rewriting the instruction text.

2. **The proposal LM is the creative engine.** It sees the current instruction, field descriptions, and example demos, then generates diverse phrasings. Better proposal models generally produce better candidates.

3. **TPE makes the search tractable.** Instead of evaluating every combination, TPE learns from past trials to focus on promising configurations. The `gamma` parameter controls the exploitation/exploration trade-off.

4. **Each predictor is optimized independently.** In a multi-step pipeline (like classify → draft), MIPROv2 finds the best instruction and demo set for *each* step separately while evaluating the *end-to-end* pipeline score.

5. **You can control cost.** Use a cheaper model for `proposalClient`, reduce `numTrials` for faster iteration, or increase `metricThreshold` to keep only high-quality demos in the pool.

## Next Steps

| Sample | What it covers |
|---|---|
| [AdvancedOptimizers](../LMP.Samples.AdvancedOptimizers/) | Demo-only optimization with `BootstrapFewShot` and `BootstrapRandomSearch` — a good prerequisite for understanding MIPROv2's Phase 1 |
| [GEPA](../LMP.Samples.GEPA/) | Gradient-free evolutionary prompt optimization — an alternative to Bayesian search |
| [Evaluation](../LMP.Samples.Evaluation/) | Deep dive into metrics, evaluators, and scoring strategies |
