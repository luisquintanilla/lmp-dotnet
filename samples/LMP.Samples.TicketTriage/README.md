# 🎫 Ticket Triage — Your First LMP Program

| Technique | Difficulty |
|-----------|------------|
| End-to-End LMP Workflow | ⭐ Beginner |

---

## What You'll Learn

- **Define typed signatures** — how LMP turns plain C# records into LLM prompts automatically.
- **Compose predictors into modules** — chain multiple LLM calls (classify → draft) into a reusable pipeline.
- **Evaluate and optimize** — measure accuracy with `Evaluator`, then improve it with `BootstrapRandomSearch`.
- **Save and reload** — persist optimized few-shot demos so you don't re-optimize every run.

---

## The Problem

Imagine you run a SaaS product and your support inbox is flooded with tickets:

> *"I was charged twice on my last invoice"*
> *"Production API returning 503 for all customers"*
> *"Please add dark mode to the dashboard"*

Each ticket needs to be **classified** (billing? technical? security?) and **assigned an urgency** (1–5). Then a **draft reply** must be written for the support agent.

Doing this manually doesn't scale. Doing it with a single prompt is fragile. This sample shows how LMP structures the task into composable, optimizable steps — the same way you'd decompose any software problem.

---

## How It Works

```
                        ┌─────────────────────────┐
                        │     Support Ticket       │
                        │  "Charged twice on my    │
                        │   last invoice"          │
                        └───────────┬─────────────┘
                                    │
                        ┌───────────▼─────────────┐
                Step 1  │  Predictor<TicketInput,  │
               Classify │    ClassifyTicket>       │
                        │  ───────────────────     │
                        │  Category: "billing"     │
                        │  Urgency:  4             │
                        └───────────┬─────────────┘
                                    │
                        ┌───────────▼─────────────┐
                Step 2  │  Predictor<ClassifyTicket│
                 Draft  │    DraftReply>           │
                        │  ───────────────────     │
                        │  "Thank you for reaching │
                        │   out about your billing │
                        │   concern..."            │
                        └───────────┬─────────────┘
                                    │
          ┌─────────────────────────┼─────────────────────────┐
          │                         │                         │
 ┌────────▼────────┐   ┌───────────▼──────────┐   ┌─────────▼─────────┐
 │   Evaluator      │   │ BootstrapRandom-     │   │  Save / Load      │
 │   (dev.jsonl)    │   │ Search (train.jsonl) │   │  (JSON artifact)  │
 │   Measures score │   │ Finds best few-shot  │   │  Persist & reuse  │
 └──────────────────┘   │ demos automatically  │   └───────────────────┘
                        └──────────────────────┘
```

The sample walks through six steps, each building on the last:

1. **Single prediction** — call a `Predictor` directly to classify one ticket.
2. **Chain of Thought** — swap in `ChainOfThought` for better reasoning, same types.
3. **Module composition** — `SupportTriageModule` chains classify → draft in `ForwardAsync`.
4. **Evaluation** — score the module against a labeled dev set using a custom metric.
5. **Optimization** — `BootstrapRandomSearch` selects the best few-shot demos from training data.
6. **Save & Load** — persist the optimized parameters and verify they reload correctly.

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10 SDK** | `dotnet --version` should report `10.0.x`. See [global.json](../../global.json). |
| **Azure OpenAI resource** | A deployed model (e.g. `gpt-4.1-nano`). |
| **Azure credentials** | The sample uses `DefaultAzureCredential` — run `az login` or use a managed identity. |

### Configure user secrets

From the sample directory:

```bash
cd samples/LMP.Samples.TicketTriage

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
```

> **⚠️ Replace `YOUR_RESOURCE`** with your actual Azure OpenAI resource name. Never commit credentials to source control.

---

## Run It

```bash
dotnet run --project samples/LMP.Samples.TicketTriage
```

Or, from the sample directory:

```bash
dotnet run
```

---

## Code Walkthrough

### 1. Define typed signatures (`Types.cs`)

LMP generates LLM prompts from your C# types. The `[Description]` attributes become field instructions in the prompt, and `[LmpSignature]` sets the task description.

```csharp
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

**Why this matters:** You never write prompt text. The source generator reads your types and descriptions to build structured prompts. Change the `Description` and the prompt updates automatically.

### 2. Use a Predictor (`Program.cs`)

A `Predictor<TInput, TOutput>` is the atomic building block — one LLM call, strongly typed:

```csharp
var classifier = new Predictor<TicketInput, ClassifyTicket>(client);
var result = await classifier.PredictAsync(
    new TicketInput("I was charged twice on my last invoice"));

Console.WriteLine($"Category: {result.Category}");  // "billing"
Console.WriteLine($"Urgency:  {result.Urgency}");   // 4
```

### 3. Upgrade to Chain of Thought (`Program.cs`)

Want the model to reason step-by-step before answering? Swap `Predictor` for `ChainOfThought` — same types, better reasoning:

```csharp
var cotClassifier = new ChainOfThought<TicketInput, ClassifyTicket>(client);
```

No prompt rewriting needed. LMP adds the reasoning scaffolding for you.

### 4. Compose into a Module (`SupportTriageModule.cs`)

An `LmpModule` chains multiple predictors into a pipeline with validation:

```csharp
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input, CancellationToken cancellationToken = default)
    {
        // Step 1: Classify with a validation assertion
        var classification = await _classify.PredictAsync(
            input, trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"),
            cancellationToken: cancellationToken);

        // Step 2: Draft a reply based on the classification
        var reply = await _draft.PredictAsync(
            classification, trace: Trace,
            cancellationToken: cancellationToken);

        return reply;
    }
}
```

**Key patterns:**
- `trace: Trace` — enables LMP's optimization to observe each predictor's inputs/outputs.
- `validate:` — `LmpAssert` retries the LLM call if the output fails validation (e.g. urgency out of range).
- The output of `_classify` flows directly as input to `_draft` — typed composition.

### 5. Evaluate with a metric (`Program.cs`)

`Evaluator.EvaluateAsync` runs the module over a labeled dataset and scores each prediction:

```csharp
var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));

Func<DraftReply, DraftReply, float> metric = (prediction, label) =>
{
    float score = 0f;
    // 0.4 — category detection via key phrases
    // 0.4 — keyword overlap with expected reply
    // 0.2 — professional tone (opens with greeting)
    return score;
};

var baseline = await Evaluator.EvaluateAsync(module, devSet, metric);
```

The metric returns a `float` from 0 to 1. You decide what "good" means for your use case.

### 6. Optimize with BootstrapRandomSearch (`Program.cs`)

The optimizer automatically selects few-shot examples from training data that maximize your metric:

```csharp
var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));

var optimizer = new BootstrapRandomSearch(
    numTrials: 8,        // try 8 different demo combinations
    maxDemos: 4,          // up to 4 few-shot examples per predictor
    metricThreshold: 0.3f // demos must score above this to be included
);

var optimized = await optimizer.CompileAsync(module, trainSet, untypedMetric);
```

**What happens under the hood:** The optimizer runs your module on training examples, collects the traces (input/output pairs at each predictor), and selects the combination of few-shot demos that produces the highest dev set score.

---

## Expected Output

```
╔══════════════════════════════════════════════╗
║   LMP — Ticket Triage Demo (Azure OpenAI)   ║
╚══════════════════════════════════════════════╝

  Using: gpt-4.1-nano @ https://YOUR_RESOURCE.openai.azure.com/

Step 1: Single Prediction
─────────────────────────
  Category: billing
  Urgency:  4

Step 2: Chain of Thought
────────────────────────
  Category: billing
  Urgency:  4

Step 3: Module Composition (SupportTriageModule)
────────────────────────────────────────────────
  Reply: Thank you for reporting this technical issue. Our engineering team
         is investigating the production API outage affecting all customers.

Step 4: Evaluate Baseline
─────────────────────────
  Examples:      25
  Average score: 52.8%
  Min score:     20.0%
  Max score:     80.0%

Step 5: Optimize with BootstrapRandomSearch (8 trials)
──────────────────────────────────────────────────────
  Optimized average score: 71.6%
  Demos in classify: 3
  Demos in draft:    4

Step 6: Save & Load
────────────────────
  Saved to: C:\Users\you\AppData\Local\Temp\triage-optimized.json
  Loaded module score: 71.6%

╔══════════════════════════════════════════════╗
║   Demo Complete!                             ║
╚══════════════════════════════════════════════╝
```

> **Note:** Actual scores vary between runs due to LLM non-determinism. The key observation is that the optimized score should be meaningfully higher than the baseline.

---

## Key Takeaways

- **Types are your prompt.** `[Description]` and `[LmpSignature]` attributes replace hand-written prompt strings. Change the type, the prompt updates.
- **Predictors compose.** The output type of one predictor can be the input type of the next — just like functions.
- **Modules are testable pipelines.** `LmpModule<TIn, TOut>` gives you a `ForwardAsync` you can evaluate, optimize, and serialize.
- **Optimization is automatic.** `BootstrapRandomSearch` finds few-shot demos that improve your metric — no manual prompt engineering.
- **Assertions keep outputs safe.** `LmpAssert` validates LLM outputs at runtime and retries on failure, so downstream code can trust the data.

---

## Next Steps

| Sample | What it covers |
|--------|---------------|
| [Evaluation](../LMP.Samples.Evaluation/) | Deep dive into metrics, per-example scoring, and evaluation strategies. |
| [MIPROv2](../LMP.Samples.MIPROv2/) | Advanced optimizer that tunes both instructions and few-shot demos. |
| [Middleware](../LMP.Samples.Middleware/) | Add logging, caching, and retry policies to the LLM pipeline. |
| [RAG](../LMP.Samples.Rag/) | Retrieval-augmented generation — ground replies in knowledge base documents. |
| [Advanced Optimizers](../LMP.Samples.AdvancedOptimizers/) | Compare BootstrapFewShot, BootstrapRandomSearch, and more. |
