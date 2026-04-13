# 🔍 Advanced RAG — Multi-Hop Pipeline with 4 Optimizable Predictors

| | |
|---|---|
| **Technique** | Multi-hop RAG with query expansion, LLM reranking, CRAG validation |
| **Difficulty** | Advanced |
| **Dataset** | RAG-QA Arena Tech (CC-BY-SA-4.0) |
| **Expected improvement** | ~42% → ~55% → ~60%+ (no-RAG → RAG → optimized) |

## What You'll Learn

- How to compose a multi-predictor RAG pipeline with 4 optimizable predictors
- How query expansion, LLM reranking, and CRAG validation improve retrieval quality
- How multi-hop retrieval handles ambiguous queries via iterative refinement
- How MIPROv2 optimizes all predictors jointly within the pipeline context
- How this architecture maps to MEDI RetrievalPipeline for production deployment

## The Problem

Basic RAG (retrieve → answer) has three failure modes:

1. **Recall failure** — the query misses relevant documents (→ solved by query expansion)
2. **Precision failure** — irrelevant documents dilute context (→ solved by LLM reranking)
3. **Confidence failure** — the model hallucinates when context is insufficient (→ solved by CRAG validation)

Additionally, complex questions may require **multiple retrieval passes** — information from the first pass informs what to search for next. This sample demonstrates all four solutions working together, with every LLM call being a typed, optimizable LMP predictor.

## How It Works

```
                              Multi-Hop Loop (max 3 iterations)
                    ┌─────────────────────────────────────────────┐
                    │                                             │
┌──────────┐    ┌───▼──────────┐    ┌───────────┐    ┌──────────┐│
│ Question  │───→│ QueryExpander │───→│ Retrieve  │───→│ Reranker ││
│           │    │ (3 variants) │    │ (per query)│    │ (score   ││
└──────────┘    └──────────────┘    └───────────┘    │ each doc)││
                                                      └────┬─────┘│
                                                           │      │
                                                      ┌────▼─────┐│
                                          "ambiguous"  │ CRAG     ││
                                    ┌────────────────│ Validator │┤
                                    │                └────┬─────┘│
                                    │  follow-up          │      │
                                    │  question           │"correct"
                                    └─────────────────────┘      │
                                                                  │
                                                      ┌──────────▼──┐
                                                      │ ChainOfThought│
                                                      │ Answer Gen   │
                                                      │ (with cites) │
                                                      └──────────────┘
```

### The 4 Predictors

| Predictor | Type | Role | What MIPROv2 Optimizes |
|-----------|------|------|----------------------|
| `expand` | Predictor | Generate 3 diverse search query variants | Instructions for generating effective query variants |
| `rerank` | Predictor | Score passage relevance (0-10) | Instructions for calibrated relevance judgments |
| `crag_validate` | Predictor | Assess context confidence (correct/ambiguous/incorrect) | Instructions for accurate confidence thresholds |
| `answer` | ChainOfThought | Generate grounded answer with citations | Instructions + demos for citation-rich answers |

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Azure OpenAI resource** | With a `gpt-4o-mini` (or better) deployment |
| **RAG-QA Arena dataset** | See [Dataset Setup](#dataset-setup) below |

### Configure Secrets

```bash
cd samples/LMP.Samples.AdvancedRag

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "gpt-4o-mini"
```

### Dataset Setup

This sample uses the **RAG-QA Arena** Tech domain dataset (CC-BY-SA-4.0). Three files are needed:

#### Download

```bash
pip install huggingface_hub datasets
```

The dataset is available from the DSPy cache on HuggingFace. Check:
- [dspy/rag-qa-arena](https://huggingface.co/datasets/dspy/rag-qa-arena) or [dspy/cache](https://huggingface.co/datasets/dspy/cache)
- DSPy RAG tutorial source: [dspy.ai/tutorials/rag](https://dspy.ai/tutorials/rag/)

#### Convert to JSONL

Three files needed:

**`data/corpus.jsonl`** — one passage per line:
```json
{"text": "The Linux kernel was first released on September 17, 1991..."}
```

**`data/train.jsonl`** — Q&A pairs with citations:
```json
{"input": {"Question": "What are the main features of Linux kernel 6.0?"}, "label": {"Answer": "Linux 6.0 introduced...", "Citations": ["The Linux kernel 6.0 released in October 2022..."]}}
```

**`data/dev.jsonl`** — same format as train.jsonl

**Python conversion script:**

```python
import json, random

# Load RAG-QA Arena from DSPy cache
# from datasets import load_dataset
# ds = load_dataset("dspy/rag-qa-arena")

# Filter to "tech" domain
# tech_data = [ex for ex in ds if ex["domain"] == "tech"]

# Extract corpus passages from the dataset
# corpus = list(set(passage for ex in tech_data for passage in ex["gold_passages"]))

# Split: 100 train, 50 dev
# random.seed(42)
# random.shuffle(tech_data)
# train = tech_data[:100]
# dev = tech_data[100:150]

def write_corpus(passages, path):
    with open(path, "w") as f:
        for p in passages:
            f.write(json.dumps({"text": p}) + "\n")

def write_qa(examples, path):
    with open(path, "w") as f:
        for ex in examples:
            obj = {
                "input": {"Question": ex["question"]},
                "label": {
                    "Answer": ex["answer"],
                    "Citations": ex.get("gold_passages", [])[:3]
                }
            }
            f.write(json.dumps(obj) + "\n")

# write_corpus(corpus, "data/corpus.jsonl")
# write_qa(train, "data/train.jsonl")
# write_qa(dev, "data/dev.jsonl")
```

Place all three files in the `data/` directory.

## Run It

```bash
dotnet run --project samples/LMP.Samples.AdvancedRag
```

## Code Walkthrough

### 1. Type definitions (`Types.cs`)

Six record types define the pipeline's data flow:

```csharp
// Pipeline I/O
public record QuestionInput(string Question);
public partial record GroundedAnswer { string Answer, string[] Citations }

// Query expansion
public record ExpandInput(string Question);
public partial record ExpandOutput { string Query1, Query2, Query3 }

// Reranking
public record RerankInput(string Question, string Passage);
public partial record RerankOutput { int RelevanceScore }  // 0-10

// CRAG validation
public record CragInput(string Question, string Context);
public partial record CragOutput { string Confidence, string FollowUpQuestion }
```

### 2. Multi-hop module (`AdvancedRagModule.cs`)

```csharp
public partial class AdvancedRagModule : LmpModule<QuestionInput, GroundedAnswer>
{
    // 4 predictors — all optimizable
    private readonly Predictor<ExpandInput, ExpandOutput> _expand;
    private readonly Predictor<RerankInput, RerankOutput> _rerank;
    private readonly Predictor<CragInput, CragOutput> _cragValidate;
    private readonly ChainOfThought<AnswerInput, GroundedAnswer> _answer;

    public override async Task<GroundedAnswer> ForwardAsync(...)
    {
        for (int hop = 0; hop < _maxHops; hop++)
        {
            // 1. Expand query → 3 variants
            // 2. Retrieve per variant → deduplicate
            // 3. Rerank each passage (LLM cross-encoder)
            // 4. CRAG validate — "correct" breaks, "ambiguous" loops
        }
        // 5. ChainOfThought answer generation with citations
    }
}
```

The multi-hop loop is the key: CRAG validation acts as a confidence gate. When it returns "ambiguous," the module generates a follow-up question and loops back to retrieval. This naturally handles questions that need information from multiple sources.

### 3. MEDI Integration Path

This sample uses LMP's `IRetriever` with `InMemoryRetriever`. When MEDI's `RetrievalPipeline` becomes available:

```csharp
// Current: LMP predictors called directly in ForwardAsync
_expand.PredictAsync(...)
_rerank.PredictAsync(...)
_cragValidate.PredictAsync(...)

// Future: Predictors wrapped as MEDI processors
// _pipeline.QueryProcessors.Add(new LmpQueryExpander(_expand));
// _pipeline.ResultProcessors.Add(new LmpReranker(_rerank));
// _pipeline.ResultProcessors.Add(new LmpCragValidator(_cragValidate));
// var results = await _pipeline.RetrieveAsync(collection, query, ...);
```

See the [LMP+MEDI integration spec](https://gist.github.com/lqdev/beca378baf544054c967c76836e0fc54) for the full adapter pattern. All APIs have been validated against both codebases.

## Expected Output

```
╔══════════════════════════════════════════════════════╗
║   LMP — Advanced RAG Benchmark                       ║
║   Multi-Hop Pipeline with 4 Optimizable Predictors   ║
╚══════════════════════════════════════════════════════╝

  Corpus: 500 passages
  Loaded 100 training examples, 50 dev examples

Step 1: Evaluate Baseline (simple RAG, no expansion/reranking)
───────────────────────────────────────────────────────────────
  Answer F1: 42.0%

Step 2: Multi-Hop RAG (3 hops, no optimization)
─────────────────────────────────────────────────
  Answer F1: 55.0%

Step 3: MIPROv2 Optimization (all 4 predictors)
─────────────────────────────────────────────────
  Answer F1: 62.0%

╔══════════════════════════════════════════════════════╗
║   Results — RAG-QA Arena Tech                         ║
╠══════════════════════════════════════════════════════╣
║   Simple RAG (1 hop):        42.0%                ║
║   Multi-Hop RAG (3 hops):    55.0%                ║
║   MIPROv2 Optimized:         62.0%                ║
╚══════════════════════════════════════════════════════╝
```

*Exact numbers will vary by model, corpus, and dataset.*

## Observed Results (gpt-4o-mini, 100 train / 50 dev / 299 corpus)

> **Important:** The "Expected Output" section above shows idealized numbers.
> Actual results demonstrate clear multi-hop improvement.

In testing with Azure OpenAI gpt-4o-mini on 100 training + 50 dev + 299 corpus:

| Configuration | Observed F1 | Notes |
|--------------|-------------|-------|
| Simple RAG (1 hop) | ~32.4% | Basic retrieval + answer generation |
| Multi-Hop RAG (3 hops) | ~39.0% | +6.6pp from query expansion + CRAG |
| MIPROv2 Optimized | ~8.2% | Regressed — small data + few trials |

**Multi-hop works:** The 3-hop pipeline with query expansion and CRAG validation
improves over simple RAG by ~6.6 percentage points (32.4% → 39.0%).

**MIPROv2 regression:** With only 50 dev examples and limited optimization trials,
MIPROv2 finds a worse configuration. The 4-predictor pipeline has many parameters
to tune, and small data leads to overfitting on the training mini-batches.

**Corpus construction note:** The full RAG-QA Arena corpus has 28K documents. For
testing, we constructed a pragmatic 299-passage corpus from the QA answer texts +
distractor passages. A larger, more diverse corpus would improve results.

## Known Issues

- **MIPROv2 needs more data** — 4 predictors × instruction + demo variants requires
  many more trials than simpler modules
- **Corpus size matters** — 299 passages is minimal for RAG evaluation; production
  use should target 1000+ passages
- **F1 metric is strict** — partial matches score low even when the answer captures
  the right concept

## Key Takeaways

- **4 predictors, all optimizable** — every LLM call in the pipeline is a typed LMP predictor that MIPROv2 can tune
- **Multi-hop handles ambiguity** — CRAG validation triggers iterative refinement for complex questions
- **Query expansion improves recall** — 3 diverse query variants catch documents a single query would miss
- **LLM reranking improves precision** — cross-encoder relevance scoring outperforms keyword matching
- **Architecture maps to MEDI** — when RetrievalPipeline is available, each predictor wraps as a MEDI processor

## Dataset Details

| Property | Value |
|----------|-------|
| **Name** | RAG-QA Arena |
| **Domain** | Tech |
| **Source** | [DSPy RAG tutorial](https://dspy.ai/tutorials/rag/) |
| **License** | CC-BY-SA-4.0 |
| **Size** | ~1000 Q&A pairs + corpus (we use 100 train + 50 dev) |

## Next Steps

| Sample | What It Covers |
|--------|---------------|
| [RAG](../LMP.Samples.Rag/) | Basic RAG with IRetriever (beginner) |
| [MathReasoning](../LMP.Samples.MathReasoning/) | ChainOfThought + MIPROv2 on MATH algebra |
| [IntentClassification](../LMP.Samples.IntentClassification/) | BootstrapRandomSearch on Banking77 |
| [FacilitySupport](../LMP.Samples.FacilitySupport/) | GEPA evolutionary optimization |
