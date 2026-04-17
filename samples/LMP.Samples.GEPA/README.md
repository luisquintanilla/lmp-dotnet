# GEPA — Evolutionary Reflection-Driven Optimization

| | |
|---|---|
| **Technique** | Evolutionary instruction optimization via LLM reflection (GEPA) |
| **Difficulty** | Advanced |

## What You'll Learn

- How GEPA **diagnoses failures** using execution traces and an LLM "reflection" step — unlike Bayesian optimizers that only see scores
- How an evolutionary loop of **mutation, evaluation, and Pareto selection** systematically improves predictor instructions
- How the **Pareto frontier** retains diverse, non-dominated candidates so the search doesn't collapse to a single strategy
- How to compare GEPA with MIPROv2 to understand when each optimizer shines

## The Problem

Out-of-the-box LLM instructions are generic. You can manually rewrite them, but that's tedious and doesn't scale when a module has multiple predictors. Bayesian optimizers like MIPROv2 can search over instructions, but they treat each candidate as a black box — they know *that* a configuration scored 0.45, but not *why*.

GEPA closes that gap. It captures the full execution trace of every failed example, hands the trace to a reflection LLM, and asks: **"What went wrong, and how should the instruction change?"** The result is targeted, explainable improvements rather than blind trial-and-error.

## How It Works

```
┌────────────────────────────────────────────────────────┐
│  1. Evaluate candidate on a mini-batch of training data │
│  2. Identify failures (score < threshold)               │
│  3. Send failure traces to the reflection LLM           │
│     → "The classify predictor output 'urgent' for a     │
│        routine billing question. Fix: be conservative." │
│  4. Reflection LLM proposes a new instruction           │
│  5. Mutated candidate enters the Pareto frontier        │
│  6. Every N iterations: merge (crossover) two           │
│     Pareto-optimal parents into a new candidate         │
└────────────────────────────────────────────────────────┘
```

**Key concepts:**

- **Reflective Mutation** — Run → trace → diagnose → rewrite. This is the core loop.
- **Pareto Frontier** — A set of candidates where no single candidate beats all others on every example. This keeps diversity: one candidate may excel at billing tickets while another handles security tickets better.
- **Merge (Crossover)** — Periodically, GEPA picks two diverse Pareto-optimal parents and combines their per-predictor instructions independently — the best `classify` instruction from parent A is paired with the best `draft` instruction from parent B. A merged candidate is only added to the frontier if it is not dominated by both parents.
- **Progress reporting** — Each iteration is tagged `[PASS]` (improved frontier), `[skip]` (no improvement), or `[MERGE]` (crossover attempt). The displayed `best=` score always reflects the true best score across all frontier candidates.

**GEPA vs. MIPROv2 at a glance:**

| | GEPA | MIPROv2 |
|---|---|---|
| Strategy | Evolutionary + LLM reflection | Bayesian (TPE/SMAC) |
| Feedback signal | Execution traces + failure diagnosis | Scores only |
| Optimizes | Instructions only | Instructions + demos jointly |
| Best for | Targeted instruction refinement | Broad instruction + demo search |

> **Tip:** Use GEPA first to evolve high-quality instructions, then pass the result to MIPROv2 for joint instruction + demo optimization.

## Prerequisites

1. **Azure OpenAI resource** with a chat deployment (e.g., `gpt-4.1-nano`).
2. **Azure identity** configured — `DefaultAzureCredential` must be able to authenticate (Azure CLI login, managed identity, etc.).
3. **User secrets** set for this project:

```bash
cd samples/LMP.Samples.GEPA

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "YOUR_DEPLOYMENT"

# Optional: use a separate (cheaper) model for the reflection LLM
dotnet user-secrets set "AzureOpenAI:ReflectionDeployment" "YOUR_REFLECTION_DEPLOYMENT"
```

## Run It

```bash
dotnet run --project samples/LMP.Samples.GEPA
```

The program runs five steps: baseline evaluation → capture original instructions → GEPA optimization → show evolved instructions → MIPROv2 comparison. Expect it to take a few minutes depending on model latency.

## Code Walkthrough

### 1. Types & Signatures (`Types.cs`)

Three records define the data flow through the module:

```csharp
public record TicketInput(string TicketText);

// C# enum enforces valid categories via JSON Schema (equivalent to DSPy's typing.Literal)
public enum TicketCategory { Billing, Technical, Account, General }

[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    public required TicketCategory Category { get; init; }
    public required int Urgency { get; init; }
}

[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    public required string ReplyText { get; init; }
}
```

`[LmpSignature]` supplies the default instruction that GEPA will evolve. `[Description]` attributes on properties tell the LLM what each field means.

### 2. The Module — Two-Stage Pipeline (`SupportTriageModule.cs`)

```csharp
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public override async Task<DraftReply> ForwardAsync(TicketInput input, ...)
    {
        var classification = await _classify.PredictAsync(input, trace: Trace, ...);
        var reply = await _draft.PredictAsync(classification, trace: Trace, ...);
        return reply;
    }
}
```

Two things matter for GEPA:
- **`trace: Trace`** — Every predictor call is recorded in the module's trace. GEPA reads these traces to understand exactly what each predictor received and produced.
- **Two named predictors** (`classify` and `draft`) — GEPA evolves each predictor's instruction independently, so a failure in classification doesn't blindly rewrite the drafting instruction.

### 3. GEPA Configuration (`Program.cs`)

```csharp
var gepa = new GEPA(
    reflectionClient: reflectionClient,   // LLM that diagnoses failures
    maxIterations: 20,                    // evolutionary generations
    miniBatchSize: 5,                     // examples per evaluation
    mergeEvery: 5,                        // crossover interval
    seed: 42);                            // reproducibility

var gepaOptimized = await gepa.CompileAsync(gepaModule, trainSet, untypedMetric);
```

- **`reflectionClient`** — The LLM used for failure diagnosis. Can be the same model as the task client, or a cheaper one to save cost.
- **`maxIterations`** — How many mutation/merge cycles to run. More iterations = more refined instructions, but more LLM calls.
- **`miniBatchSize`** — Each iteration evaluates on a random subset, not the full training set, keeping cost manageable.
- **`mergeEvery`** — Every 5 iterations, instead of mutating, GEPA picks two diverse Pareto-optimal parents and merges their instructions.

### 4. Reflection: How GEPA Diagnoses Failures (`GEPA.cs`)

The heart of GEPA is the `ReflectAndMutate` loop inside the optimizer:

1. **Select** a candidate from the Pareto frontier
2. **Evaluate** it on a random mini-batch, capturing full execution traces
3. **Filter** for failures (score < 0.8)
4. **Build a reflection prompt** showing the predictor's current instruction, the inputs it received, and the outputs it produced
5. **Ask the reflection LLM**: *"Based on these failures, propose an improved instruction"*
6. **Replace** the predictor's instruction with the LLM's suggestion

This is fundamentally different from Bayesian search — GEPA doesn't just know the score was low, it knows *why* (e.g., "the classify predictor labeled a billing ticket as 'general'").

### 5. Pareto Selection (`ParetoFrontier.cs`)

The Pareto frontier keeps every candidate that isn't strictly dominated by another:

- **Dominance:** Candidate A dominates B if A scores ≥ B on every example *and* strictly better on at least one.
- **Diversity:** When selecting parents for merge, GEPA picks the pair with maximum score disagreement — ensuring crossover combines genuinely different strategies.
- **Best:** At the end, the candidate with the highest average score is returned.

### 6. The Metric (`Program.cs`)

The sample uses a composite metric (0–1 scale):

| Component | Weight | What it checks |
|---|---|---|
| Category match | 0.4 | Reply mentions the correct category (billing, technical, account, general) |
| Keyword overlap | 0.4 | Reply covers key terms from the reference answer |
| Tone | 0.2 | Reply starts with a professional greeting |

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — GEPA Evolutionary Optimizer (Azure OpenAI)   ║
╚══════════════════════════════════════════════════════╝

Step 1: Baseline Evaluation
  Score: 45.0%

Step 2: Original Instructions (before GEPA)
  [classify] "Classify a support ticket by category and urgency"
  [draft]    "Draft a helpful reply to the customer based on the tic..."

Step 3: GEPA Evolutionary Optimization
  Score: 72.0%

Step 4: Evolved Instructions (after GEPA)
  [classify] "Classify each support ticket into exactly one category:
              billing, technical, account, security, or feature..."
  [draft]    "Draft a professional, empathetic reply that begins with
              'Thank you' and explicitly references the ticket category..."

Step 5: MIPROv2 for Comparison
  Score: 65.0%

╔══════════════════════════════════════════════════════╗
║   Results Comparison                                 ║
║   Baseline:              45.0%                       ║
║   GEPA (evolutionary):   72.0%                       ║
║   MIPROv2 (Bayesian):    65.0%                       ║
╚══════════════════════════════════════════════════════╝
```

> Exact scores will vary by model and run. The key observation is that GEPA produces **targeted, explainable** instruction changes — you can read the before/after and understand exactly what improved.

## Key Takeaways

1. **Trace-driven feedback beats score-only feedback.** GEPA sees *why* something failed, not just *that* it failed.
2. **The Pareto frontier preserves diversity.** Instead of converging on one "best" instruction, GEPA maintains a population of non-dominated candidates that excel on different subsets.
3. **Merge combines the best of both worlds.** Crossover lets GEPA inherit strong classify instructions from one parent and strong draft instructions from another.
4. **GEPA and MIPROv2 are complementary.** GEPA excels at instruction refinement; MIPROv2 excels at joint instruction + demo search. Use them in sequence for best results.
5. **The reflection client can be a cheaper model.** Failure diagnosis doesn't need the most powerful model — use a smaller deployment to reduce cost.

## Next Steps

| Sample | What it adds |
|---|---|
| [Z3](../LMP.Samples.Z3/) | Constraint-based reasoning with the Z3 theorem prover |
| [AdvancedOptimizers](../LMP.Samples.AdvancedOptimizers/) | Combining multiple optimizers in pipelines |
