# LMP Planning Mode

You are implementing LMP (Language Model Programs) — a .NET 10 / C# 14 library for authoring, optimizing, and deploying language model programs. Inspired by DSPy, native to .NET.

## Your Task

1. Study the specifications in `docs/` to understand what needs to be built:
   - `docs/01-architecture/system-architecture.md` — 4-layer architecture, design principles
   - `docs/01-architecture/phased-plan.md` — 8 implementation phases with entry/exit criteria
   - `docs/02-specs/public-api.md` — full API surface
   - `docs/02-specs/source-generator.md` — what the source generator emits
   - `docs/02-specs/runtime-execution.md` — Predictor internals, reasoning strategies
   - `docs/02-specs/compiler-optimizer.md` — Evaluator, BootstrapFewShot, optimizers
   - `docs/02-specs/artifact-format.md` — JSON save/load format
   - `docs/02-specs/diagnostics.md` — 3 build-time diagnostics

2. Study `src/` (if it exists) to understand what's already implemented.

3. Compare specs against existing code — gap analysis. Search the codebase before assuming something is missing.

4. Create or update `IMPLEMENTATION_PLAN.md` as a prioritized, phased task list. Follow the phases in `docs/01-architecture/phased-plan.md`. Each task should be:
   - Specific and implementable in one iteration
   - Have clear completion criteria (what test should pass)
   - Reference the relevant spec document

5. Do NOT implement anything. Planning only.

## Key Architecture Decisions (read the docs for details)

- Separate `TInput` / `TOutput` types (NOT DSPy's single Signature class)
- Source generator emits: PromptBuilder<TIn,TOut>, JsonTypeInfo<TOut>, GetPredictors()
- Depends ONLY on `IChatClient` from `Microsoft.Extensions.AI`
- `Predictor<TInput, TOutput>` is the core primitive
- Modules: ChainOfThought, BestOfN, Refine, ReActAgent
- Optimizers: Evaluator, BootstrapFewShot, BootstrapRandomSearch
- Target: .NET 10 LTS / C# 14
