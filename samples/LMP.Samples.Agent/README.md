# 🤖 ReAct Agent with Tools

> **Technique:** ReAct Agent (Reason + Act) · **Difficulty:** Intermediate

## What You'll Learn

- How to define **tools** as `AIFunction` instances and hand them to an agent
- How the **Think → Act → Observe** loop lets an LLM call tools iteratively
- How `ReActAgent<TInput, TOutput>` wraps Microsoft.Extensions.AI's `FunctionInvokingChatClient` to automate tool dispatch
- How **typed input/output records** give you structured, compiler-checked results from an agentic workflow

## The Problem

You want an LLM to **research a topic**, but a single prompt isn't enough — the model needs to search the web, read articles, and extract facts before it can write a report. A plain `Predictor` can't do this because it makes one LM call and returns. You need an **agent** that can reason about *which* tool to call next, observe the result, and repeat until it has enough information to answer.

## How It Works

The ReAct pattern interleaves **reasoning** with **action**:

```
┌──────────────────────────────────────────────────────────────┐
│                    ReAct Agent Loop                           │
│                                                              │
│  ┌─────────┐     ┌─────────┐     ┌─────────────┐            │
│  │  THINK  │────▶│   ACT   │────▶│   OBSERVE   │──┐         │
│  │         │     │         │     │             │  │         │
│  │ "I need │     │ Call    │     │ Tool returns│  │         │
│  │  to     │     │ search_ │     │ two URLs    │  │         │
│  │  search │     │ web()   │     │             │  │         │
│  │  first" │     │         │     │             │  │         │
│  └─────────┘     └─────────┘     └─────────────┘  │         │
│       ▲                                            │         │
│       └────────────────────────────────────────────┘         │
│                    (repeat up to maxSteps)                    │
│                                                              │
│  When enough info is gathered:                               │
│  ┌──────────────────────────────────┐                        │
│  │  FINAL ANSWER (structured JSON)  │                        │
│  └──────────────────────────────────┘                        │
└──────────────────────────────────────────────────────────────┘
```

Under the hood, `ReActAgent` passes your tools via `ChatOptions.Tools` and delegates to `FunctionInvokingChatClient`. The middleware handles calling the LLM, parsing tool-call requests, invoking the matching `AIFunction`, and feeding results back — all within a single `GetResponseAsync` call.

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 10+ SDK | `dotnet --version` ≥ 10.0 |
| Azure OpenAI resource | A deployed chat model (e.g., `gpt-4.1-nano`) |
| Authentication | `DefaultAzureCredential` — Azure CLI login, managed identity, etc. |

## Run It

**1. Configure your Azure OpenAI endpoint and deployment:**

```bash
cd samples/LMP.Samples.Agent

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR_DEPLOYMENT_NAME"
```

**2. Make sure you're logged in to Azure:**

```bash
az login
```

**3. Run the sample:**

```bash
dotnet run
```

## Code Walkthrough

### 1. Define Tools as `AIFunction`

Each tool is a plain lambda wrapped with `AIFunctionFactory.Create`. The factory inspects the delegate's parameters and description to build the JSON schema the LLM needs for tool calling — no custom interfaces required.

```csharp
var searchWeb = AIFunctionFactory.Create(
    (string query) =>
    {
        // Your real implementation would call a search API
        return new[] { "https://example.com/article-1", "https://example.com/article-2" };
    },
    "search_web",
    "Search the web for articles on a topic. Returns a list of URLs.");

var getArticle = AIFunctionFactory.Create(
    (string url) => "Article content extracted from the URL...",
    "get_article",
    "Fetch and summarize an article at the given URL.");

var getFacts = AIFunctionFactory.Create(
    (string text) => new[] { "Fact 1", "Fact 2" },
    "get_facts",
    "Extract key facts from a block of text.");
```

> **Key point:** Tools are just `AIFunction` from `Microsoft.Extensions.AI` — no LMP-specific abstraction. Any `AIFunction` you've written for other M.E.AI scenarios works here.

### 2. Define Typed Input and Output

The input is a simple `record`. The output uses `[LmpSignature]` so the source generator can build a typed prompt and JSON schema at compile time:

```csharp
public record ResearchInput(
    [property: Description("The research topic to investigate")]
    string Topic);

[LmpSignature("Research a topic and produce a summary report")]
public partial record ResearchReport
{
    [Description("A concise summary of the research findings")]
    public required string Summary { get; init; }

    [Description("Key facts discovered during research")]
    public required string[] KeyFacts { get; init; }

    [Description("Number of sources consulted")]
    public required int SourceCount { get; init; }
}
```

`[Description]` on each property tells the LLM what to put in each field. The `partial` keyword is required so the source generator can emit the serialization context.

### 3. Create the ReAct Agent

```csharp
var agent = new ReActAgent<ResearchInput, ResearchReport>(
    client,
    tools: [searchWeb, getArticle, getFacts],
    maxSteps: 5);

agent.Instructions = "You are a research assistant. Use the available tools to search "
                   + "for information, read articles, and extract facts before producing "
                   + "your final report.";
```

What happens inside:
- The constructor wraps your `IChatClient` with `FunctionInvokingChatClient` (the M.E.AI middleware that auto-dispatches tool calls).
- `maxSteps: 5` caps the Think→Act→Observe loop at 5 iterations to prevent runaway agents.
- `Instructions` becomes the system prompt, with a ReAct preamble appended automatically.

### 4. Run the Agent

```csharp
var input = new ResearchInput("Quantum Computing");
var report = await agent.PredictAsync(input);
```

One `await` — the agent handles the entire multi-turn conversation internally. If the LLM's output fails validation, `PredictAsync` retries automatically (up to `maxRetries`).

## Expected Output

```
╔══════════════════════════════════════════════════╗
║   LMP — ReAct Agent Demo (Azure OpenAI)         ║
╚══════════════════════════════════════════════════╝

  Using: gpt-4.1-nano @ https://YOUR_RESOURCE.openai.azure.com/

Running research agent on topic: "Quantum Computing"
─────────────────────────────────────────────────────

  🔍 [search_web] query="Quantum Computing"
  📄 [get_article] url="https://example.com/quantum-computing-overview"
  📄 [get_article] url="https://example.com/qubits-explained"
  📋 [get_facts] extracting facts from 182 chars

── Research Report ────────────────────────────────
  Summary:      Quantum computing leverages qubits and superposition to
                enable parallel computation, with major investment from
                IBM and Google.
  Source Count:  2
  Key Facts:
    • Quantum computers use qubits instead of classical bits
    • Qubits leverage superposition to represent multiple states simultaneously
    • IBM and Google are leading investors in quantum computing

╔══════════════════════════════════════════════════╗
║   Demo Complete!                                 ║
╚══════════════════════════════════════════════════╝
```

> **Note:** The exact summary and order of tool calls depend on the model. The model decides which tools to call and in what order — that's the whole point of an agent.

## Key Takeaways

| Concept | What This Sample Shows |
|---|---|
| **Tools are plain `AIFunction`** | No custom interface — use `AIFunctionFactory.Create` with any lambda or method. |
| **`FunctionInvokingChatClient` does the heavy lifting** | LMP's `ReActAgent` delegates tool dispatch to M.E.AI middleware — no hand-rolled tool loop. |
| **Typed input/output** | The agent returns a strongly-typed `ResearchReport`, not a raw string. Source generators validate the schema at build time. |
| **`maxSteps` prevents runaway loops** | The agent stops after N iterations even if the model keeps requesting tools. |
| **Built-in retry with validation** | Pass a `validate` delegate to `PredictAsync` to assert output quality; failures retry automatically. |

## Next Steps

- **[RAG Sample](../LMP.Samples.Rag/)** — Combine retrieval-augmented generation with typed predictors for grounded Q&A.
- **[Middleware Sample](../LMP.Samples.Middleware/)** — Add cross-cutting concerns (logging, caching, rate limiting) to your LM pipeline.
- **Compose agents into modules** — Use `ReActAgent` as a building block inside a larger `Module` and optimize it with `BootstrapFewShot` or `MIPROv2`.
