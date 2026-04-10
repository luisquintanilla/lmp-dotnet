# Multi-Metric Evaluation

| | |
|---|---|
| **Technique** | Multi-metric evaluation with `EvaluationBridge` |
| **Difficulty** | Intermediate |
| **Time** | ~10 minutes |
| **Prerequisites** | Azure OpenAI resource with a deployed model |

## What You'll Learn

- How to measure LMP module quality using **three tiers** of evaluation metrics
- How to use **keyword metrics** for fast, LLM-free sanity checks
- How to bridge **Microsoft.Extensions.AI.Evaluation** (`M.E.AI`) evaluators into LMP's metric system
- How to run **LLM-as-judge** evaluators (Coherence, Relevance) that score your module's output on a 1–5 scale
- How to **combine** multiple metrics with custom weights into a single optimization-ready score
- How to feed bridged metrics directly into an **optimizer** like `BootstrapRandomSearch`

## The Problem

You've built an LMP module — a two-step support-ticket triage pipeline that classifies tickets and drafts customer replies. But **how do you know if it's any good?**

A single metric rarely tells the whole story:

| Question | Metric tier |
|---|---|
| "Does the reply mention the right category?" | **Keyword** — fast, deterministic, no LLM |
| "How much vocabulary overlap with the expected reply?" | **NLP** — token-level F1, BLEU, or custom `IEvaluator` |
| "Is the reply coherent and relevant to the customer?" | **LLM-as-judge** — an LLM scores the output on quality dimensions |

This sample shows you how to use all three tiers, then **combine** them into a single score that drives LMP optimization.

## How It Works

```
┌────────────────────────────────────────────────────────────────┐
│                    Evaluation Tiers                             │
├───────────────┬───────────────────┬────────────────────────────┤
│  Tier 1       │  Tier 2           │  Tier 3                    │
│  Keyword      │  NLP / Custom     │  LLM-as-Judge              │
│               │  IEvaluator       │                            │
│  • Category   │  • WordOverlap    │  • CoherenceEvaluator      │
│    match      │  • F1Evaluator    │  • RelevanceEvaluator      │
│  • Greeting   │  • BLEUEvaluator  │  • GroundednessEvaluator   │
│    check      │  • GLEUEvaluator  │  • CompletenessEvaluator   │
│               │                   │  • FluencyEvaluator        │
│  No LLM ✓     │  No LLM ✓         │  Requires LLM              │
│  Instant ✓    │  Fast ✓           │  ~1-2 sec per example      │
└───────────────┴───────────────────┴────────────────────────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  EvaluationBridge     │
              │  Normalizes all       │
              │  scores to [0, 1]     │
              │  for LMP optimizers   │
              └───────────────────────┘
```

**`EvaluationBridge`** is the adapter that converts any `M.E.AI` `IEvaluator` into an LMP-compatible `Func<Example, object, Task<float>>` metric. LLM-as-judge evaluators return scores on a 1–5 scale; the bridge normalizes them to `[0, 1]` so they work seamlessly with LMP optimizers.

## Prerequisites

1. **.NET 9 SDK** or later
2. **Azure OpenAI resource** with a deployed chat model (e.g., `gpt-4o-mini`)
3. **Azure CLI** logged in (`az login`) — the sample uses `DefaultAzureCredential`

### Configure secrets

```bash
cd samples/LMP.Samples.Evaluation

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR_DEPLOYMENT_NAME"
```

> **Security:** Never hard-code endpoints or keys. This sample uses `DefaultAzureCredential` (Azure CLI, Managed Identity, etc.) — no API key required.

## Run It

```bash
dotnet run --project samples/LMP.Samples.Evaluation
```

The sample runs five approaches sequentially:

1. **Keyword metric** — instant, no LLM calls
2. **Custom `IEvaluator` via bridge** — word overlap, no LLM calls
3. **Coherence (LLM-as-judge)** — one LLM call per example
4. **Combined multi-metric** — Coherence (60%) + Relevance (40%)
5. **Optimization** — `BootstrapRandomSearch` using the bridged metric

## Code Walkthrough

### The Module Under Test

`SupportTriageModule` is a two-step LMP pipeline:

```
TicketInput ──► Classify (category + urgency) ──► DraftReply
```

```csharp
// SupportTriageModule.cs
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    public override async Task<DraftReply> ForwardAsync(TicketInput input, ...)
    {
        var classification = await _classify.PredictAsync(input, ...);
        var reply = await _draft.PredictAsync(classification, ...);
        return reply;
    }
}
```

The dev set (`data/dev.jsonl`) contains 25 ticket/reply pairs used as ground truth for evaluation.

---

### Approach 1: Keyword Metric (No LLM)

The simplest metric — a plain `Func<DraftReply, DraftReply, float>` that checks two things:

```csharp
Func<DraftReply, DraftReply, float> keywordMetric = (prediction, label) =>
{
    float score = 0f;

    // +0.5 if the reply mentions the correct category keyword
    var categoryPhrases = new[] { "billing", "technical", "account", "security", "feature" };
    var expectedCategory = categoryPhrases
        .FirstOrDefault(c => label.ReplyText.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? "";
    if (!string.IsNullOrEmpty(expectedCategory) &&
        prediction.ReplyText.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase))
        score += 0.5f;

    // +0.5 if the reply starts with "Thank you"
    if (prediction.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase))
        score += 0.5f;

    return score;
};

var keywordResult = await Evaluator.EvaluateAsync(module, devSet, keywordMetric);
```

**When to use:** Quick smoke tests, CI gates, or guardrails that don't need LLM calls.

---

### Approach 2: Custom `IEvaluator` via `EvaluationBridge` (No LLM)

You can implement `Microsoft.Extensions.AI.Evaluation.IEvaluator` and bridge it into LMP:

```csharp
// A custom evaluator that computes word overlap between expected and predicted text
file sealed class WordOverlapEvaluator : IEvaluator
{
    public const string OverlapMetricName = "WordOverlap";

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse, ...)
    {
        // Extract keywords from expected text, compute overlap ratio
        var score = (double)matchCount / expectedWords.Count;
        return new ValueTask<EvaluationResult>(new EvaluationResult(
            new NumericMetric(OverlapMetricName, score, reason)));
    }
}
```

Bridge it into LMP with one call:

```csharp
var bridgedMetric = EvaluationBridge.CreateTypedMetric<DraftReply, DraftReply>(
    new WordOverlapEvaluator(),
    chatConfiguration: null,        // no LLM needed
    metricName: WordOverlapEvaluator.OverlapMetricName,
    maxScore: 1.0f);                // already [0, 1]

var result = await Evaluator.EvaluateAsync(module, devSet, bridgedMetric);
```

> **M.E.AI built-in NLP evaluators** — You don't have to write your own. The `Microsoft.Extensions.AI.Evaluation` package includes:
>
> | Evaluator | What it measures |
> |---|---|
> | `F1Evaluator` | Token-level precision/recall F1 between predicted and expected text |
> | `BLEUEvaluator` | BLEU score (n-gram overlap, standard MT metric) |
> | `GLEUEvaluator` | GLEU score (sentence-level variant of BLEU) |
>
> All work without an LLM — pass `chatConfiguration: null`.

---

### Approach 3: LLM-as-Judge (Coherence)

`CoherenceEvaluator` sends your module's output to an LLM that scores it 1–5 for readability and organization:

```csharp
// Create an eval client (can be the same or different model)
IChatClient evalClient = azureClient.GetChatClient(deployment).AsIChatClient();
var evalConfig = new ChatConfiguration(evalClient);

// Bridge CoherenceEvaluator into LMP — scores 1-5, normalized to [0, 1]
var coherenceMetric = EvaluationBridge.CreateMetric(
    new CoherenceEvaluator(),
    evalConfig,
    CoherenceEvaluator.CoherenceMetricName,
    maxScore: 5.0f);

var coherenceResult = await Evaluator.EvaluateAsync(module, devSet, coherenceMetric);
```

> **Other LLM-as-judge evaluators** from `Microsoft.Extensions.AI.Evaluation.Quality`:
>
> | Evaluator | Dimension |
> |---|---|
> | `CoherenceEvaluator` | Readability, logical flow |
> | `RelevanceEvaluator` | On-topic, addresses the question |
> | `GroundednessEvaluator` | Factual accuracy vs. provided context |
> | `CompletenessEvaluator` | Thoroughness of the response |
> | `FluencyEvaluator` | Grammar, naturalness |
> | `EquivalenceEvaluator` | Similarity to ground truth |

---

### Approach 4: Combined Multi-Metric

Weight multiple evaluators into a single score for optimization:

```csharp
var combinedMetric = EvaluationBridge.CreateCombinedMetric(
    [
        (new CoherenceEvaluator(), CoherenceEvaluator.CoherenceMetricName, Weight: 0.6f),
        (new RelevanceEvaluator(), RelevanceEvaluator.RelevanceMetricName, Weight: 0.4f)
    ],
    chatConfiguration: evalConfig,
    maxScore: 5.0f);

var combinedResult = await Evaluator.EvaluateAsync(module, devSet, combinedMetric);
```

The bridge computes: `score = (0.6 × coherence + 0.4 × relevance) / totalWeight`, normalized to `[0, 1]`.

---

### Approach 5: Bridged Metrics + Optimization

Any bridged metric works directly with LMP optimizers:

```csharp
var optimizer = new BootstrapRandomSearch(
    numTrials: 4, maxDemos: 3, metricThreshold: 0.1f, seed: 42);

var optimized = await optimizer.CompileAsync(optimizerModule, trainSet, syncBridgedMetric);

// Compare baseline vs. optimized
var optimizedResult = await Evaluator.EvaluateAsync(optimized, devSet, bridgedMetric);
```

This closes the loop: **evaluate → score → optimize → re-evaluate**.

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP + M.E.AI.Evaluation Integration Demo          ║
╚══════════════════════════════════════════════════════╝

Approach 1: LMP Keyword Metric (sync, no LLM)
──────────────────────────────────────────────
  Average: 75.0%
  Min/Max: 50.0% / 100.0%

Approach 2: Custom IEvaluator via EvaluationBridge
──────────────────────────────────────────────────
  Word Overlap Average: 42.0%
  Word Overlap Min/Max: 20.0% / 65.0%

Approach 3: M.E.AI Coherence Evaluator (LLM-as-judge)
──────────────────────────────────────────────────────
  Coherence Average: 88.0%
  Coherence Min/Max: 72.0% / 100.0%

Approach 4: Combined Multi-Metric (Coherence + Relevance)
──────────────────────────────────────────────────────────
  Combined Average: 85.0%

Step 5: Optimize Using M.E.AI Metric
──────────────────────────────────────
  Baseline:  42.0%
  Optimized: 58.0%
```

> **Note:** Exact scores will vary depending on the model and its responses.

## Key Takeaways

| Takeaway | Details |
|---|---|
| **Start with keyword metrics** | They're instant, free, and catch obvious failures. Use them in CI. |
| **Layer NLP metrics for coverage** | F1, BLEU, and GLEU measure textual similarity without LLM cost. |
| **Use LLM-as-judge for quality** | Coherence and Relevance capture dimensions no keyword check can. |
| **`EvaluationBridge` unifies everything** | Any `IEvaluator` becomes an LMP metric with one call — `CreateMetric()`, `CreateTypedMetric()`, or `CreateCombinedMetric()`. |
| **Combined metrics drive optimization** | Weight the dimensions that matter most, then pass the combined score straight to an optimizer. |
| **Scores are normalized to `[0, 1]`** | The bridge divides by `maxScore` so 1–5 judge scores and 0–1 NLP scores live on the same scale. |

## Next Steps

- **[MIPROv2 Sample](../LMP.Samples.MIPROv2/)** — Use multi-metric evaluation to drive instruction and few-shot optimization with the MIPROv2 optimizer.
- **[GEPA Sample](../LMP.Samples.GEPA/)** — Apply evaluation metrics within a genetic prompt-engineering loop that evolves prompts across generations.
