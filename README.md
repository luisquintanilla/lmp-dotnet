# LMP — Language Model Programs for .NET

> **Turn enterprise AI workflows from prompt strings and notebook experiments into typed, compilable, testable, observable software artifacts.**

LMP is a .NET 10 / C# 14 framework for authoring, optimizing, and deploying language model programs. Inspired by [DSPy](https://dspy.ai/) (Stanford NLP), LMP brings programmatic LM optimization to the .NET ecosystem with native innovations that go beyond what Python frameworks offer.

## Why LMP?

Today, most enterprise AI features are built with:
- 🔴 **Prompt strings** copy-pasted between notebooks and production code
- 🔴 **Manual tuning** — hours of trial-and-error with model parameters
- 🔴 **No versioning** — no way to know what's running in production
- 🔴 **No testing** — changes go live without systematic evaluation

LMP treats LM programs as **software artifacts** with the same discipline as any other enterprise code:

- ✅ **Typed signatures** — Compile-time validated input/output contracts (no more string parsing)
- ✅ **Automatic optimization** — A compiler searches over instruction variants, few-shot examples, model choices, and temperature settings to maximize your metrics
- ✅ **Compiled artifacts** — Versioned JSON packages that pin every tuned parameter, so what passed evaluation is exactly what runs in production
- ✅ **Observable** — Built-in OpenTelemetry tracing, cost tracking, and evaluation metrics
- ✅ **Hot-swappable** — Deploy new artifact versions without restarting your service

## Architecture

LMP uses a **Three-Layer Build Architecture** that integrates into the standard `dotnet build` pipeline:

| Layer | When | What | Cost |
|-------|------|------|------|
| **Source Generators** | Inside `dotnet build` (Roslyn) | Validate signatures, emit descriptors, IDE red squiggles | Free |
| **MSBuild Targets** | After `dotnet build` (post-compile) | Emit IR, validate program graph, embed artifacts | Free |
| **CLI Optimizer** | `dotnet lmp compile` (explicit) | Run optimization trials against LM APIs | $$$ |

## Quick Start

```csharp
// 1. Define a typed signature
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Input(Description = "The raw ticket text from the customer")]
    public required string TicketText { get; init; }

    [Output(Description = "Category: billing, technical, account, or general")]
    public required string Category { get; init; }

    [Output(Description = "Urgency level from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

// 2. Build → source generator validates and emits descriptors
// 3. Run → works immediately with defaults
// 4. Compile → optimize for production
//    dotnet lmp compile --program SupportTriage --data eval-data.json
// 5. Deploy → compiled artifact pins all tuned parameters
```

## Key Dependencies

LMP depends **only** on [`IChatClient`](https://www.nuget.org/packages/Microsoft.Extensions.AI) from `Microsoft.Extensions.AI`. It does not require Semantic Kernel, Microsoft Agent Framework, or any specific LLM provider.

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.AI` | `IChatClient` abstraction for any LLM provider |
| `Microsoft.Extensions.AI.Evaluation.Quality` | Built-in groundedness, relevance, coherence evaluators |
| `System.Threading.Tasks.Dataflow` | TPL Dataflow for graph execution |
| `Microsoft.Extensions.Resilience` | Retry, circuit breaker, rate limiting for LM API calls |

## Target Platform

- **.NET 10 LTS** (GA November 2025)
- **C# 14** — Extension members, interceptors, field-backed properties
- **Native AOT compatible** — Source generators eliminate reflection

## Documentation

| Document | Description |
|----------|-------------|
| [`spec.org`](spec.org) | Master implementation blueprint (Emacs Org-mode) |
| [`docs/00-product/prd.md`](docs/00-product/prd.md) | Product requirements with cited business case |
| [`docs/01-architecture/`](docs/01-architecture/) | System architecture, phased implementation plan |
| [`docs/02-specs/`](docs/02-specs/) | Public API, source generators, runtime, compiler, MSBuild targets, IR, artifact format, CLI, diagnostics |
| [`docs/03-implementation/`](docs/03-implementation/) | Repository layout, testing strategy |
| [`docs/04-demo/`](docs/04-demo/) | MVP demo script |

## Status

📐 **Specification phase** — All design documents are complete and grounded against the .NET 10 platform. Implementation has not yet started.

## Relationship to DSPy

LMP adopts DSPy's core thesis — that LM programs should be optimized programmatically — but is **not a port**. Approximately 60% of LMP's architecture is original .NET innovation with no Python equivalent:

- **Compile-time program validation** (Python: impossible — no compilation step)
- **Three-tier binding** (convention → attributes → interceptors → expression trees)
- **True parallelism** (.NET has no GIL)
- **Compiled, hot-swappable deployment artifacts** (Python: pickle or ad-hoc JSON)
- **Native AOT** (50ms cold start vs 2-5s for Python)

## License

[MIT](LICENSE)
