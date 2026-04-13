# 🏢 Facility Support — GEPA Evolutionary Optimization on Enterprise Multi-Task

| | |
|---|---|
| **Technique** | GEPA evolutionary optimization with LLM reflection |
| **Difficulty** | Advanced |
| **Dataset** | FacilitySupportAnalyzer (Meta, public) |
| **Expected improvement** | ~75% → ~85-87% (+10-12pp combined) |

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

  Loaded 66 training examples, 66 dev examples

Step 1: Baseline Evaluation
───────────────────────────
  Combined Score: 75.0%
    Urgency:    52/66 (78.8%)
    Sentiment:  48/66 (72.7%)
    Category:   49/66 (74.2%)

Step 3: GEPA Evolutionary Optimization
──────────────────────────────────────
  Combined Score: 86.0%
    Urgency:    60/66 (90.9%)
    Sentiment:  54/66 (81.8%)
    Category:   56/66 (84.8%)

╔══════════════════════════════════════════════════════╗
║   Results — FacilitySupportAnalyzer                   ║
╠══════════════════════════════════════════════════════╣
║   Baseline (no opt):     75.0%                    ║
║   GEPA (evolutionary):   86.0%                    ║
╚══════════════════════════════════════════════════════╝
```

*Exact numbers will vary by model and dataset.*

## Key Takeaways

- **Multi-predictor modules shine with GEPA** — each predictor gets independently evolved instructions
- **Parallel execution reduces latency** — three LLM calls run concurrently via `Task.WhenAll`
- **Reflection-based optimization is targeted** — GEPA doesn't randomly mutate; it diagnoses failures
- **Enterprise tasks need enterprise evaluation** — the combined metric ensures no sub-task is neglected
- **Real data proves real value** — the FacilitySupportAnalyzer dataset is from Meta's production environment

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
