# LMP — Language Model Programs for .NET

> **Building blocks that compile** — bring DSPy's optimization insight to .NET with compile-time superpowers.

## What is LMP?

[DSPy](https://dspy.ai/) proved that language model programs should be **optimized programmatically**, not hand-tuned with prompt strings. Instead of tweaking instructions by hand, you define typed signatures and let an optimizer search over few-shot examples and instructions to maximize your metric.

LMP brings this insight to .NET — and takes it further. C# source generators **validate your signatures at build time**, emit typed prompt builders, and produce zero-reflection serialization. The result: LM programs that are checked by the compiler, discoverable by tooling, and deployable with Native AOT.

Built on .NET 10 / C# 14. The only LM dependency is [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient) from `Microsoft.Extensions.AI` — works with OpenAI, Anthropic, Ollama, or any provider.

## Quick Example

```csharp
// 1. Define input/output types
public record Question([Description("The question")] string Text);

[LmpSignature("Answer concisely")]
public partial record Answer
{
    [Description("A short factoid answer")]
    public required string Text { get; init; }
}

// 2. Create a predictor and call it
var qa = new Predictor<Question, Answer>(chatClient);
var result = await qa.PredictAsync(new Question("What is the capital of France?"));
// result.Text → "Paris"

// 3. Add chain-of-thought reasoning
var cot = new ChainOfThought<Question, Answer>(chatClient);
var reasoned = await cot.PredictAsync(new Question("What is 15% of 80?"));
// reasoned.Reasoning → "15% of 80 = 0.15 × 80 = 12"
// reasoned.Value.Text → "12"

// 4. Compose into a module and optimize
var module = new MyModule(chatClient);
var optimizer = new BootstrapFewShot(chatClient, maxDemos: 4);
var optimized = await optimizer.CompileAsync(module, trainSet, metric);
await optimized.SaveAsync("my-module.json");
```

## What the Source Generator Does

When you hit **Build**, the Roslyn source generator reads your `[LmpSignature]` types and emits:

- **`PromptBuilder`** — assembles `ChatMessage[]` from instructions, demos, and input fields
- **`JsonTypeInfo<T>`** — zero-reflection JSON serialization for structured output
- **`GetPredictors()`** — zero-reflection predictor discovery so optimizers can find every predictor in a module
- **`Clone()`** — deep-copy support for modules so optimizers can trial candidates independently
- **Diagnostics** — IDE warnings/errors for missing descriptions, non-serializable types, and invalid signatures
- **C# 14 interceptors** — opt-in zero-dispatch `PredictAsync` inlining at call sites

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
| `ReActAgent<TIn, TOut>` | Agents | Think → Act → Observe loop using `AIFunction` tools |
| `ProgramOfThought<TIn, TOut>` | Reasoning | LM generates C# code → Roslyn scripting executes → typed result |

## Optimizer Catalog

| Optimizer | What It Does |
|---|---|
| `Evaluator` | Runs a module on a dev set, scores with a metric, returns `EvaluationResult` |
| `BootstrapFewShot` | Collects successful traces as few-shot demos for each predictor |
| `BootstrapRandomSearch` | Runs BootstrapFewShot × N candidates, returns the best |
| `MIPROv2` | Bayesian optimization (TPE) over both instructions and demos |

## CLI Tool

Install and use the `dotnet lmp` CLI for optimization workflows:

```bash
# Inspect a saved module's parameters
dotnet lmp inspect my-module.json

# Optimize a module
dotnet lmp optimize --module MyModule --optimizer BootstrapRandomSearch \
  --train data/train.jsonl --dev data/dev.jsonl --output optimized.json

# Evaluate on a dataset
dotnet lmp eval --module MyModule --params optimized.json --data data/dev.jsonl

# Run a single input through a module
dotnet lmp run --module MyModule --input '{"Text": "hello"}'
```

## Assertions & Guardrails

```csharp
var result = await predictor.PredictAsync(input);

// Hard assertion — triggers retry with error context if failed
LmpAssert.That(result, r => r.Urgency >= 1 && r.Urgency <= 5,
    "Urgency must be between 1 and 5");

// Soft suggestion — returns false but never throws
LmpSuggest.That(result, r => r.Category != "unknown",
    "Category should not be unknown");
```

## Project Structure

```
src/
├── LMP.Abstractions/      # Interfaces, attributes, base types (no dependencies)
├── LMP.Core/               # Predictor<TIn,TOut> — the core primitive
├── LMP.SourceGen/          # Roslyn IIncrementalGenerator (netstandard2.0)
├── LMP.Modules/            # ChainOfThought, BestOfN, Refine, ReActAgent, ProgramOfThought
├── LMP.Optimizers/         # Evaluator, BootstrapFewShot, BootstrapRandomSearch, MIPROv2
├── LMP.Cli/                # CLI tool: inspect, optimize, eval, run
└── LMP.Aspire.Hosting/     # Aspire dashboard integration for optimization runs
test/                       # xUnit tests for each project (865 tests)
samples/
└── LMP.Samples.TicketTriage/  # End-to-end demo with mock client
```

## Getting Started

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.201+)

```bash
# Clone and build
git clone https://github.com/luisquintanilla/lmp-dotnet.git
cd lmp-dotnet
dotnet build
dotnet test

# Run the sample (no API key needed — uses mock client)
dotnet run --project samples/LMP.Samples.TicketTriage
```

Reference LMP in your project:

```xml
<ItemGroup>
  <PackageReference Include="LMP.Core" />
  <PackageReference Include="LMP.Modules" />       <!-- Optional: ChainOfThought, ReAct, etc. -->
  <PackageReference Include="LMP.Optimizers" />     <!-- Optional: evaluation & optimization -->
</ItemGroup>
```

## Diagnostics

| Code | Severity | Description |
|---|---|---|
| **LMP001** | Warning | `[LmpSignature]` output property is missing a `[Description]` attribute |
| **LMP002** | Error | Output property type is not serializable by System.Text.Json |
| **LMP003** | Error | `[LmpSignature]` applied to a type that is not a `partial record` |

## Dependencies

- [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/) 10.4.1 — `IChatClient`, `GetResponseAsync<T>`, `AIFunction`
- [`Microsoft.CodeAnalysis.CSharp`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/) 5.3.0 — source generator (build-time only)
- [`Microsoft.CodeAnalysis.CSharp.Scripting`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting/) 5.3.0 — `ProgramOfThought` code execution

## Design Documents

Full architecture and specs live in [`docs/`](docs/):

- [`system-architecture.md`](docs/01-architecture/system-architecture.md) — architecture overview
- [`public-api.md`](docs/02-specs/public-api.md) — complete API surface specification
- [`phased-plan.md`](docs/01-architecture/phased-plan.md) — implementation phases
- [`source-generator.md`](docs/02-specs/source-generator.md) — source generator internals
- [`compiler-optimizer.md`](docs/02-specs/compiler-optimizer.md) — optimizer design

## Status

✅ **All 8 phases complete** — 865 tests passing. Core framework, source generator, reasoning modules, optimizers, CLI tool, Aspire integration, interceptors, and `[Predict]` sugar are all implemented.

## License

[MIT](LICENSE)
