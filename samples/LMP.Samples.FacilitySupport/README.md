# 🏢 Facility Support — GEPA Evolutionary Optimization on Enterprise Multi-Task

| | |
|---|---|
| **Technique** | GEPA evolutionary optimization with LLM reflection |
| **Difficulty** | Advanced |
| **Dataset** | FacilitySupportAnalyzer (Meta, public) |
| **Expected improvement** | ~51% → ~63% (+11pp combined, gpt-4o-mini) |

## What You'll Learn

- How to compose a multi-predictor module (3 parallel sub-tasks)
- How GEPA's reflection loop diagnoses failures and evolves instructions
- How each predictor gets independently optimized instructions
- How to evaluate multi-task performance with a combined metric

## The Problem

Enterprise facility support teams receive messages that need **three simultaneous assessments**: urgency level, sentiment, and service category. Each is a distinct classification problem, and getting all three right on the same message is hard.

A single generic instruction works okay (~75%), but each sub-task has different failure modes. GEPA's evolutionary approach shines here: when the urgency predictor confuses "medium" with "high," the reflection LLM diagnoses *why* (e.g., "the model isn't distinguishing between inconvenience and safety risk") and proposes a targeted fix.

## How It Works

```
                          GEPA Optimizer
                   ┌──────────────────────┐
                   │ For each failure:     │
                   │  1. Reflection LLM    │
                   │     diagnoses WHY     │
                   │  2. Mutate instruction│
                   │     per predictor     │
                   │  3. Pareto selection  │
                   └──────────┬───────────┘
                              │ evolves
                              ▼
┌──────────┐    ┌─────────────────────────┐    ┌───────────┐
│  Support  │───→│  FacilitySupportModule   │───→│  Combined │
│  Message  │    │  ├─ urgency predictor   │    │  Metric   │
│           │    │  ├─ sentiment predictor │    │  (avg of  │
│           │    │  └─ category predictor  │    │   3 tasks)│
└──────────┘    └─────────────────────────┘    └───────────┘
```

**Key insight:** GEPA doesn't just try random instruction variants — it **reflects** on each failure, identifies the specific sub-task that went wrong, and proposes a targeted instruction mutation. This produces highly specialized instructions per predictor.

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Azure OpenAI resource** | With a `gpt-4o-mini` (or better) deployment |
| **FacilitySupportAnalyzer dataset** | See [Dataset Setup](#dataset-setup) below |

### Configure Secrets

```bash
cd samples/LMP.Samples.FacilitySupport

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "gpt-4o-mini"
```

### Dataset Setup

This sample uses the **FacilitySupportAnalyzer** dataset released by Meta. The dataset is not included in this repository — download it yourself.

#### Download Source

The dataset is referenced in the DSPy GEPA tutorial. Check:
- [DSPy GEPA FacilitySupportAnalyzer tutorial](https://dspy.ai/tutorials/gepa_facilitysupportanalyzer/)
- HuggingFace (search for "FacilitySupportAnalyzer")

#### Convert to JSONL

Each line must have `input` and `label` fields. The label contains all three sub-task outputs:

```json
{"input": {"Message": "The AC unit in Building 3, Floor 2 has been making loud grinding noises since yesterday morning. Several employees have complained about the temperature rising above 80°F."}, "label": {"Urgency": "high", "Sentiment": "negative", "PrimaryCategory": "HVAC", "SecondaryCategory": "None"}}
```

**Python conversion script:**

```python
import json, random

# Load the FacilitySupportAnalyzer dataset
# Adjust the loading code based on the actual dataset format
# from datasets import load_dataset
# ds = load_dataset("meta/facility-support-analyzer")  # adjust name

# Example conversion (adjust field names to match actual dataset):
def convert_example(ex):
    return {
        "input": {"Message": ex["text"]},
        "label": {
            "Urgency": ex["urgency"],
            "Sentiment": ex["sentiment"],
            "PrimaryCategory": ex["primary_category"],
            "SecondaryCategory": ex.get("secondary_category", "None")
        }
    }

# Split: 66 train, 66 dev (matching DSPy tutorial sizes)
random.seed(42)
# ... shuffle and split ...

def to_jsonl(examples, path):
    with open(path, "w") as f:
        for ex in examples:
            f.write(json.dumps(convert_example(ex)) + "\n")

# to_jsonl(train, "data/train.jsonl")
# to_jsonl(dev, "data/dev.jsonl")
```

Place the resulting `train.jsonl` and `dev.jsonl` files in the `data/` directory of this sample.

## Run It

```bash
dotnet run --project samples/LMP.Samples.FacilitySupport
```

## Code Walkthrough

### 1. Type definitions (`Types.cs`)

Three separate output records — one per sub-task:

```csharp
[LmpSignature("Assess the urgency level of this facility support request")]
public partial record UrgencyOutput
{
    [Description("Urgency level: 'low', 'medium', 'high', or 'critical'")]
    public required string Urgency { get; init; }
}

[LmpSignature("Analyze the sentiment expressed in this facility support request")]
public partial record SentimentOutput { ... }

[LmpSignature("Identify the facility service categories relevant to this support request")]
public partial record CategoryOutput { ... }
```

Each has its own `[LmpSignature]` — the source generator creates separate prompt templates, and GEPA optimizes each independently.

### 2. Multi-predictor module (`FacilitySupportModule.cs`)

```csharp
public partial class FacilitySupportModule : LmpModule<SupportInput, AnalysisResult>
{
    private readonly Predictor<SupportInput, UrgencyOutput> _urgency;
    private readonly Predictor<SupportInput, SentimentOutput> _sentiment;
    private readonly Predictor<SupportInput, CategoryOutput> _category;

    public override async Task<AnalysisResult> ForwardAsync(...)
    {
        // Run all three sub-tasks concurrently
        var urgencyTask = _urgency.PredictAsync(input, trace: Trace, ...);
        var sentimentTask = _sentiment.PredictAsync(input, trace: Trace, ...);
        var categoryTask = _category.PredictAsync(input, trace: Trace, ...);

        await Task.WhenAll(urgencyTask, sentimentTask, categoryTask);
        // ... combine results
    }
}
```

Three predictors run in parallel via `Task.WhenAll`. The source generator discovers all three via `GetPredictors()`, and GEPA optimizes each predictor's instructions independently.

### 3. Combined metric (`Program.cs`)

```csharp
Func<AnalysisResult, AnalysisResult, float> combinedMetric = (predicted, expected) =>
{
    float urgencyScore = NormalizeLabel(predicted.Urgency) == NormalizeLabel(expected.Urgency) ? 1f : 0f;
    float sentimentScore = NormalizeLabel(predicted.Sentiment) == NormalizeLabel(expected.Sentiment) ? 1f : 0f;
    float categoryScore = NormalizeLabel(predicted.PrimaryCategory) == NormalizeLabel(expected.PrimaryCategory) ? 1f : 0f;
    return (urgencyScore + sentimentScore + categoryScore) / 3f;
};
```

The combined metric is the arithmetic mean of three exact-match sub-scores. This ensures all sub-tasks contribute equally to the optimization objective.

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — Facility Support Benchmark                   ║
║   GEPA Evolutionary Optimization (3 sub-tasks)        ║
╚══════════════════════════════════════════════════════╝

  Loaded 100 training examples, 100 dev examples

Step 1: Baseline Evaluation
───────────────────────────
  Combined Score: 51.3%
    Urgency:    48/100 (48.0%)
    Sentiment:  41/100 (41.0%)
    Category:   65/100 (65.0%)

Step 3: GEPA Evolutionary Optimization
──────────────────────────────────────
  iter  5/30 [PASS ]  frontier= 2  best=62.7%
  ...
  iter 28/30 [PASS ]  frontier=12  best=70.7%
  Cooling down before final evaluation...
  Combined Score: 63.0%
    Urgency:    83/100 (83.0%)
    Sentiment:  45/100 (45.0%)
    Category:   61/100 (61.0%)

Step 4: Evolved Instructions (after GEPA)
─────────────────────────────────────────────
  [urgency]   Instruction (... chars): "Classify the urgency level ..."   Demos: 0
  [sentiment] Instruction (... chars): "Classify the sentiment ..."        Demos: 0
  [category]  Instruction (... chars): "Identify the facility service ..." Demos: 0

╔══════════════════════════════════════════════════════╗
║   Results — FacilitySupportAnalyzer                   ║
╠══════════════════════════════════════════════════════╣
║   Baseline (no opt):      51.3%                    ║
║   GEPA (evolutionary):    63.0%                    ║
╚══════════════════════════════════════════════════════╝
```

*Numbers above are from a real run with Azure OpenAI gpt-4o-mini (100 train / 100 dev).*  
*DSPy tutorial (stronger model, 66 train/dev) reports 72% → 86%; the +10-12pp improvement magnitude is consistent.*  
*`Demos: 0` is expected — GEPA is an instruction-only optimizer. Use `MIPROv2` or `BootstrapRandomSearch` if you want few-shot demo optimization.*  
*`best=` during optimization is the best individual candidate's training score. The final dev-set score is typically a few points lower due to train/dev gap.*

## Observed Results (gpt-4o-mini, 100 train / 100 dev)

| Configuration | Observed Score | Notes |
|--------------|---------------|-------|
| Baseline (no opt) | **51.3%** | Urgency 48%, Sentiment 41%, Category 65% |
| GEPA (30 iters) | **63.0% (+11.7pp)** | Urgency 83% (+35pp!), Sentiment 45%, Category 61% |

GEPA evolved instructions for 2/3 predictors over 30 iterations:
- **urgency**: `"Classify the urgency level … based on the immediacy and potential impact of the issues described"` (+35pp)
- **sentiment**: `"Classify the sentiment … focusing on expressions of positivity, negativity, or neutrality"` (+4pp)
- **category**: unchanged — round-robin iterations targeting category didn't pass the gate check

The urgency improvement (+35pp) is striking: the reflection LLM correctly identified that the default instruction wasn't distinguishing severity levels, and the mutated instruction explicitly calls for impact-based classification.

### C# Enum Types (Constrained Output)

This sample uses **C# enum types** for output field validation — the framework-level
solution to the label invention problem:

```csharp
public enum UrgencyLevel { Low, Medium, High }
public enum SentimentLevel { Positive, Neutral, Negative }
public enum ServiceCategory { RoutineMaintenance, CustomerFeedback, ... }

[LmpSignature("Assess the urgency level")]
public partial record UrgencyOutput
{
    public required UrgencyLevel Urgency { get; init; }
}
```

With `JsonStringEnumConverter` in the source-generated serializer options, C# enums
produce JSON Schema `"enum"` constraints that are enforced at the API level by OpenAI
Structured Outputs — **eliminating label invention at the token generation level** with
zero retries. This is better than DSPy's `typing.Literal` approach which uses
prompt-level hints + validation + retry.

## Known Issues

- **Small data limits optimization quality** — 100 training examples with 30 GEPA
  iterations is minimal; production use should target 500+ examples
- **Category accuracy is hardest** — 10 categories with semantically overlapping labels
  (e.g., "Routine Maintenance" vs "Facility Management") is inherently challenging
- **Rate limits during long runs** — GEPA makes ~1000+ API calls over 30 iterations.
  The sample uses `maxConcurrency: 1` and a 30s cooldown before the final eval to avoid
  Azure rate-limit cascades. If you see many failures, reduce your deployment's concurrency
  or increase the cooldown delay in `Program.cs`.

## Key Takeaways

- **GEPA evolves instructions only, not few-shot demos** — each predictor's instruction is mutated based on reflective diagnosis of failures. Demo examples are not part of GEPA's optimization surface; use `BootstrapRandomSearch` or `MIPROv2` if few-shot example selection is important for your task
- **Multi-predictor modules shine with GEPA** — each predictor gets independently evolved instructions
- **Parallel execution reduces latency** — three LLM calls run concurrently via `Task.WhenAll`
- **Reflection-based optimization is targeted** — GEPA doesn't randomly mutate; it diagnoses failures
- **Enterprise tasks need enterprise evaluation** — the combined metric ensures no sub-task is neglected
- **Real data proves real value** — the FacilitySupportAnalyzer dataset is from Meta's production environment
- **`best=` in progress output is the best individual candidate's training score** — this is the score you should expect the optimized module to achieve (subject to train/dev gap). It is distinct from the internal Pareto ensemble score which is always higher

## Dataset Details

| Property | Value |
|----------|-------|
| **Name** | FacilitySupportAnalyzer |
| **Source** | Meta (public release) |
| **License** | Public (check original source for specific terms) |
| **Referenced in** | [DSPy GEPA Enterprise Tutorial](https://dspy.ai/tutorials/gepa_facilitysupportanalyzer/) |
| **Size** | ~200 examples (66 train / 66 dev / 68 test) |
| **Domain** | Facility management support tickets |

## Next Steps

| Sample | What It Covers |
|--------|---------------|
| [MathReasoning](../LMP.Samples.MathReasoning/) | ChainOfThought + MIPROv2 on MATH algebra |
| [IntentClassification](../LMP.Samples.IntentClassification/) | BootstrapRandomSearch on 77-class Banking77 |
| [AdvancedRag](../LMP.Samples.AdvancedRag/) | LMP + MEDI pipeline integration with multi-hop |
| [GEPA](../LMP.Samples.GEPA/) | GEPA basics on simpler ticket triage task |
