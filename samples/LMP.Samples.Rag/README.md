# RAG Pipeline — Retrieval-Augmented Generation

| | |
|---|---|
| **Technique** | Retrieval-Augmented Generation (RAG) |
| **Difficulty** | Intermediate |

## What You'll Learn

- How to **ground LLM answers in factual context** using a retrieve → inject → generate pipeline
- How to implement the `IRetriever` interface to plug in any knowledge source
- How to compose a `RagModule` that wires retrieval into an LMP prediction pipeline
- How to **evaluate** retrieval-augmented answers against a labeled dev set

## The Problem: Knowledge Grounding

Large language models are powerful, but they can hallucinate — confidently generating
answers that sound plausible yet are factually wrong. This is especially problematic
when users ask about **domain-specific information** (product specs, internal docs,
policies) that wasn't in the model's training data.

**Retrieval-Augmented Generation (RAG)** solves this by fetching relevant documents
at query time and injecting them into the prompt as context. The model no longer has
to "remember" facts — it reads them from your knowledge base, just like a person
consulting a reference manual before answering.

## How It Works

```
┌──────────┐      ┌───────────┐      ┌──────────────┐      ┌────────────┐
│  Question │─────▶│  Retrieve │─────▶│ Inject into  │─────▶│  Generate  │
│           │      │  top-K    │      │   prompt     │      │   answer   │
└──────────┘      │ passages  │      │  as context  │      └────────────┘
                  └───────────┘      └──────────────┘
```

1. **Retrieve** — The user's question is sent to an `IRetriever`, which returns the
   top-K most relevant passages from a knowledge base.
2. **Inject** — Those passages are combined with the question into an `AugmentedInput`
   and formatted as context in the prompt.
3. **Generate** — A `Predictor` sends the augmented prompt to the LLM, which produces
   an `AnswerOutput` with an answer grounded in the retrieved context plus a confidence
   score.

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 9 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/9.0) |
| **Azure OpenAI resource** | An endpoint with a deployed chat model (e.g., `gpt-4.1-nano`) |
| **Azure CLI / login** | `az login` — the sample uses `DefaultAzureCredential` |

### Configure Secrets

The sample reads your Azure OpenAI endpoint and deployment name from
[.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
so credentials never appear in source code:

```bash
cd samples/LMP.Samples.Rag

dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment"  "YOUR_DEPLOYMENT_NAME"
```

> **Note:** Replace `YOUR_RESOURCE` and `YOUR_DEPLOYMENT_NAME` with the values from
> your Azure OpenAI resource. Never commit real endpoints or keys to source control.

## Run It

```bash
cd samples/LMP.Samples.Rag
dotnet run
```

## Code Walkthrough

### 1. Types — `Types.cs`

```csharp
public record QuestionInput(
    [property: Description("The question to answer")]
    string Question);

[LmpSignature("Answer a question using provided context")]
public partial record AnswerOutput
{
    [Description("The answer to the question based on the provided context")]
    public required string Answer { get; init; }

    [Description("Confidence score between 0.0 and 1.0")]
    public required float Confidence { get; init; }
}
```

- `QuestionInput` is the pipeline input — just a question string.
- `AnswerOutput` is what the model returns: a grounded answer and a confidence score.
- The `[Description]` attributes and `[LmpSignature]` feed directly into the auto-generated
  LLM prompt, telling the model *exactly* what each field means.

### 2. `IRetriever` — The Retrieval Abstraction

```csharp
public interface IRetriever
{
    Task<string[]> RetrieveAsync(string query, int k = 5, CancellationToken cancellationToken = default);
}
```

This is the pluggable contract defined in `LMP.Abstractions`. Any knowledge source —
an in-memory list, Azure AI Search, a vector database, a SQL full-text index — can
implement `IRetriever` and slot into the pipeline.

### 3. `InMemoryRetriever` — A Simple Keyword Retriever

```csharp
public sealed class InMemoryRetriever : IRetriever
{
    public Task<string[]> RetrieveAsync(string query, int k = 5, ...)
    {
        // Split query into words, score each document by keyword overlap,
        // return top-K highest-scoring documents.
    }
}
```

This sample uses a naive keyword-overlap scorer over an in-memory list of passages
about a fictional product called "NovaBridge." In production you would swap this for
a semantic/vector retriever — the `RagModule` doesn't need to change.

### 4. `RagModule` — The Heart of the Pipeline

```csharp
public partial class RagModule : LmpModule<QuestionInput, AnswerOutput>
{
    public override async Task<AnswerOutput> ForwardAsync(QuestionInput input, ...)
    {
        // Step 1: Retrieve relevant passages
        var passages = await _retriever.RetrieveAsync(input.Question, _topK, cancellationToken);

        // Step 2: Build augmented input with retrieved context
        var augmented = new AugmentedInput(input.Question, passages);

        // Step 3: Predict answer using context
        return await _answer.PredictAsync(augmented, ...);
    }
}
```

Key design points:

| Concept | What it does |
|---|---|
| `LmpModule<TIn, TOut>` | Base class that gives you tracing, optimizer hooks, and source-generated boilerplate |
| `Predictor<AugmentedInput, AnswerOutput>` | Sends the augmented context + question to the LLM and parses the structured response |
| `AugmentedInput.ToString()` | Formats `Question:` and `Context:` sections — this is the actual text the model sees |
| `LmpAssert.That(...)` | Validates the confidence is in `[0, 1]`; the framework retries on violation |

### 5. `Program.cs` — Putting It All Together

The entry point performs three steps:

1. **Single question** — calls `module.ForwardAsync(...)` with a question and prints the answer.
2. **Second question** — shows the module is reusable across queries.
3. **Evaluate on dev set** — loads `data/dev.jsonl` (6 labeled examples), runs each
   through the module, and computes keyword-overlap accuracy.

The evaluation metric counts how many expected-answer keywords appear in the predicted
answer, giving a simple recall score without requiring exact string matching.

## Expected Output

```
╔══════════════════════════════════════════════╗
║   LMP — RAG Pipeline Demo (Azure OpenAI)    ║
╚══════════════════════════════════════════════╝

Step 1: Single Question
───────────────────────
  Answer:     NovaBridge encrypts data at rest using AES-256 and in transit using TLS 1.3...
  Confidence: 92%

Step 2: Another Question
────────────────────────
  Answer:     NovaBridge provides official SDKs for Python, JavaScript, Go, and C#...
  Confidence: 90%

Step 3: Evaluate on Dev Set
───────────────────────────
  Examples:      6
  Average score: 85.0%
  Min score:     70.0%
  Max score:     100.0%
```

> Exact values will vary across runs due to LLM non-determinism.

## Key Takeaways

1. **RAG = Retrieve + Augment + Generate.** Fetching context at query time is the
   simplest, most effective way to reduce hallucination for domain-specific questions.

2. **`IRetriever` is pluggable.** Swap `InMemoryRetriever` for a vector store or
   search index without touching the rest of the pipeline.

3. **`RagModule` follows the LMP module pattern** — it extends `LmpModule`, uses a
   `Predictor` for the LLM call, and gets tracing/optimization support for free.

4. **Structured outputs are enforced.** The `[Description]` attributes and
   `LmpAssert` validation ensure the model returns well-formed answers with valid
   confidence scores.

5. **Evaluation is built in.** `Evaluator.EvaluateAsync` lets you measure pipeline
   quality against labeled examples in a single call.

## Next Steps

| Sample | What it adds |
|---|---|
| [**TicketTriage**](../LMP.Samples.TicketTriage/) | Multi-class classification with RAG — route support tickets using retrieved context |
| [**Evaluation**](../LMP.Samples.Evaluation/) | Deeper dive into evaluation strategies, custom metrics, and test-set design |
