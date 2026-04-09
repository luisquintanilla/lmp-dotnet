# LMP — Language Model Programs for .NET

> **Building blocks that compile** — bring DSPy's optimization insight to .NET with compile-time superpowers.

## What is LMP?

[DSPy](https://dspy.ai/) proved that language model programs should be **optimized programmatically**, not hand-tuned with prompt strings. Instead of tweaking instructions by hand, you define typed signatures and let an optimizer search over few-shot examples and instructions to maximize your metric.

LMP brings this insight to .NET — and takes it further. C# source generators **validate your signatures at build time**, emit typed prompt builders, and produce zero-reflection serialization. The result: LM programs that are checked by the compiler, discoverable by tooling, and deployable with Native AOT.

Built on .NET 10 / C# 14. The only LM dependency is [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient) from `Microsoft.Extensions.AI` — works with OpenAI, Anthropic, Ollama, or any provider.

## Quick Example

```csharp
// Define types
public record Question([Description("The question")] string Text);

[LmpSignature("Answer concisely")]
public partial record Answer
{
    [Description("A short factoid answer")]
    public required string Text { get; init; }
}

// Use it
var qa = new Predictor<Question, Answer>(chatClient);
var result = await qa.PredictAsync(new Question("What is the capital of France?"));
// result.Text → "Paris"
```

## What the Source Generator Does

When you hit **Build**, the Roslyn source generator reads your `[LmpSignature]` types and emits:

- **`PromptBuilder<TIn, TOut>`** — assembles `ChatMessage[]` from instructions, demos, and input fields
- **`JsonTypeInfo<TOut>`** — zero-reflection JSON serialization for structured output
- **`GetPredictors()`** — zero-reflection predictor discovery so optimizers can find every predictor in a module
- **Diagnostics** — IDE red squiggles for missing descriptions, non-serializable types, and invalid signatures

## Five Things Python Can't Do

| # | Capability | Why It Matters |
|---|---|---|
| 1 | **Compile-time signature validation** | Catch errors at `dotnet build`, not at runtime after an API call |
| 2 | **Zero-reflection predictor discovery** | Optimizers enumerate all predictors without reflection or decorators |
| 3 | **Source-generated prompt builders** | Typed, inspectable prompt assembly — no string formatting at runtime |
| 4 | **AOT-deployable LM programs** | 50 ms cold start — no JIT, no reflection, no interpreter |
| 5 | **True parallelism in optimization** | `Task.WhenAll` across real threads — no GIL |

## Module Catalog

| Module | Layer | What It Does |
|---|---|---|
| `Predictor<TIn, TOut>` | Core | The fundamental building block — input → LM → typed output |
| `ChainOfThought<TIn, TOut>` | Reasoning | Adds a `Reasoning` field so the LM thinks step-by-step before answering |
| `BestOfN<TIn, TOut>` | Reasoning | Runs N predictions in parallel, returns the best by a reward function |
| `Refine<TIn, TOut>` | Reasoning | Predict → critique → predict again with critique context |
| `ReActAgent<TIn, TOut>` | Reasoning | Think → Act → Observe loop using `AIFunction` tools |
| `BootstrapFewShot` | Optimization | Collects successful traces as few-shot demos for each predictor |
| `BootstrapRandomSearch` | Optimization | BootstrapFewShot × N candidates — returns the best |
| `Evaluator` | Optimization | Runs a module on a dev set, scores with a metric, aggregates results |

## Status

📐 **Early design phase** — documentation-first. All architecture and specs are written; no implementation yet. See [`docs/`](docs/) for the full design.

## License

[MIT](LICENSE)
