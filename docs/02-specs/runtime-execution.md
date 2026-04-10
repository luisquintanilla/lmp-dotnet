# Runtime Execution Specification

> **Status:** v3 — updated to match actual implementation (Phase 9A.2)  
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
| `Instructions` | `string` | System-level instructions (defaults to empty; set by optimizers or `[LmpSignature]`) |
| `Demos` | `List<(TInput Input, TOutput Output)>` | Few-shot examples — filled by optimizers, serialized by `SaveAsync` |
| `Config` | `ChatOptions` | M.E.AI chat options: temperature, max tokens, stop sequences, etc. |
| `Client` | `IChatClient` | The LM provider — injected via constructor, never learnable |
| `Name` | `string` | Predictor identity for traces and diagnostics |
| `SerializerOptions` | `JsonSerializerOptions?` | Optional source-gen `JsonSerializerContext`-based options for AOT-safe serialization |

```csharp
public class Predictor<TInput, TOutput> : IPredictor
    where TOutput : class
{
    private readonly IChatClient _client;

    public IChatClient Client => _client;
    public string Name { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public List<(TInput Input, TOutput Output)> Demos { get; set; } = [];
    public ChatOptions Config { get; set; } = new();
    public JsonSerializerOptions? SerializerOptions { get; set; }

    public Predictor(IChatClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Name = $"{typeof(TInput).Name}→{typeof(TOutput).Name}";
    }
}
```

**`where TOutput : class`:** Required by `GetResponseAsync<TOutput>()` in M.E.AI's structured output API.

### 2.2 PredictAsync Flow

`PredictAsync` is the single execution path. No graph dispatch, no step executors — just prompt → call → parse → optional validate/retry.

```
 TInput
   │
   ▼
 ┌──────────────────────────────────────────────┐
 │ MessageBuilder delegate (source-gen) or      │
 │ BuildDefaultMessages fallback                │
 │  • System msg: Instructions + field schemas  │
 │  • Demo pairs: Demos → (User, Assistant)×N   │
 │  • Current: TInput → User msg               │
 │  • If retry: append error feedback           │
 │  Result: IList<ChatMessage>                  │
 └──────────────────┬───────────────────────────┘
                    ▼
 ┌──────────────────────────────────────────────┐
 │ IChatClient.GetResponseAsync<TOutput>()      │
 │  M.E.AI handles JSON schema, structured      │
 │  output negotiation with the provider        │
 └──────────────────┬───────────────────────────┘
                    ▼
                 TOutput
                    │
            ┌───────┴───────┐
            │  validate?    │
            │  (if set)     │
            └───────┬───────┘
              pass? │ fail → retry with error feedback
                    ▼
               Return TOutput
```

```csharp
public virtual async Task<TOutput> PredictAsync(
    TInput input,
    Trace? trace = null,
    Action<TOutput>? validate = null,
    int maxRetries = 3,
    CancellationToken cancellationToken = default)
{
    string? lastError = null;

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        var messages = BuildMessages(input, lastError);

        var response = await _client.GetResponseAsync<TOutput>(
            messages, Config, cancellationToken: cancellationToken);

        var result = response.Result
            ?? throw new InvalidOperationException(
                $"Predictor '{Name}': structured output returned null.");

        trace?.Record(Name, input!, result);

        if (validate is null)
            return result;

        try
        {
            validate(result);
            return result;
        }
        catch (LmpAssertionException ex)
        {
            lastError = ex.Message;
        }
    }

    throw new LmpMaxRetriesExceededException(Name, maxRetries);
}
```

**Key decisions:**

- **`GetResponseAsync<TOutput>()`** — not `GetResponseAsync()` + manual JSON parse. M.E.AI negotiates JSON schema with the provider (tool-use for Anthropic, `response_format` for OpenAI). No `OutputParser`, no `ExtractJson`, no regex fence stripping.
- **`MessageBuilder` delegate** — set by source-generated interceptor code via `SetPromptBuilder()`. Field names, types, and descriptions are baked in as string constants. No runtime reflection. When `null`, falls back to `BuildDefaultMessages` which uses `ToString()` on inputs and `JsonSerializer` for demo outputs.
- **`validate` parameter** — optional `Action<TOutput>` that throws `LmpAssertionException` on validation failure. The retry loop is built into `PredictAsync` — no external retry pattern needed.
- **`ChatOptions`** — M.E.AI's native options type. No custom `PredictorConfig` wrapper.

### 2.2.1 SetPromptBuilder / MessageBuilder — Source Gen Integration

The source generator emits interceptor code that calls `SetPromptBuilder()` once per predictor, wiring a type-specific prompt formatting delegate:

```csharp
// Source-generated interceptor code (simplified):
predictor.SetPromptBuilder((instructions, input, demos, lastError) =>
{
    var messages = new List<ChatMessage>();
    // System message with instructions + field schemas
    if (!string.IsNullOrEmpty(instructions))
        messages.Add(new ChatMessage(ChatRole.System, instructions + "\n\n" + fieldSchemas));
    // Demo pairs
    foreach (var (demoInput, demoOutput) in demos ?? [])
        // ... format input/output with field-level detail
    // Current input with optional error feedback
    return messages;
});
```

**`SetPromptBuilder` API:**

```csharp
[EditorBrowsable(EditorBrowsableState.Never)]
public void SetPromptBuilder(
    Func<string, TInput, IReadOnlyList<(TInput Input, TOutput Output)>?,
         string?, IList<ChatMessage>> builder)
{
    MessageBuilder ??= builder;  // Only sets if not already set
}
```

The `??=` guard ensures the first call wins — if an optimizer or user sets a custom builder before the interceptor runs, it is preserved.

When `MessageBuilder` is `null` (no source-gen), `BuildDefaultMessages` provides a simple fallback:

```csharp
protected virtual IList<ChatMessage> BuildMessages(TInput input, string? lastError)
{
    if (MessageBuilder is not null)
        return MessageBuilder(Instructions, input, Demos, lastError);
    return BuildDefaultMessages(input, lastError);
}
```

The fallback uses `ToString()` for inputs and `JsonSerializer.Serialize(demoOutput, SerializerOptions)` for demo outputs — functional but less detailed than the source-generated version.

### 2.3 Prompt Format

The `MessageBuilder` delegate (source-generated) or `BuildDefaultMessages` fallback creates `IList<ChatMessage>` with this structure:

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

During execution, a `Trace` object captures every predictor invocation. Optimizers use these traces to select few-shot demos. The trace is thread-safe: concurrent predictor calls (e.g., `BestOfN`) can record simultaneously.

```csharp
public sealed class Trace
{
    private readonly List<TraceEntry> _entries = [];
    private readonly object _lock = new();

    public IReadOnlyList<TraceEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public void Record(string predictorName, object input, object output)
    {
        lock (_lock)
        {
            _entries.Add(new TraceEntry(predictorName, input, output));
        }
    }
}

public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);
```

- `Record` is non-generic — both `input` and `output` are boxed as `object`.
- A `Trace` is created per top-level `ForwardAsync` call.
- Each `PredictAsync` call appends one `TraceEntry`.
- The optimizer collects traces from successful training examples and fills `predictor.Demos` via `AddDemo(object input, object output)`.

### 2.5 Retry on LmpAssert Failure

When `LmpAssert.That()` fails inside a `validate` delegate, the predictor re-invokes the LM with the assertion error message appended to the prompt. This is **backtracking** — the predictor retries itself, not an external repair step.

The retry loop is built into `PredictAsync` via the `validate` parameter:

```csharp
public virtual async Task<TOutput> PredictAsync(
    TInput input,
    Trace? trace = null,
    Action<TOutput>? validate = null,
    int maxRetries = 3,
    CancellationToken cancellationToken = default)
{
    string? lastError = null;

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        var messages = BuildMessages(input, lastError);

        var response = await _client.GetResponseAsync<TOutput>(
            messages, Config, cancellationToken: cancellationToken);

        var result = response.Result
            ?? throw new InvalidOperationException(
                $"Predictor '{Name}': structured output returned null.");

        trace?.Record(Name, input!, result);

        if (validate is null)
            return result;

        try
        {
            validate(result);
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

> **How `lastError` reaches the prompt:** `BuildMessages()` passes `lastError` to the `MessageBuilder` delegate (or `BuildDefaultMessages`). When non-null, it appends a feedback section to the final user message: `"Previous attempt failed: {lastError}. Try again."` This gives the LM context to self-correct.

**Usage in `ForwardAsync`:**

```csharp
public override async Task<DraftReply> ForwardAsync(
    TicketInput input, CancellationToken cancellationToken = default)
{
    var classification = await _classify.PredictAsync(
        input, Trace,
        validate: c => LmpAssert.That(c,
            x => x.Urgency >= 1 && x.Urgency <= 5,
            "Urgency must be between 1 and 5"),
        maxRetries: 3,
        cancellationToken: cancellationToken);

    return await _draft.PredictAsync(
        classification, Trace, cancellationToken: cancellationToken);
}
```

When the `validate` delegate is `null`, no retry loop runs — `PredictAsync` returns the first result. When `validate` is provided and throws `LmpAssertionException`, the predictor retries with the error message in context. After `maxRetries` failures, `LmpMaxRetriesExceededException` is thrown.

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

LMP provides two base classes:

- **`LmpModule`** — untyped base with `ForwardAsync(object, CancellationToken)`
- **`LmpModule<TInput, TOutput>`** — typed base with `ForwardAsync(TInput, CancellationToken)` that bridges to the untyped version automatically

```csharp
public partial class TicketTriageModule : LmpModule<TicketInput, DraftReply>
{
    public TicketTriageModule(IChatClient client) { Client = client; }

    [Predict]
    public partial Task<ClassifyTicket> ClassifyAsync(TicketInput input);

    [Predict]
    public partial Task<DraftReply> DraftAsync(ClassifyTicket classification);

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input,
        CancellationToken cancellationToken = default)
    {
        var classification = await ClassifyAsync(input);

        LmpAssert.That(classification,
            c => c.Urgency >= 1 && c.Urgency <= 5,
            "Urgency must be between 1 and 5");

        return await DraftAsync(classification);
    }
}
```

> **`[Predict]` attribute:** Marks partial methods for source-generated predictor wiring. The generator emits backing `Predictor<TInput, TOutput>` fields that are lazily initialized from `LmpModule.Client`. No manual predictor construction needed. The containing class must be `partial`.

**Execution model:** `ForwardAsync()` is plain `async/await`. Parallelism is `Task.WhenAll`. Branching is `if/else`. Loops are `for/while`. The C# compiler handles control flow — LMP does not reimplement it.

### 3.2 GetPredictors() — Source-Generator Emitted

The source generator emits `GetPredictors()` to enumerate all `Predictor` fields. Optimizers call this to discover learnable parameters without reflection. Returns a list of `(string Name, IPredictor Predictor)` tuples.

```csharp
// Source-generated — emitted as a partial method on TicketTriageModule
public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
{
    return [
        ("ClassifyAsync", __predict_ClassifyAsync),
        ("DraftAsync", __predict_DraftAsync)
    ];
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

    PredictorState GetState();
    void LoadState(PredictorState state);
    void AddDemo(object input, object output);
    IPredictor Clone();
}
```

The non-generic `IPredictor` interface lets optimizers iterate over predictors without knowing `TInput`/`TOutput` at compile time. `Demos` is typed as `IList` — the concrete `List<(TInput Input, TOutput Output)>` satisfies this. `AddDemo(object, object)` provides type-erased demo addition used by optimizers working with `Trace` entries. `Clone()` creates an independent copy with separate learnable state.

### 3.3 SaveAsync / LoadAsync — JSON State Persistence

State persistence serializes each predictor's learnable parameters (instructions + demos) to JSON. Uses `ModuleStateSerializerContext` (source-generated `JsonSerializerContext`) for AOT-safe, reflection-free serialization.

**State types:**

```csharp
public sealed record ModuleState
{
    public required string Version { get; init; }       // "1.0"
    public required string Module { get; init; }        // Type name
    public required Dictionary<string, PredictorState> Predictors { get; init; }
}

public sealed record PredictorState
{
    public required string Instructions { get; init; }
    public required List<DemoEntry> Demos { get; init; }
    public Dictionary<string, JsonElement>? Config { get; init; }
}

public sealed record DemoEntry
{
    public required Dictionary<string, JsonElement> Input { get; init; }
    public required Dictionary<string, JsonElement> Output { get; init; }
}
```

**SaveAsync (atomic write: temp file → rename):**

```csharp
public virtual async Task SaveAsync(string path, CancellationToken cancellationToken = default)
{
    var state = new ModuleState
    {
        Version = "1.0",
        Module = GetType().Name,
        Predictors = GetPredictors().ToDictionary(
            p => p.Name,
            p => p.Predictor.GetState())
    };

    byte[] json = JsonSerializer.SerializeToUtf8Bytes(
        state, ModuleStateSerializerContext.Default.ModuleState);

    string tempPath = path + ".tmp";
    await File.WriteAllBytesAsync(tempPath, json, cancellationToken);
    File.Move(tempPath, path, overwrite: true);
}
```

**LoadAsync:**

```csharp
public virtual async Task LoadAsync(string path, CancellationToken cancellationToken = default)
{
    byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);

    var state = JsonSerializer.Deserialize(
        bytes, ModuleStateSerializerContext.Default.ModuleState)
        ?? throw new InvalidOperationException(
            $"Failed to deserialize module state from '{path}'.");

    foreach (var (name, predictor) in GetPredictors())
    {
        if (state.Predictors.TryGetValue(name, out var predictorState))
        {
            predictor.LoadState(predictorState);
        }
    }
}
```

**JSON format (human-readable, diff-friendly):**

```json
{
  "Version": "1.0",
  "Module": "TicketTriageModule",
  "Predictors": {
    "ClassifyAsync": {
      "Instructions": "Classify a support ticket by category and urgency",
      "Demos": [
        {
          "Input": { "ticketText": "I was charged twice" },
          "Output": { "category": "billing", "urgency": 4 }
        }
      ],
      "Config": null
    },
    "DraftAsync": {
      "Instructions": "Draft a helpful reply to the customer",
      "Demos": [],
      "Config": null
    }
  }
}
```

### 3.4 Trace Property

`LmpModule` exposes a `Trace` property that `ForwardAsync` passes to each `PredictAsync` call. The optimizer creates a fresh `Trace` before each training run and reads it after.

```csharp
public abstract class LmpModule
{
    protected IChatClient? Client { get; set; }
    public Trace? Trace { get; set; }

    public abstract Task<object> ForwardAsync(
        object input, CancellationToken cancellationToken = default);

    public virtual IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => [];

    public TModule Clone<TModule>() where TModule : LmpModule;
    public virtual Task SaveAsync(string path, CancellationToken ct = default);
    public virtual Task LoadAsync(string path, CancellationToken ct = default);
}

public abstract class LmpModule<TInput, TOutput> : LmpModule
{
    public abstract Task<TOutput> ForwardAsync(
        TInput input, CancellationToken cancellationToken = default);

    // Sealed bridge: routes untyped ForwardAsync to the typed overload
    public sealed override async Task<object> ForwardAsync(
        object input, CancellationToken cancellationToken = default)
        => (object)(await ForwardAsync((TInput)input, cancellationToken))!;
}
```

---

## 4. Reasoning Strategies

Reasoning strategies are thin wrappers around `Predictor<TInput, TOutput>`. Each lives in `LMP.Modules` and is under 100 lines of code. They do not introduce new execution primitives — they compose the existing ones.

### 4.1 ChainOfThought\<TInput, TOutput\>

Asks the LM to produce step-by-step reasoning before the final answer. Extends `Predictor<TInput, TOutput>` and overrides `PredictAsync` to call the LM with a `ChainOfThoughtResult<TOutput>` wrapper type.

**Mechanism:** The generic `ChainOfThoughtResult<TOutput>` wrapper prepends a `Reasoning` field:

```csharp
public sealed class ChainOfThoughtResult<TOutput> where TOutput : class
{
    [Description("Think step by step to work toward the answer")]
    [JsonPropertyOrder(-1)]
    public required string Reasoning { get; init; }

    public required TOutput Result { get; init; }
}
```

At runtime, `ChainOfThought` inherits from `Predictor<TInput, TOutput>` and overrides `PredictAsync`:

```csharp
public class ChainOfThought<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly IChatClient _chatClient;

    public ChainOfThought(IChatClient client) : base(client)
    {
        _chatClient = client;
    }

    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        string? lastError = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var messages = BuildCoTMessages(input, lastError);

            var response = await _chatClient
                .GetResponseAsync<ChainOfThoughtResult<TOutput>>(
                    messages, Config, cancellationToken: cancellationToken);

            var extended = response.Result ?? throw new InvalidOperationException(
                $"ChainOfThought '{Name}': structured output returned null.");
            var result = extended.Result ?? throw new InvalidOperationException(
                $"ChainOfThought '{Name}': Result field was null.");

            // Record unwrapped TOutput (not wrapper) — avoids InvalidCastException
            // when optimizers call AddDemo(input, output) casting to TOutput.
            trace?.Record(Name, input!, result);

            if (validate is null) return result;

            try { validate(result); return result; }
            catch (LmpAssertionException ex) { lastError = ex.Message; }
        }

        throw new LmpMaxRetriesExceededException(Name, maxRetries);
    }
}
```

**Key detail:** The caller gets `TOutput`. The reasoning is captured in the LM call but only the unwrapped `TOutput` is recorded in the trace. This is critical for optimizer compatibility — `AddDemo` casts to `TOutput`, not `ChainOfThoughtResult<TOutput>`.

**CoT instruction injection:** `BuildCoTMessages` calls the base `BuildMessages`, then appends `"Let's think step by step."` to the system message.

### 4.2 BestOfN\<TInput, TOutput\>

Runs N parallel predictions and returns the one that scores highest on a reward function. Inherits from `Predictor<TInput, TOutput>` — all N predictions share the same learnable state (Instructions, Demos, Config).

```csharp
public class BestOfN<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly int _n;
    private readonly Func<TInput, TOutput, float> _reward;

    public BestOfN(IChatClient client, int n, Func<TInput, TOutput, float> reward)
        : base(client)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentNullException.ThrowIfNull(reward);
        _n = n;
        _reward = reward;
    }

    public int N => _n;

    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        // True parallelism — no GIL. Each prediction runs concurrently.
        var tasks = Enumerable.Range(0, _n)
            .Select(_ => base.PredictAsync(
                input, trace, cancellationToken: cancellationToken));

        var candidates = await Task.WhenAll(tasks);

        // Score each candidate and return the best
        var best = candidates
            .OrderByDescending(c => _reward(input, c))
            .First();

        validate?.Invoke(best);
        return best;
    }
}
```

**Why `Task.WhenAll`:** .NET has true OS-level parallelism. All N predictions run concurrently against the LM API. This is a structural advantage over Python's GIL-constrained `asyncio.gather`.

### 4.3 Refine\<TInput, TOutput\>

Iterative improvement loop: predict → send to refiner with original input and previous output → repeat. Inherits from `Predictor<TInput, TOutput>` — the initial prediction uses the base predictor's state.

```csharp
public class Refine<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly Predictor<RefineCritiqueInput<TOutput>, TOutput> _refiner;
    private readonly int _maxIterations;

    public Refine(IChatClient client, int maxIterations = 2) : base(client)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        _maxIterations = maxIterations;
        _refiner = new Predictor<RefineCritiqueInput<TOutput>, TOutput>(client)
        {
            Instructions = "Given the original input, a previous attempt, " +
                           "and a critique, produce an improved output."
        };
    }

    public int MaxIterations => _maxIterations;

    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        // Initial prediction using the base predictor
        var current = await base.PredictAsync(
            input, trace, cancellationToken: cancellationToken);

        for (int i = 0; i < _maxIterations; i++)
        {
            var critiqueInput = new RefineCritiqueInput<TOutput>(
                OriginalInput: input!,
                PreviousOutput: current);

            current = await _refiner.PredictAsync(
                critiqueInput, trace, cancellationToken: cancellationToken);
        }

        validate?.Invoke(current);
        return current;
    }
}

public record RefineCritiqueInput<TOutput>(
    [property: Description("The original input")] object OriginalInput,
    [property: Description("The previous attempt to improve upon")] TOutput PreviousOutput);
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

`ReActAgent<TInput, TOutput>` inherits from `Predictor<TInput, TOutput>` and wraps the provided `IChatClient` with `FunctionInvokingChatClient` for automatic tool dispatch:

```csharp
public class ReActAgent<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly IChatClient _wrappedClient;
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly int _maxSteps;

    public ReActAgent(
        IChatClient client,
        IEnumerable<AIFunction> tools,
        int maxSteps = 5) : base(client)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

        _tools = tools.ToList();
        _maxSteps = maxSteps;
        _wrappedClient = new FunctionInvokingChatClient(client)
        {
            MaximumIterationsPerRequest = maxSteps
        };
    }

    public int MaxSteps => _maxSteps;
    public IReadOnlyList<AIFunction> Tools => _tools;

    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        string? lastError = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var messages = BuildAgentMessages(input, lastError);

            var options = new ChatOptions
            {
                Tools = [.. _tools],
                Temperature = Config.Temperature,
                MaxOutputTokens = Config.MaxOutputTokens,
                TopP = Config.TopP,
                FrequencyPenalty = Config.FrequencyPenalty,
                PresencePenalty = Config.PresencePenalty,
                ModelId = Config.ModelId,
            };

            var response = await _wrappedClient.GetResponseAsync<TOutput>(
                messages, options, cancellationToken: cancellationToken);

            var result = response.Result
                ?? throw new InvalidOperationException(
                    $"ReActAgent '{Name}': structured output returned null.");

            trace?.Record(Name, input!, result);

            if (validate is null) return result;

            try { validate(result); return result; }
            catch (LmpAssertionException ex) { lastError = ex.Message; }
        }

        throw new LmpMaxRetriesExceededException(Name, maxRetries);
    }
}
```

**Key design:** `FunctionInvokingChatClient` wraps the chat client with `MaximumIterationsPerRequest` set to `maxSteps`. The middleware handles the Think → Act → Observe loop internally. `BuildAgentMessages` appends a ReAct instruction to the system message built by the base class.

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
        string? message = null)
    {
        if (!predicate(result))
        {
            throw new LmpAssertionException(
                message ?? "LMP assertion failed.", result);
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

public class LmpMaxRetriesExceededException : Exception
{
    public string PredictorName { get; }
    public int MaxRetries { get; }

    public LmpMaxRetriesExceededException(string predictorName, int maxRetries)
        : base($"Predictor '{predictorName}' exceeded {maxRetries} retries.")
    {
        PredictorName = predictorName;
        MaxRetries = maxRetries;
    }
}
```

**Retry mechanism:** The retry loop is built into `PredictAsync` via the `validate` parameter — not an external loop. The developer passes assertions as a delegate:

```csharp
public override async Task<DraftReply> ForwardAsync(
    TicketInput input, CancellationToken cancellationToken = default)
{
    var classification = await _classify.PredictAsync(
        input, Trace,
        validate: c => LmpAssert.That(c,
            x => x.Urgency >= 1 && x.Urgency <= 5,
            "Urgency must be between 1 and 5"),
        cancellationToken: cancellationToken);

    return await _draft.PredictAsync(
        classification, Trace, cancellationToken: cancellationToken);
}
```

Alternatively, assertions can be placed after `PredictAsync` for cases where you want the exception to propagate to the caller:

```csharp
var classification = await _classify.PredictAsync(input, Trace,
    cancellationToken: cancellationToken);

// Throws LmpAssertionException if the check fails — does NOT retry
LmpAssert.That(classification,
    c => c.Urgency >= 1 && c.Urgency <= 5,
    "Urgency must be between 1 and 5");

return await _draft.PredictAsync(classification, Trace,
    cancellationToken: cancellationToken);
```

### 6.2 LmpSuggest — Fail → Return False, Continue

```csharp
public static class LmpSuggest
{
    public static bool That<T>(
        T result,
        Func<T, bool> predicate,
        string? message = null)
    {
        return predicate(result);
    }
}
```

**Usage:**

```csharp
var classification = await _classify.PredictAsync(input, Trace);

// Soft check — returns false if category is "unknown", but continues
bool categoryOk = LmpSuggest.That(classification,
    c => c.Category != "unknown",
    "Category should not be 'unknown'");

return await _draft.PredictAsync(classification, Trace);
```

**Design:** `LmpSuggest` never throws. It returns a `bool` indicating whether the predicate passed. The caller can inspect the result or ignore it. During optimization, the optimizer can check suggest outcomes as soft signals for candidate ranking.

### 6.3 Assertion Summary

| Type | On Failure | Use Case |
|---|---|---|
| `LmpAssert.That()` | Throws `LmpAssertionException` → retry with feedback (when used via `validate` param) | Hard constraints: valid ranges, required formats |
| `LmpSuggest.That()` | Returns `false`, continues | Soft preferences: style guidelines, optional fields |

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
    protected IChatClient? Client { get; set; }
    public Trace? Trace { get; set; }
    public virtual IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => [];
    public abstract Task<object> ForwardAsync(object input, CancellationToken ct = default);
    public TModule Clone<TModule>() where TModule : LmpModule;
    public virtual Task SaveAsync(string path, CancellationToken ct = default);
    public virtual Task LoadAsync(string path, CancellationToken ct = default);
}

public abstract class LmpModule<TInput, TOutput> : LmpModule
{
    public abstract Task<TOutput> ForwardAsync(TInput input, CancellationToken ct = default);
    // Sealed bridge to untyped ForwardAsync
}

public interface IPredictor
{
    string Name { get; set; }
    string Instructions { get; set; }
    IList Demos { get; }
    ChatOptions Config { get; set; }
    PredictorState GetState();
    void LoadState(PredictorState state);
    void AddDemo(object input, object output);
    IPredictor Clone();
}

public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
{
    public IChatClient Client { get; }
    public string Name { get; set; }
    public string Instructions { get; set; }
    public List<(TInput Input, TOutput Output)> Demos { get; set; }
    public ChatOptions Config { get; set; }
    public JsonSerializerOptions? SerializerOptions { get; set; }

    public virtual Task<TOutput> PredictAsync(
        TInput input, Trace? trace = null,
        Action<TOutput>? validate = null, int maxRetries = 3,
        CancellationToken cancellationToken = default);

    public void SetPromptBuilder(
        Func<string, TInput, IReadOnlyList<(TInput, TOutput)>?,
             string?, IList<ChatMessage>> builder);
}

public sealed class Trace
{
    public IReadOnlyList<TraceEntry> Entries { get; }
    public void Record(string name, object input, object output);
}

public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);

// === Examples (training/validation data) ===

public abstract record Example
{
    public abstract object WithInputs();
    public abstract object GetLabel();
    public static IReadOnlyList<Example<TInput, TLabel>>
        LoadFromJsonl<TInput, TLabel>(string path, JsonSerializerOptions? options = null);
}

public sealed record Example<TInput, TLabel>(TInput Input, TLabel Label) : Example;

// === Retrieval ===

public interface IRetriever
{
    Task<string[]> RetrieveAsync(string query, int k = 5,
        CancellationToken cancellationToken = default);
}

// === Assertions ===

public static class LmpAssert
{
    public static void That<T>(T result, Func<T, bool> predicate, string? message = null);
}

public static class LmpSuggest
{
    public static bool That<T>(T result, Func<T, bool> predicate, string? message = null);
}

public class LmpAssertionException(string message, object? failedResult)
    : Exception(message)
{
    public object? FailedResult { get; } = failedResult;
}

public class LmpMaxRetriesExceededException(string predictorName, int maxRetries)
    : Exception($"Predictor '{predictorName}' exceeded {maxRetries} retries.")
{
    public string PredictorName { get; } = predictorName;
    public int MaxRetries { get; } = maxRetries;
}

// === State persistence ===

public sealed record ModuleState
{
    public required string Version { get; init; }
    public required string Module { get; init; }
    public required Dictionary<string, PredictorState> Predictors { get; init; }
}

public sealed record PredictorState
{
    public required string Instructions { get; init; }
    public required List<DemoEntry> Demos { get; init; }
    public Dictionary<string, JsonElement>? Config { get; init; }
}

public sealed record DemoEntry
{
    public required Dictionary<string, JsonElement> Input { get; init; }
    public required Dictionary<string, JsonElement> Output { get; init; }
}

// === Evaluation ===

public static class Evaluator
{
    // Untyped (base)
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module, IReadOnlyList<Example> devSet,
        Func<Example, object, float> metric,
        int maxConcurrency = 4, CancellationToken ct = default)
        where TModule : LmpModule;

    // Typed (float metric)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, float> metric,
        int maxConcurrency = 4, CancellationToken ct = default);

    // Typed (bool metric → 0/1)
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, bool> metric,
        int maxConcurrency = 4, CancellationToken ct = default);

    // Async overloads for LLM-as-judge metrics
    public static Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module, IReadOnlyList<Example> devSet,
        Func<Example, object, Task<float>> metric,
        int maxConcurrency = 4, CancellationToken ct = default)
        where TModule : LmpModule;
}

// Uses System.Numerics.Tensors.TensorPrimitives for Average/Min/Max aggregation.

public sealed record EvaluationResult(
    IReadOnlyList<ExampleResult> PerExample,
    float AverageScore, float MinScore, float MaxScore, int Count);

public sealed record ExampleResult(Example Example, object Output, float Score);
```
