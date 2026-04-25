# LMP Samples

> **Language Model Programs for .NET** — learn by example, from basics to production.

## Quick Start

Every sample uses **Azure OpenAI** with `DefaultAzureCredential` (no API keys in code).

One-time setup:

```bash
# In any sample directory:
dotnet user-secrets init
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "gpt-4o-mini"
```

Then run:

```bash
dotnet run --project samples/LMP.Samples.TicketTriage
```

---

## Learning Path

Start at the top and work your way down. Each sample builds on concepts from the previous ones.

### 🟢 Beginner

| Sample | What You'll Learn |
|--------|-------------------|
| [**TicketTriage**](LMP.Samples.TicketTriage/) | The "hello world" of LMP. Modules, Predictors, Chain of Thought, BootstrapFewShot optimization, evaluation. Start here. |

### 🟡 Intermediate

| Sample | What You'll Learn |
|--------|-------------------|
| [**Agent**](LMP.Samples.Agent/) | ReAct agent with tool-calling. Think → Act → Observe loop. |
| [**RAG**](LMP.Samples.Rag/) | Retrieval-Augmented Generation. IRetriever, context injection, knowledge grounding. |
| [**Middleware**](LMP.Samples.Middleware/) | Production observability. Distributed caching, OpenTelemetry tracing, structured logging via M.E.AI middleware. |
| [**Evaluation**](LMP.Samples.Evaluation/) | Multi-metric evaluation. Keyword metrics → NLP (F1, BLEU) → LLM-as-judge (Coherence, Relevance, Groundedness). **Plus:** `EvaluationCritique` feeds M.E.AI evaluator rationale into a GEPA pipeline for targeted instruction improvement. |
| [**Modules**](LMP.Samples.Modules/) | Inference-time quality modules: `BestOfN` (N parallel, pick best by reward), `Refine` (self-critique loops), `ProgramOfThought` (LM writes + Roslyn executes C#). Plus `LmpSuggest` (soft) vs `LmpAssert` (hard) guardrails. |

### 🔴 Advanced

| Sample | What You'll Learn |
|--------|-------------------|
| [**MIPROv2**](LMP.Samples.MIPROv2/) | Bayesian instruction + demo optimization. Proposal LM generates instruction variants, TPE searches over combinations. |
| [**GEPA**](LMP.Samples.GEPA/) | Evolutionary optimization via LLM reflection. Captures failures → diagnoses → proposes fixes → evolves instructions. |
| [**Z3**](LMP.Samples.Z3/) | Constraint-based demo selection with the Z3 solver. Enforces category coverage and quality constraints. |
| [**AdvancedOptimizers**](LMP.Samples.AdvancedOptimizers/) | Pluggable search strategies: `ISearchStrategy`, `SmacSampler`, `CostAwareSampler`, `TraceAnalyzer`, warm-start transfer. **Plus:** unified `LmpPipelines.Auto()` façade — `Goal.Accuracy` (BFS → GEPA → MIPROv2 → Calibration), `Goal.Speed` (SIMBA), `Goal.Cost` (BFS → MIPROv2), `Goal.Balanced` (BFS → GEPA → Calibration). `CostBudget` caps token spend. |
| [**AutoOptimize**](LMP.Samples.AutoOptimize/) | Build-time auto-optimization. `[AutoOptimize]` → source gen → `.g.cs` artifacts → `dotnet build -p:LmpAutoOptimize=true`. |
| [**ChatClientTarget**](LMP.Samples.ChatClientTarget/) | Optimize any `IChatClient` without defining an `LmpModule`. `ChatClientTarget.For()` wraps a client with optimizable params (system prompt, temperature, tools). `ChainTarget` chains two targets end-to-end. `UseOptimized()` deploys the learned state as M.E.AI middleware. |

### 📊 Benchmarks (Real Datasets)

| Sample | What You'll Learn |
|--------|-------------------|
| [**MathReasoning**](LMP.Samples.MathReasoning/) | ChainOfThought + MIPROv2 on MATH algebra. Strong baseline (~85-90%); optimization benefits from larger data. Download [MATH dataset](https://huggingface.co/datasets/hendrycks/competition_math) (MIT). |
| [**IntentClassification**](LMP.Samples.IntentClassification/) | BootstrapRandomSearch on Banking77 (77 classes). Demonstrates 5x improvement from demo selection. Shows the label invention problem and LmpAssert mitigations. Download [Banking77](https://huggingface.co/datasets/PolyAI/banking77) (CC-BY-4.0). |
| [**FacilitySupport**](LMP.Samples.FacilitySupport/) | GEPA on enterprise multi-task with **C# enum output types** (urgency + sentiment + categories). +57% relative improvement. Shows how enum types eliminate label invention via JSON Schema enforcement. FacilitySupportAnalyzer (Meta). |
| [**AdvancedRag**](LMP.Samples.AdvancedRag/) | Multi-hop RAG with 4 optimizable predictors (expand, rerank, CRAG, CoT answer). Multi-hop improves ~4.7pp over simple RAG. Uses `CragConfidence` enum for constrained CRAG validation output. Download [RAG-QA Arena](https://dspy.ai/tutorials/rag/) (CC-BY-SA-4.0). |

> **Note on enum types:** LMP has native C# enum support for constrained output fields.
> Enum types produce JSON Schema `"enum"` constraints enforced at the token generation level
> by OpenAI Structured Outputs — zero retries needed. This is the C# equivalent of DSPy's
> `typing.Literal` support. The `TicketCategory` enum (Billing/Technical/Account/General) is
> used in 7 SupportTriageModule samples; `CragConfidence` and `UrgencyLevel`/`SentimentTone`/
> `SupportCategory` are used in the AdvancedRag and FacilitySupport benchmark samples.

---

## Samples × Techniques Matrix

| Technique | TicketTriage | Agent | RAG | Middleware | Evaluation | Modules | MIPROv2 | GEPA | Z3 | AdvOpt | AutoOpt | ChatClientTarget | MathReason | IntentClass | FacilSupport | AdvRag |
|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| LmpModule / Predictor | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | | ✅ | ✅ | ✅ | ✅ |
| Chain of Thought | ✅ | | | ✅ | ✅ | | | | | | | | ✅ | | | ✅ |
| BestOfN | | | | | | ✅ | | | | | | | | | | |
| Refine | | | | | | ✅ | | | | | | | | | | |
| ProgramOfThought | | | | | | ✅ | | | | | | | | | | |
| LmpSuggest (soft) | | | | | | ✅ | | | | | | | | | | |
| LmpAssert (hard) | ✅ | | | | | ✅ | | ✅ | | | | | | ✅ | | |
| Tool Calling / ReAct | | ✅ | | | | | | | | | | | | | | |
| Retrieval (RAG) | | | ✅ | | | | | | | | | | | | | ✅ |
| Multi-Hop RAG | | | | | | | | | | | | | | | | ✅ |
| Query Expansion | | | | | | | | | | | | | | | | ✅ |
| LLM Reranking | | | | | | | | | | | | | | | | ✅ |
| CRAG Validation | | | | | | | | | | | | | | | | ✅ |
| M.E.AI Middleware | | | | ✅ | | | | | | | | | | | | |
| UseLmpTrace | | | | ✅ | | | | | | | | | | | | |
| ChatClientTarget | | | | | | | | | | | | ✅ | | | | |
| ChainTarget | | | | | | | | | | | | ✅ | | | | |
| UseOptimized middleware | | | | | | | | | | | | ✅ | | | | |
| BootstrapFewShot | ✅ | | | | | | | | | | ✅ | | | | | |
| BootstrapRandomSearch | | | | | | ✅ | | ✅ | ✅ | ✅ | ✅ | | ✅ | ✅ | | |
| MIPROv2 (Bayesian) | | | | | | | ✅ | | | ✅ | ✅ | | ✅ | ✅ | | ✅ |
| GEPA (Evolutionary) | | | | | ✅ | | | ✅ | | | ✅ | | | | ✅ | |
| SIMBA (Mini-Batch) | | | | | | | | | | ✅ | | | | | | |
| BayesianCalibration | | | | | | | | | | ✅ | | ✅ | | | | |
| Goal.Accuracy | | | | | | | | | | ✅ | | | | | | |
| Goal.Speed | | | | | | | | | | ✅ | | | | | | |
| Goal.Cost | | | | | | | | | | ✅ | | | | | | |
| Goal.Balanced | | | | | | | | | | ✅ | | | | | | |
| CostBudget | | | | | | | | | | ✅ | | | | | | |
| LmpPipelines.Auto() | | | | | | | | | | ✅ | | | | | | |
| EvaluationCritique | | | | | ✅ | | | | | | | | | | | |
| Z3 Constraints | | | | | | | | | ✅ | | | | | | | |
| ISearchStrategy / SmacSampler | | | | | | | | | | ✅ | | | | | | |
| CostAwareSampler | | | | | | | | | | ✅ | | | | | | |
| Evaluator | ✅ | | | | ✅ | | ✅ | ✅ | ✅ | ✅ | ✅ | | ✅ | ✅ | ✅ | ✅ |
| M.E.AI Evaluation | | | | | ✅ | | | | | | | | | | | |
| Multi-Predictor Module | | | | | | | | | | | | | | | ✅ | ✅ |
| Real Benchmark Dataset | | | | | | | | | | | | | ✅ | ✅ | ✅ | ✅ |
| C# Enum Output Types | ✅ | | | | | | ✅ | ✅ | ✅ | ✅ | | | ✅ | | ✅ | ✅ |
| Source Generation | | | | | | ✅ | | | | | ✅ | | | | | |
| MSBuild Integration | | | | | | | | | | | ✅ | | | | | |

---

## Prerequisites

All samples share these requirements:

- [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0) (or later)
- **Azure OpenAI resource** (or any `IChatClient` provider — LMP is provider-agnostic)
- **Azure CLI** for `DefaultAzureCredential`: `az login`

The Z3 sample additionally requires the Z3 NuGet package (included in the project).

---

## Data

Most tutorial samples use the same **support ticket** domain:
- `data/train.jsonl` — training examples (ticket text → category + reply)
- `data/dev.jsonl` — held-out evaluation set

The RAG sample uses a fictional **NovaBridge API** knowledge base.
The AutoOptimize sample uses a simple **Q&A** dataset.

### Benchmark Samples (Real Datasets)

Benchmark samples do **not** ship data in this repository. Each README has download + conversion instructions:

| Sample | Dataset | Source | License |
|--------|---------|--------|---------|
| MathReasoning | MATH algebra | [hendrycks/competition_math](https://huggingface.co/datasets/hendrycks/competition_math) | MIT |
| IntentClassification | Banking77 | [PolyAI/banking77](https://huggingface.co/datasets/PolyAI/banking77) | CC-BY-4.0 |
| FacilitySupport | FacilitySupportAnalyzer | Meta (public) | See source |
| AdvancedRag | RAG-QA Arena Tech | [DSPy tutorials](https://dspy.ai/tutorials/rag/) | CC-BY-SA-4.0 |
