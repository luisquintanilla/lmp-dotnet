# LMP — Agent Operational Guide

## Project

LMP (Language Model Programs) — .NET 10 / C# 14 library for optimizable LM programs.
Repository: https://github.com/luisquintanilla/lmp-dotnet

## Solution Structure

```
src/
├── LMP.Abstractions/      # Interfaces, attributes, base types (no dependencies)
├── LMP.Core/               # Predictor<TIn,TOut>, LmpModule, assertions
├── LMP.SourceGen/          # Roslyn IIncrementalGenerator
├── LMP.Modules/            # ChainOfThought, BestOfN, Refine, ReActAgent
├── LMP.Optimizers/         # Evaluator, BootstrapFewShot, BootstrapRandomSearch, MIPROv2
└── LMP.Cli/                # CLI tool (dotnet lmp): inspect, optimize, eval
test/
├── LMP.Abstractions.Tests/
├── LMP.Core.Tests/
├── LMP.SourceGen.Tests/
├── LMP.Modules.Tests/
├── LMP.Optimizers.Tests/
└── LMP.Cli.Tests/
```

## Build & Test

```bash
dotnet build
dotnet test
dotnet build --no-restore   # fast rebuild
```

## Dependencies

- `Microsoft.Extensions.AI` — IChatClient, GetResponseAsync<T>, AIFunction
- `Microsoft.Extensions.AI.Evaluation` — built-in evaluators (post-Phase 4)
- `Microsoft.CodeAnalysis` — source generator (LMP.SourceGen only)

## Conventions

- Target: `net10.0`
- Language: C# 14 (`<LangVersion>preview</LangVersion>`)
- Nullable: enabled everywhere
- XML doc comments on all public types and members
- Records for data types; classes for services
- `Async` suffix on all async methods
- Source generator: `IIncrementalGenerator` with `[Generator]` attribute
- Test framework: xUnit + Moq (or NSubstitute)

## Specs

All specifications live in `docs/`. The source of truth is:
- `docs/01-architecture/system-architecture.md` — architecture overview
- `docs/02-specs/public-api.md` — API surface
- `docs/01-architecture/phased-plan.md` — implementation phases
