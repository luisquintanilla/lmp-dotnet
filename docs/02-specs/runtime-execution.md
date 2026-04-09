# Runtime Execution Specification

> **Status:** v2 — rewritten to match system-architecture.md  
> **Target:** .NET 10 / C# 14  
> **Dependency:** `Microsoft.Extensions.AI` (`IChatClient`)  
> **Audience:** Implementer — a junior developer should be able to build the runtime from this document alone.

---

## 1. What the Runtime Does (Plain Language)

The runtime is the part of the framework that **actually runs your LM program**. There are no graphs, no IR, no step dispatchers. The runtime is just C# classes calling `IChatClient`.

Concretely, the runtime:

1. **Calls language models** through `IChatClient.GetResponseAsync<TOutput>()` — the core prediction loop.
2. **Manages learnable state** (instructions, few-shot demos, config) inside `Predictor<TInput, TOutput>`.
3. **Records traces** — captures `(predictor name, input, output)` tuples during execution for optimizer consumption.
4. **Retries on assertion failure** — `LmpAssert` triggers retry with error feedback; `LmpSuggest` logs and continues.
5. **Composes predictors** — `LmpModule.ForwardAsync()` is plain C# that chains predictors together.
6. **Persists and restores state** — `SaveAsync` / `LoadAsync` serialize demos + instructions to JSON via source-gen `JsonSerializerContext`.

> **What's gone:** There is no `ProgramDescriptor`, no `StepDescriptor`, no `EdgeDescriptor`, no TPL Dataflow, no topological sort, no `ProgramExecutor`, no `StepExecutor`, no `ExecutionContext`. Modules are plain C# classes. Composition is method calls. Parallelism is `Task.WhenAll`.

---

## 2. Predictor\<TInput, TOutput\> Internals

`Predictor<TInput, TOutput>` is the atomic unit of LM invocation. It holds learnable state, builds prompts, calls the model, records traces, and handles assertion-driven retries.

### 2.1 State Held by a Predictor

| Field | Type | Purpose |
|---|---|---|
| `Instructions` | `string` | System-level instructions (from `[LmpSignature]`, overridable by optimizers) |
| `Demos` | `List<Example<TInput, TOutput>>` | Few-shot examples — filled by optimizers, serialized by `SaveAsync` |
| `Config` | `ChatOptions` | M.E.AI chat options: temperature, max tokens, stop sequences, etc. |
| `Client` | `IChatClient` | The LM provider — injected via constructor, never learnable |

```csharp
public class Predictor<TInput, TOutput>
{
    private readonly IChatClient _client;

    public string Instructions { get; set; }
    public List<Example<TInput, TOutput>> Demos { get; set; } = [];
    public ChatOptions Config { get; set; } = new();

    public Predictor(IChatClient client)
    {
        _client = client;
        // Source generator emits a static method that returns the default
        // instructions from [LmpSignature] on TOutput.
        Instructions = PromptBuilder<TInput, TOutput>.DefaultInstructions;
    }
}
```

### 2.2 PredictAsync Flow

`PredictAsync` is the single execution path. No graph dispatch, no step executors — just prompt → call → parse.

```
 TInput
   │
   ▼
 ┌──────────────────────────────────────────────┐
 │ Source-gen PromptBuilder<TInput, TOutput>     │
 │  • System msg: Instructions + field schemas  │
 │  • Demo pairs: Demos → (User, Assistant)×N   │
 │  • Current: TInput fields → User msg         │
 │  Result: ChatMessage[]                       │
 └──────────────────┬───────────────────────────┘
                    ▼
 ┌──────────────────────────────────────────────┐
 │ IChatClient.GetResponseAsync<TOutput>()      │
 │  M.E.AI handles JSON schema, structured      │
 │  output negotiation with the provider        │
 └──────────────────┬───────────────────────────┘
                    ▼
                 TOutput
```

```csharp
public async Task<TOutput> PredictAsync(
    TInput input,
    Trace? trace = null,
    CancellationToken cancellationToken = default)
{
    // 1. Build prompt — source-generated, no reflection
    var messages = PromptBuilder<TInput, TOutput>.Build(
        Instructions, Demos, input);

    // 2. Call the model — M.E.AI handles structured output
    var result = await _client.GetResponseAsync<TOutput>(
        messages, Config, cancellationToken);

    // 3. Record trace entry (if tracing is active)
    trace?.Record(Name, input, result);

    return result;
}
```

**Key decisions:**

- **`GetResponseAsync<TOutput>()`** — not `GetResponseAsync()` + manual JSON parse. M.E.AI negotiates JSON schema with the provider (tool-use for Anthropic, `response_format` for OpenAI). No `OutputParser`, no `ExtractJson`, no regex fence stripping.
- **`PromptBuilder<TInput, TOutput>`** — emitted by the source generator. Field names, types, and descriptions are baked in as string constants. No runtime reflection.
- **`ChatOptions`** — M.E.AI's native options type. No custom `PredictorConfig` wrapper.

### 2.3 Prompt Format

The source-generated `PromptBuilder<TInput, TOutput>` creates `ChatMessage[]` with this structure:

```
System message:
  {Instructions}
  Field descriptions (from [Description] / XML doc comments on TInput + TOutput)

Demo pairs (from predictor.Demos):
  User message:   serialized TInput fields
  Assistant message: serialized TOutput as JSON

Current input:
  User message:   serialized TInput fields
  [If retry: "Previous attempt failed: {assertion message}. Try again."]
```

**No explicit JSON schema in the prompt.** `GetResponseAsync<TOutput>()` passes the schema to the provider via its native structured-output mechanism.

### 2.4 Trace Recording

During execution, a `Trace` object captures every predictor invocation. Optimizers use these traces to select few-shot demos.

```csharp
public sealed class Trace
{
    private readonly List<TraceEntry> _entries = [];

    public IReadOnlyList<TraceEntry> Entries => _entries;

    public void Record<TInput, TOutput>(
        string predictorName, TInput input, TOutput output)
    {
        _entries.Add(new TraceEntry(
            PredictorName: predictorName,
            Input: input!,
            Output: output!));
    }
}

public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);
```

- A `Trace` is created per top-level `ForwardAsync` call.
- Each `PredictAsync` call appends one `TraceEntry`.
- The optimizer collects traces from successful training examples and fills `predictor.Demos`.

### 2.5 Retry on LmpAssert Failure

When `LmpAssert.That()` fails, the predictor re-invokes the LM with the assertion error message appended to the prompt. This is **backtracking** — the predictor retries itself, not an external repair step.

```csharp
public async Task<TOutput> PredictAsync(
    TInput input,
    Trace? trace = null,
    int maxRetries = 3,
    CancellationToken cancellationToken = default)
{
    string? lastError = null;

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        var messages = PromptBuilder<TInput, TOutput>.Build(
            Instructions, Demos, input, lastError);

        var result = await _client.GetResponseAsync<TOutput>(
            messages, Config, cancellationToken);

        trace?.Record(Name, input, result);

        try
        {
            // LmpAssert.That() calls are in the caller's code (ForwardAsync),
            // but PredictAsync itself handles the retry loop when called via
            // the assertion-aware overload. See §6 for assertion mechanics.
            return result;
        }
        catch (LmpAssertionException ex)
        {
            lastError = ex.Message;
            // Loop continues — next attempt includes error feedback
        }
    }

    throw new LmpMaxRetriesExceededException(Name, maxRetries);
}
```

> **How `lastError` reaches the prompt:** `PromptBuilder.Build()` accepts an optional `lastError` parameter. When non-null, it appends a feedback section to the final user message: `"Previous attempt failed: {lastError}. Try again."` This gives the LM context to self-correct.

### 2.6 Predictor Name

Each predictor has a `Name` property used in traces and diagnostics. The source generator sets it to the field name in the containing `LmpModule`:

```csharp
// Source-generated in GetPredictors():
_classify.Name = "classify";
_draft.Name = "draft";
```

For standalone predictors (not inside a module), the name defaults to `$"{typeof(TInput).Name}→{typeof(TOutput).Name}"`.

---

## 3. LmpModule Execution

`LmpModule` is the base class for composable LM programs. It provides three capabilities: forward execution, predictor discovery, and state persistence.

### 3.1 ForwardAsync() — Developer Implements

The developer overrides `ForwardAsync()` with plain C# that chains predictors. There is no graph, no step registration, no edge declaration — just method calls.

```csharp
public class TicketTriageModule : LmpModule
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public TicketTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client);
        _draft = new Predictor<ClassifyTicket, DraftReply>(client);
    }

    public async Task<DraftReply> ForwardAsync(
        TicketInput input,
        CancellationToken cancellationToken = default)
    {
        var classification = await _classify.PredictAsync(
            input, Trace, cancellationToken: cancellationToken);

        LmpAssert.That(classification,
            c => c.Urgency >= 1 && c.Urgency <= 5,
            "Urgency must be between 1 and 5");

        return await _draft.PredictAsync(
            classification, Trace, cancellationToken: cancellationToken);
    }
}
```

**Execution model:** `ForwardAsync()` is plain `async/await`. Parallelism is `Task.WhenAll`. Branching is `if/else`. Loops are `for/while`. The C# compiler handles control flow — LMP does not reimplement it.

### 3.2 GetPredictors() — Source-Generator Emitted

The source generator emits `GetPredictors()` to enumerate all `Predictor` fields. Optimizers call this to discover learnable parameters without reflection.

```csharp
// Source-generated — emitted as a partial method on TicketTriageModule
public override IReadOnlyList<IPredictor> GetPredictors()
{
    _classify.Name = "classify";
    _draft.Name = "draft";
    return [_classify, _draft];
}
```

**What `IPredictor` exposes to optimizers:**

```csharp
public interface IPredictor
{
    string Name { get; set; }
    string Instructions { get; set; }
    IList Demos { get; }
    ChatOptions Config { get; set; }
}
```

The non-generic `IPredictor` interface lets optimizers iterate over predictors without knowing `TInput`/`TOutput` at compile time. `Demos` is typed as `IList` — the concrete `List<Example<TInput, TOutput>>` satisfies this.

### 3.3 SaveAsync / LoadAsync — JSON State Persistence

State persistence serializes each predictor's learnable parameters (instructions + demos) to JSON. The source generator emits a `JsonSerializerContext` so serialization is AOT-safe and reflection-free.

```csharp
// Source-generated JsonSerializerContext for the module
[JsonSerializable(typeof(ModuleState<TicketTriageModule>))]
[JsonSerializable(typeof(PredictorState<TicketInput, ClassifyTicket>))]
[JsonSerializable(typeof(PredictorState<ClassifyTicket, DraftReply>))]
internal partial class TicketTriageModuleJsonContext : JsonSerializerContext;
```

**SaveAsync:**

```csharp
public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
{
    var state = new ModuleState
    {
        Predictors = GetPredictors().ToDictionary(
            p => p.Name,
            p => new PredictorState
            {
                Instructions = p.Instructions,
                Demos = p.Demos
            })
    };

    await using var stream = File.Create(path);
    await JsonSerializer.SerializeAsync(stream, state,
        GeneratedJsonContext.Default.ModuleState, cancellationToken);
}
```

**LoadAsync:**

```csharp
public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
{
    await using var stream = File.OpenRead(path);
    var state = await JsonSerializer.DeserializeAsync(stream,
        GeneratedJsonContext.Default.ModuleState, cancellationToken)
        ?? throw new InvalidOperationException("Deserialized null state.");

    foreach (var predictor in GetPredictors())
    {
        if (state.Predictors.TryGetValue(predictor.Name, out var ps))
        {
            predictor.Instructions = ps.Instructions;
            // Demos are set via reflection-free source-gen helper
            predictor.LoadDemos(ps.Demos);
        }
    }
}
```

**JSON format (human-readable, diff-friendly):**

```json
{
  "predictors": {
    "classify": {
      "instructions": "Classify a support ticket by category and urgency",
      "demos": [
        {
          "input": { "ticketText": "I was charged twice" },
          "output": { "category": "billing", "urgency": 4 }
        }
      ]
    },
    "draft": {
      "instructions": "Draft a helpful reply to the customer",
      "demos": []
    }
  }
}
```

### 3.4 Trace Property

`LmpModule` exposes a `Trace` property that `ForwardAsync` passes to each `PredictAsync` call. The optimizer creates a fresh `Trace` before each training run and reads it after.

```csharp
public abstract class LmpModule
{
    public Trace? Trace { get; set; }

    public abstract IReadOnlyList<IPredictor> GetPredictors();
}
```

---

## 4. Reasoning Strategies

Reasoning strategies are thin wrappers around `Predictor<TInput, TOutput>`. Each lives in `LMP.Modules` and is under 100 lines of code. They do not introduce new execution primitives — they compose the existing ones.

### 4.1 ChainOfThought\<TInput, TOutput\>

Asks the LM to produce step-by-step reasoning before the final answer.

**Mechanism:** The source generator creates an **extended output type** at build time that prepends a `Reasoning` field to `TOutput`:

```csharp
// Source-generated at build time
internal record ChainOfThoughtOutput<TOutput>(
    [property: Description("Step-by-step reasoning")]
    string Reasoning,
    TOutput Result);
```

At runtime, `ChainOfThought` wraps an inner predictor that targets the extended type:

```csharp
public class ChainOfThought<TInput, TOutput>
{
    private readonly Predictor<TInput, ChainOfThoughtOutput<TOutput>> _inner;

    public ChainOfThought(IChatClient client)
    {
        _inner = new Predictor<TInput, ChainOfThoughtOutput<TOutput>>(client);
    }

    public async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        CancellationToken cancellationToken = default)
    {
        var extended = await _inner.PredictAsync(
            input, trace, cancellationToken: cancellationToken);

        // Reasoning is captured in the trace via the inner predictor.
        // Only the final TOutput is returned to the caller.
        return extended.Result;
    }

    // Delegate learnable state to inner predictor
    public string Instructions
    {
        get => _inner.Instructions;
        set => _inner.Instructions = value;
    }

    public List<Example<TInput, ChainOfThoughtOutput<TOutput>>> Demos
    {
        get => _inner.Demos;
        set => _inner.Demos = value;
    }
}
```

**Key detail:** The caller gets `TOutput`. The reasoning is captured in the trace (via the inner predictor's `Record` call) and available to optimizers, but it does not leak into the caller's type system.

### 4.2 BestOfN\<TInput, TOutput\>

Runs N parallel predictions and returns the one that scores highest on a reward function.

```csharp
public class BestOfN<TInput, TOutput>
{
    private readonly Predictor<TInput, TOutput> _predictor;
    private readonly int _n;
    private readonly Func<TInput, TOutput, float> _reward;

    public BestOfN(
        IChatClient client,
        int n,
        Func<TInput, TOutput, float> reward)
    {
        _predictor = new Predictor<TInput, TOutput>(client);
        _n = n;
        _reward = reward;
    }

    public async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        CancellationToken cancellationToken = default)
    {
        // True parallelism — no GIL. Each prediction runs concurrently.
        var tasks = Enumerable.Range(0, _n)
            .Select(_ => _predictor.PredictAsync(
                input, trace, cancellationToken: cancellationToken));

        var candidates = await Task.WhenAll(tasks);

        // Score each candidate and return the best
        return candidates
            .OrderByDescending(c => _reward(input, c))
            .First();
    }
}
```

**Why `Task.WhenAll`:** .NET has true OS-level parallelism. All N predictions run concurrently against the LM API. This is a structural advantage over Python's GIL-constrained `asyncio.gather`.

### 4.3 Refine\<TInput, TOutput\>

Iterative improvement loop: predict → LM-generated critique → predict again with critique context.

```csharp
public class Refine<TInput, TOutput>
{
    private readonly Predictor<TInput, TOutput> _predictor;
    private readonly Predictor<RefineCritiqueInput<TOutput>, TOutput> _refiner;
    private readonly int _maxIterations;

    public Refine(IChatClient client, int maxIterations = 2)
    {
        _predictor = new Predictor<TInput, TOutput>(client);
        _refiner = new Predictor<RefineCritiqueInput<TOutput>, TOutput>(client)
        {
            Instructions = "Given the original input, a previous attempt, " +
                           "and a critique, produce an improved output."
        };
        _maxIterations = maxIterations;
    }

    public async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        CancellationToken cancellationToken = default)
    {
        // Initial prediction
        var current = await _predictor.PredictAsync(
            input, trace, cancellationToken: cancellationToken);

        for (int i = 0; i < _maxIterations; i++)
        {
            var critiqueInput = new RefineCritiqueInput<TOutput>(
                OriginalInput: input!,
                PreviousOutput: current);

            current = await _refiner.PredictAsync(
                critiqueInput, trace, cancellationToken: cancellationToken);
        }

        return current;
    }
}

public record RefineCritiqueInput<TOutput>(
    [Description("The original input")] object OriginalInput,
    [Description("The previous attempt to improve upon")] TOutput PreviousOutput);
```

**Design:** Each refinement iteration is a separate `PredictAsync` call recorded in the trace. The optimizer can see the full refinement chain and learn which critique patterns improve output quality.

---

## 5. ReAct Agent Loop

`ReActAgent<TInput, TOutput>` implements the Think → Select Tool → Call Tool → Observe → Repeat loop. It uses M.E.AI's `AIFunction` and `FunctionInvokingChatClient` for tool execution — no custom tool abstraction.

### 5.1 Architecture

```
 TInput
   │
   ▼
 ┌──────────────────────────────────────────────┐
 │  Think:  LM decides what to do next          │
 │  Act:    LM selects a tool + arguments       │ ◄── FunctionInvokingChatClient
 │  Observe: Tool result appended to history    │     handles tool dispatch
 │  Repeat until LM produces final answer       │
 └──────────────────┬───────────────────────────┘
                    ▼
                 TOutput
```

### 5.2 Implementation

```csharp
public class ReActAgent<TInput, TOutput>
{
    private readonly IChatClient _client;
    private readonly IList<AIFunction> _tools;
    private readonly int _maxSteps;
    private readonly ChatOptions _config;
    private readonly string _instructions;

    public ReActAgent(
        IChatClient client,
        IList<AIFunction> tools,
        int maxSteps = 10)
    {
        // Wrap with FunctionInvokingChatClient for automatic tool dispatch
        _client = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
        _tools = tools;
        _maxSteps = maxSteps;
        _instructions = PromptBuilder<TInput, TOutput>.DefaultInstructions;
        _config = new ChatOptions();
    }

    public string Instructions
    {
        get => _instructions;
        init => _instructions = value;
    }

    public async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _instructions),
            new(ChatRole.User,
                PromptBuilder<TInput, TOutput>.FormatInput(input))
        };

        var options = new ChatOptions
        {
            Tools = [.. _tools],
            Temperature = _config.Temperature,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        // The agent loop — FunctionInvokingChatClient handles
        // Think → Act → Observe internally. Each call to
        // GetResponseAsync may trigger multiple tool invocations
        // before the LM produces a final text response.
        var response = await _client.GetResponseAsync<TOutput>(
            messages, options, cancellationToken);

        trace?.Record("react-agent", input, response);

        return response;
    }
}
```

### 5.3 Tool Registration

Tools are M.E.AI `AIFunction` instances — created from static or instance methods via `AIFunctionFactory`:

```csharp
var tools = new AIFunction[]
{
    AIFunctionFactory.Create(SearchKnowledgeBase),
    AIFunctionFactory.Create(GetAccountInfo),
    AIFunctionFactory.Create(CreateJiraTicket)
};

var agent = new ReActAgent<TicketInput, TriageResult>(client, tools);
```

```csharp
[Description("Search the knowledge base for relevant articles")]
static async Task<string[]> SearchKnowledgeBase(
    [Description("Search query")] string query,
    [Description("Max results")] int topK = 5)
{
    // Implementation — hits your vector store, Elasticsearch, etc.
    return await knowledgeBase.SearchAsync(query, topK);
}
```

**No custom tool abstraction.** `AIFunction` already provides: name, description, parameter schema, invocation. `FunctionInvokingChatClient` already handles: tool selection parsing, dispatch, result formatting, multi-turn loops. LMP adds zero code here.

### 5.4 Trajectory Tracking

The full agent trajectory (think/act/observe steps) is captured as the `ChatMessage[]` history within the `FunctionInvokingChatClient`. For optimizer consumption, the trace records the final `(input, output)` pair. If fine-grained trajectory inspection is needed, the `IChatClient` middleware pipeline (e.g., `UseLogging()`) captures individual turns.

---

## 6. Assertions

Assertions are runtime checks on LM outputs. They come in two flavors: hard (`LmpAssert`) and soft (`LmpSuggest`).

### 6.1 LmpAssert — Fail → Retry with Feedback

```csharp
public static class LmpAssert
{
    public static void That<T>(
        T result,
        Func<T, bool> predicate,
        string message)
    {
        if (!predicate(result))
        {
            throw new LmpAssertionException(message, result);
        }
    }
}

public class LmpAssertionException : Exception
{
    public object? FailedResult { get; }

    public LmpAssertionException(string message, object? failedResult)
        : base(message)
    {
        FailedResult = failedResult;
    }
}
```

**Retry mechanism:** The caller wraps the predict + assert in a retry loop:

```csharp
public async Task<DraftReply> ForwardAsync(TicketInput input,
    CancellationToken cancellationToken = default)
{
    var classification = await _classify.PredictAsync(
        input, Trace, cancellationToken: cancellationToken);

    // If this fails, the exception propagates. The developer can
    // wrap in a retry loop, or use the assertion-aware PredictAsync overload.
    LmpAssert.That(classification,
        c => c.Urgency >= 1 && c.Urgency <= 5,
        "Urgency must be between 1 and 5");

    return await _draft.PredictAsync(
        classification, Trace, cancellationToken: cancellationToken);
}
```

For automatic retry, the developer uses the retry-aware pattern:

```csharp
public async Task<DraftReply> ForwardAsync(TicketInput input,
    CancellationToken cancellationToken = default)
{
    ClassifyTicket classification = default!;
    string? lastError = null;

    for (int attempt = 0; attempt < 3; attempt++)
    {
        classification = await _classify.PredictAsync(
            input, Trace, lastError: lastError,
            cancellationToken: cancellationToken);

        try
        {
            LmpAssert.That(classification,
                c => c.Urgency >= 1 && c.Urgency <= 5,
                "Urgency must be between 1 and 5");
            break; // Assertion passed
        }
        catch (LmpAssertionException ex)
        {
            lastError = ex.Message;
        }
    }

    return await _draft.PredictAsync(
        classification, Trace, cancellationToken: cancellationToken);
}
```

**Why explicit loops, not magic?** The retry loop is visible C# code. The developer controls retry count, which predictors are retried, and whether to backtrack to an earlier predictor. No hidden `with_assertions()` decorator — the control flow is in `ForwardAsync` where the developer can see and debug it.

### 6.2 LmpSuggest — Fail → Log Warning, Continue

```csharp
public static class LmpSuggest
{
    private static readonly ILogger Logger =
        LoggerFactory.Create(b => b.AddConsole())
            .CreateLogger(nameof(LmpSuggest));

    public static void That<T>(
        T result,
        Func<T, bool> predicate,
        string message)
    {
        if (!predicate(result))
        {
            Logger.LogWarning(
                "LmpSuggest failed: {Message}. Result: {Result}",
                message, result);
        }
    }
}
```

**Usage:**

```csharp
var classification = await _classify.PredictAsync(input, Trace);

// Soft check — logs warning if category is "unknown", but continues
LmpSuggest.That(classification,
    c => c.Category != "unknown",
    "Category should not be 'unknown'");

return await _draft.PredictAsync(classification, Trace);
```

**Design:** `LmpSuggest` never throws. It logs a structured warning via `ILogger`. During optimization, the optimizer can read these warnings from the logging pipeline and use them as soft signals for candidate ranking.

### 6.3 Assertion Summary

| Type | On Failure | Use Case |
|---|---|---|
| `LmpAssert.That()` | Throws `LmpAssertionException` → retry with feedback | Hard constraints: valid ranges, required formats |
| `LmpSuggest.That()` | Logs warning via `ILogger`, continues | Soft preferences: style guidelines, optional fields |

---

## 7. RAG Composition

RAG (Retrieval-Augmented Generation) is not a special primitive. It is plain composition in `ForwardAsync`: retrieve passages, then predict with those passages as context.

### 7.1 IRetriever Interface

```csharp
public interface IRetriever
{
    Task<string[]> RetrieveAsync(
        string query,
        int k = 5,
        CancellationToken cancellationToken = default);
}
```

Users bring their own implementation via DI — vector store, Elasticsearch, Azure AI Search, in-memory BM25, etc.

### 7.2 RAG Module Example

```csharp
public record QuestionInput(
    [Description("The user's question")] string Question);

[LmpSignature("Answer the question using the provided context passages")]
public partial record AnswerWithContext
{
    [Description("The answer to the user's question")]
    public required string Answer { get; init; }

    [Description("Confidence from 0.0 to 1.0")]
    public required float Confidence { get; init; }
}

public record AnswerInput(
    [Description("The user's question")] string Question,
    [Description("Retrieved context passages")] string[] Passages);

public class RagQaModule : LmpModule
{
    private readonly IRetriever _retriever;
    private readonly Predictor<AnswerInput, AnswerWithContext> _answer;

    public RagQaModule(IChatClient client, IRetriever retriever)
    {
        _retriever = retriever;
        _answer = new Predictor<AnswerInput, AnswerWithContext>(client);
    }

    public async Task<AnswerWithContext> ForwardAsync(
        QuestionInput input,
        CancellationToken cancellationToken = default)
    {
        // 1. Retrieve
        var passages = await _retriever.RetrieveAsync(
            input.Question, k: 5, cancellationToken);

        // 2. Predict with context
        var answerInput = new AnswerInput(input.Question, passages);
        var result = await _answer.PredictAsync(
            answerInput, Trace, cancellationToken: cancellationToken);

        LmpAssert.That(result,
            r => r.Confidence >= 0f && r.Confidence <= 1f,
            "Confidence must be between 0.0 and 1.0");

        return result;
    }
}
```

**Design:** `IRetriever.RetrieveAsync` returns `string[]` — the simplest useful abstraction. If you need metadata (scores, document IDs), define a richer return type in your own implementation. LMP does not dictate retriever internals.

**Optimization works naturally:** The optimizer calls `ForwardAsync` on training data, collects traces, and fills `_answer.Demos` with successful examples. The retriever is called during optimization too — the few-shot demos capture the full retrieve-then-predict pattern.

---

## 8. Cancellation and Timeout

### 8.1 CancellationToken Threading

Every async method accepts a `CancellationToken`. The token flows from the top-level caller through `ForwardAsync`, into each `PredictAsync`, and down to `IChatClient.GetResponseAsync<T>()`.

```csharp
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var result = await module.ForwardAsync(input, cts.Token);
```

### 8.2 Per-Predictor Timeout

Individual predictor calls can be bounded with a linked `CancellationTokenSource`:

```csharp
using var timeoutCts = CancellationTokenSource
    .CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

var result = await _classify.PredictAsync(
    input, Trace, cancellationToken: timeoutCts.Token);
```

### 8.3 Resilience Pipeline

LM API calls can be wrapped in a `ResiliencePipeline` from `Microsoft.Extensions.Resilience` via `IChatClient` middleware:

```csharp
var client = new ChatClientBuilder(innerClient)
    .UseOpenTelemetry()
    .UseDistributedCache()
    .Build();
```

Retry, circuit breaker, and rate limiting are middleware concerns — not runtime concerns. The `Predictor` calls `IChatClient` and the middleware pipeline handles transience.

---

## Appendix: Key Types Summary

```csharp
// === Core types ===

public abstract class LmpModule
{
    public Trace? Trace { get; set; }
    public abstract IReadOnlyList<IPredictor> GetPredictors();
    public abstract Task SaveAsync(string path, CancellationToken ct = default);
    public abstract Task LoadAsync(string path, CancellationToken ct = default);
}

public interface IPredictor
{
    string Name { get; set; }
    string Instructions { get; set; }
    IList Demos { get; }
    ChatOptions Config { get; set; }
}

public sealed class Trace
{
    public IReadOnlyList<TraceEntry> Entries { get; }
    public void Record<TInput, TOutput>(string name, TInput input, TOutput output);
}

public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);

public sealed record Example<TInput, TOutput>(
    TInput Input,
    TOutput Output);

// === Retrieval ===

public interface IRetriever
{
    Task<string[]> RetrieveAsync(string query, int k = 5,
        CancellationToken cancellationToken = default);
}

// === Assertions ===

public static class LmpAssert
{
    public static void That<T>(T result, Func<T, bool> predicate, string message);
}

public static class LmpSuggest
{
    public static void That<T>(T result, Func<T, bool> predicate, string message);
}

public class LmpAssertionException(string message, object? failedResult)
    : Exception(message)
{
    public object? FailedResult { get; } = failedResult;
}

public class LmpMaxRetriesExceededException(string predictorName, int maxRetries)
    : Exception($"Predictor '{predictorName}' failed after {maxRetries} retries.");

// === State persistence ===

public record ModuleState
{
    public Dictionary<string, PredictorState> Predictors { get; init; } = new();
}

public record PredictorState
{
    public string Instructions { get; init; } = "";
    public IList Demos { get; init; } = new List<object>();
}
```
