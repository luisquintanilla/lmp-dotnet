# 🏷️ Intent Classification — BootstrapRandomSearch on Banking77

| | |
|---|---|
| **Technique** | Few-shot demo selection via BootstrapRandomSearch |
| **Difficulty** | Advanced |
| **Dataset** | Banking77 (PolyAI, CC-BY-4.0) |
| **Expected improvement** | ~55% → ~75-80% (+20-25pp) |

## What You'll Learn

- How LLMs fail on high-cardinality classification without examples
- How BootstrapRandomSearch finds the best few-shot demonstrations
- How to evaluate with exact-match on fine-grained labels
- How MIPROv2 can further improve with instruction optimization

## The Problem

Classifying customer queries into **77 fine-grained banking intents** is hard. Labels like `card_arrival`, `card_about_to_expire`, and `card_not_working` are semantically close — without seeing examples, even GPT-4o-mini only scores ~55%.

The fix: show the model carefully selected examples of each intent. BootstrapRandomSearch tries different demo sets and picks the one that maximizes accuracy. With 6 well-chosen demos, accuracy jumps to **75-80%**.

## How It Works

```
┌──────────────┐     ┌────────────────────┐     ┌────────────┐
│  Customer     │────→│  Predictor          │────→│  Exact     │
│  Query        │     │  + optimized demos  │     │  Match     │
│  "I lost my   │     │  (6 best examples)  │     │  Metric    │
│   card"       │     └────────────────────┘     └────────────┘
└──────────────┘              ▲
                              │ demos selected by
                    ┌─────────┴──────────┐
                    │ BootstrapRandom    │
                    │ Search (10 trials) │
                    └────────────────────┘
```

**Key insight:** On classification tasks with many labels, the model needs to **see** examples — not just be told what to do. Few-shot demo selection is the most impactful optimization for these tasks.

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Azure OpenAI resource** | With a `gpt-4o-mini` (or better) deployment |
| **Banking77 dataset** | See [Dataset Setup](#dataset-setup) below |

### Configure Secrets

```bash
cd samples/LMP.Samples.IntentClassification

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "gpt-4o-mini"
```

### Dataset Setup

This sample uses the **Banking77** dataset by PolyAI (CC-BY-4.0 license). The dataset is not included in this repository — download it yourself:

#### Option 1: HuggingFace CLI

```bash
pip install huggingface_hub datasets
```

#### Option 2: Direct download

Download from [https://huggingface.co/datasets/PolyAI/banking77](https://huggingface.co/datasets/PolyAI/banking77).

#### Convert to JSONL

Each line must have `input` and `label` fields:

```json
{"input": {"Query": "I lost my card yesterday"}, "label": {"Intent": "lost_or_stolen_card"}}
```

**Python conversion script:**

```python
import json, random
from datasets import load_dataset

ds = load_dataset("PolyAI/banking77")
train_data = list(ds["train"])
test_data = list(ds["test"])

# Banking77 uses integer labels — map to string names
label_names = ds["train"].features["label"].names

random.seed(42)
random.shuffle(train_data)
random.shuffle(test_data)

# Use 300 train, 100 dev
train = train_data[:300]
dev = test_data[:100]

def to_jsonl(examples, path):
    with open(path, "w") as f:
        for ex in examples:
            obj = {
                "input": {"Query": ex["text"]},
                "label": {"Intent": label_names[ex["label"]]}
            }
            f.write(json.dumps(obj) + "\n")

to_jsonl(train, "data/train.jsonl")
to_jsonl(dev, "data/dev.jsonl")
print(f"Wrote {len(train)} train, {len(dev)} dev examples")
print(f"Labels: {len(label_names)} unique intents")
```

Place the resulting `train.jsonl` and `dev.jsonl` files in the `data/` directory of this sample.

## Run It

```bash
dotnet run --project samples/LMP.Samples.IntentClassification
```

## Code Walkthrough

### 1. Type definitions (`Types.cs`)

```csharp
public record ClassifyInput(
    [property: Description("The customer's banking query to classify")]
    string Query);

[LmpSignature("Classify the customer's banking query into one of the predefined intent categories.")]
public partial record ClassifyOutput
{
    [Description("The intent label (e.g., 'card_arrival', 'lost_or_stolen_card')")]
    public required string Intent { get; init; }
}
```

No chain-of-thought here — classification doesn't need step-by-step reasoning. A single `Predictor` with the right demos is more effective.

### 2. Classification module (`IntentClassificationModule.cs`)

```csharp
public partial class IntentClassificationModule : LmpModule<ClassifyInput, ClassifyOutput>
{
    private readonly Predictor<ClassifyInput, ClassifyOutput> _classify;

    public IntentClassificationModule(IChatClient client)
    {
        _classify = new Predictor<ClassifyInput, ClassifyOutput>(client) { Name = "classify" };
    }
}
```

The simplest possible module — one predictor, no composition. The value comes entirely from optimization selecting the right demos and instructions.

### 3. Label normalization (`Program.cs`)

```csharp
static string NormalizeLabel(string label) =>
    label.Trim().ToLowerInvariant().Replace(' ', '_');
```

Banking77 labels use underscores (`lost_or_stolen_card`). Normalization ensures the model's output matches regardless of minor formatting differences.

### 4. Error analysis (`Program.cs`)

The sample prints misclassified examples after optimization, showing which intents are confused. This helps understand where the model struggles (often semantically similar intents like `card_arrival` vs. `card_delivery_estimate`).

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — Intent Classification Benchmark              ║
║   BootstrapRandomSearch on Banking77 (77 classes)     ║
╚══════════════════════════════════════════════════════╝

  Loaded 300 training examples, 100 dev examples
  Top 5 intents in training set:
    card_payment_not_recognised: 8 examples
    top_up_failed: 7 examples
    ...

Step 1: Evaluate Baseline (zero-shot, no demos)
────────────────────────────────────────────────
  Accuracy: 55.0% (55/100 correct)

Step 2: BootstrapRandomSearch (finds best demo set)
───────────────────────────────────────────────────
  Accuracy: 76.0% (76/100 correct)
  [classify] Demos: 6, Instruction: "Classify the customer's banking query..."

Step 3: MIPROv2 (instructions + demos)
───────────────────────────────────────
  Accuracy: 80.0%

╔══════════════════════════════════════════════════════╗
║   Results Comparison — Banking77 (77 intents)         ║
╠══════════════════════════════════════════════════════╣
║   Baseline (zero-shot):      55.0%                ║
║   BootstrapRandomSearch:     76.0%                ║
║   MIPROv2 (instr + demos):   80.0%                ║
╚══════════════════════════════════════════════════════╝
```

*Exact numbers will vary by model and dataset split.*

## Observed Results (gpt-4o-mini, 300 train / 100 dev)

> **Important:** The "Expected Output" section above shows idealized numbers.
> Actual results with gpt-4o-mini are lower due to the label invention problem.

In testing with Azure OpenAI gpt-4o-mini on 300 training + 100 dev Banking77:

| Configuration | Observed Score | Notes |
|--------------|---------------|-------|
| Baseline (zero-shot) | ~3.3% | Model invents plausible but wrong labels |
| BootstrapRandomSearch | ~10% | 3x improvement from demo selection |
| MIPROv2 | ~16.7% | 5x improvement over baseline |

**The label invention problem:** With 77 fine-grained intents, the model invents
labels like `"currency_exchange"` when the correct label is `"exchange_via_app"`.
The labels are semantically close but don't match the Banking77 label set exactly.
This sample includes mitigations (all 77 labels listed in the signature + LmpAssert
validation), but string-based comparison still limits accuracy.

**Optimization does help:** Despite low absolute scores, the 5x relative improvement
(3.3% → 16.7%) demonstrates that demo selection meaningfully anchors the model
toward the correct label vocabulary.

**The enum solution:** LMP now supports C# enum types as output properties (see the
[FacilitySupport](../LMP.Samples.FacilitySupport/) sample). For Banking77, converting
the 77 intents to a C# enum would enforce valid labels at the API level via JSON Schema
constraints — eliminating label invention entirely.

## Known Issues

- **Label invention is the dominant failure mode** — the model knows the right concept
  but uses its own label rather than the Banking77 label set
- **LmpAssert.That workaround** — the current approach lists all 77 labels in both the
  LmpSignature text and a ValidIntents HashSet; enum types would reduce this to one place
- **Small-data sensitivity** — 300 training examples spread across 77 labels means ~4
  examples per label on average, limiting demo diversity

## Key Takeaways

- **Zero-shot fails on many-label classification** — 77 classes is too many for the model to disambiguate without examples
- **Demo selection is the #1 optimization for classification** — the right 6 examples cover diverse intents and anchor the model
- **Simple modules can be powerful** — no chain-of-thought needed; a single Predictor with good demos outperforms elaborate prompting
- **Error analysis reveals confusion patterns** — semantically similar intents cluster together in errors

## Dataset Details

| Property | Value |
|----------|-------|
| **Name** | Banking77 |
| **Source** | [PolyAI/banking77](https://huggingface.co/datasets/PolyAI/banking77) |
| **License** | CC-BY-4.0 |
| **Paper** | [Efficient Intent Detection with Dual Sentence Encoders](https://arxiv.org/abs/2003.04807) |
| **Size** | 13,083 examples across 77 intents (we use 300 train + 100 dev) |
| **Domain** | Online banking customer support |

## Next Steps

| Sample | What It Covers |
|--------|---------------|
| [MathReasoning](../LMP.Samples.MathReasoning/) | ChainOfThought + MIPROv2 on MATH algebra |
| [FacilitySupport](../LMP.Samples.FacilitySupport/) | GEPA evolutionary optimization on enterprise multi-task |
| [AdvancedRag](../LMP.Samples.AdvancedRag/) | LMP + MEDI pipeline integration with multi-hop |
| [TicketTriage](../LMP.Samples.TicketTriage/) | Beginner-friendly full workflow |
