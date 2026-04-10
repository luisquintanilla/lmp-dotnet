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
| [**Evaluation**](LMP.Samples.Evaluation/) | Multi-metric evaluation. Keyword metrics → NLP (F1, BLEU) → LLM-as-judge (Coherence, Relevance, Groundedness). |

### 🔴 Advanced

| Sample | What You'll Learn |
|--------|-------------------|
| [**MIPROv2**](LMP.Samples.MIPROv2/) | Bayesian instruction + demo optimization. Proposal LM generates instruction variants, TPE searches over combinations. |
| [**GEPA**](LMP.Samples.GEPA/) | Evolutionary optimization via LLM reflection. Captures failures → diagnoses → proposes fixes → evolves instructions. |
| [**Z3**](LMP.Samples.Z3/) | Constraint-based demo selection with the Z3 solver. Enforces category coverage and quality constraints. |
| [**AdvancedOptimizers**](LMP.Samples.AdvancedOptimizers/) | Pluggable search strategies: ISampler, SmacSampler, CostAwareSampler, TraceAnalyzer, warm-start transfer learning. |
| [**AutoOptimize**](LMP.Samples.AutoOptimize/) | Build-time auto-optimization. `[AutoOptimize]` → source gen → `.g.cs` artifacts → `dotnet build -p:LmpAutoOptimize=true`. |

---

## Samples × Techniques Matrix

| Technique | TicketTriage | Agent | RAG | Middleware | Evaluation | MIPROv2 | GEPA | Z3 | AdvOpt | AutoOpt |
|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| LmpModule / Predictor | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Chain of Thought | ✅ | | | ✅ | ✅ | | | | | |
| Tool Calling / ReAct | | ✅ | | | | | | | | |
| Retrieval (RAG) | | | ✅ | | | | | | | |
| M.E.AI Middleware | | | | ✅ | | | | | | |
| BootstrapFewShot | ✅ | | | | | | | | | ✅ |
| BootstrapRandomSearch | | | | | | ✅ | | ✅ | ✅ | ✅ |
| MIPROv2 (Bayesian) | | | | | | ✅ | | | ✅ | |
| GEPA (Evolutionary) | | | | | | | ✅ | | | |
| Z3 Constraints | | | | | | | | ✅ | | |
| ISampler / SmacSampler | | | | | | | | | ✅ | |
| CostAwareSampler | | | | | | | | | ✅ | |
| Evaluator | ✅ | | | | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| M.E.AI Evaluation | | | | | ✅ | | | | | |
| Source Generation | | | | | | | | | | ✅ |
| MSBuild Integration | | | | | | | | | | ✅ |

---

## Prerequisites

All samples share these requirements:

- [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0) (or later)
- **Azure OpenAI resource** (or any `IChatClient` provider — LMP is provider-agnostic)
- **Azure CLI** for `DefaultAzureCredential`: `az login`

The Z3 sample additionally requires the Z3 NuGet package (included in the project).

---

## Data

Most samples use the same **support ticket** domain:
- `data/train.jsonl` — training examples (ticket text → category + reply)
- `data/dev.jsonl` — held-out evaluation set

The RAG sample uses a fictional **NovaBridge API** knowledge base.
The AutoOptimize sample uses a simple **Q&A** dataset.
