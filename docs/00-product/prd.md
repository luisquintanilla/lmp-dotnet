# LMP (Language Model Programs) — Product Requirements Document

> **Status:** Draft | **Source of Truth:** `spec.org` (Sections 1–4, 17) | **Word Target:** 2000–3000

---

## 1. Executive Summary

**LMP** (Language Model Programs) is a .NET framework that turns enterprise AI workflows — today scattered across prompt strings, notebooks, and ad-hoc scripts — into **typed, compilable, testable, observable software artifacts**. It adopts DSPy's core insight that LM programs should be optimized programmatically rather than manually tuned, and extends it with .NET-native innovations: compile-time validation, directed program graphs, constraint-based optimization, and hot-swappable deployment artifacts. The result is a platform where AI-powered features can be authored, evaluated, optimized, versioned, and deployed with the same discipline as any other enterprise software. LMP produces **compiled artifacts** — versioned, deployable packages that pin every tuned parameter, instruction variant, and model choice so that what passed validation is exactly what runs in production.

> **One-sentence thesis:** We are building a .NET framework that turns enterprise AI workflows from prompt strings and notebook experiments into typed, compilable, testable, observable software artifacts.

---

## 2. The Problem (Written for Non-Experts)

### The Business Case

Enterprise AI is bleeding money. Not because the technology doesn't work — but because there is **no engineering discipline** for managing LM programs as software:

| Pain | Impact | Source |
|------|--------|--------|
| **Silent prompt drift** | 3–5× debugging time per incident; 20–40% of AI budgets wasted on unnecessary model swaps | InsightFinder 2024 [1], TrackAI 2025 [2] |
| **Manual optimization** | Substantial labor cost per program (LMP team estimate: ~$154K/yr based on 2 weeks × 10 cycles at blended rates) | LMP team estimate — see spec.org Section 1A for methodology |
| **Governance failure** | EU AI Act fines: up to €35M or 7% of global turnover for prohibited practices; up to €15M or 3% for high-risk violations | EU AI Act Article 99 [3] |
| **Enterprise AI failure** | 70–85% of GenAI deployments fail to meet ROI expectations | NTT Data 2024 [4] |

Only **~25%** of AI initiatives deliver expected ROI ([Google Cloud / Deloitte / McKinsey 2025][5]). GenAI spending is projected to grow from $16B (2023) to $143B+ by 2027 at 73% CAGR ([IDC][9]). Most AI projects stall at "pilot purgatory" — technically functional but operationally immature ([McKinsey][7]).

**Sources:**
- [1] https://insightfinder.com/blog/hidden-cost-llm-drift-detection/
- [2] https://trackai.dev/tracks/evaluations/model-drift/root-cause/
- [3] https://artificialintelligenceact.eu/article/99/
- [4] https://www.nttdata.com/global/en/insights/focus/2024/between-70-85p-of-genai-deployment-efforts-are-failing
- [5] https://services.google.com/fh/files/misc/google_cloud_roi_of_ai_2025.pdf
- [7] https://aigazine.com/industry/mckinsey-most-companies-are-stuck-in-ai-pilot-purgatory--s
- [9] https://www.computerworld.com/article/1637459/generative-ai-spending-to-reach-143b-in-2027-idc.html

### What Is an "LM Program"?

Imagine an enterprise **ticket triage system**. A customer submits a support ticket. The system must look up knowledge-base articles, classify the severity, route it to the right team, draft a grounded reply, decide if a human needs to intervene, and justify every decision. Each step calls a **language model** (like GPT-4) with carefully crafted instructions. Chained together, these steps form an **LM program** — a multi-step workflow where AI models do structured work under constraints.

### Why Current Approaches Break Down

Today, most teams build these workflows in Python notebooks or scripts. That works fine for a prototype. It falls apart at enterprise scale:

- **When your triage system handles 10,000 tickets/day and a prompt change breaks severity classification**, there's no compiler to catch it. The bug ships silently.
- **When a new team member adds a routing step but forgets to handle the "Critical" case**, Python gives zero warnings. The gap surfaces as a production incident.
- **When you need to prove to compliance that version 2.3 of your triage logic is exactly what's running in production**, there's no artifact, no hash, no versioned package — just a notebook someone ran last Thursday.
- **When optimization takes 4 hours and costs $200 in API calls**, you can't pause, inspect, or reproduce what happened. The process is a black box.

These aren't AI problems. They're **software engineering problems**. LMP solves them.

> **Why This Matters:** Every pain point above is a real conversation happening in enterprise AI/ML teams today. LMP doesn't make AI "smarter" — it makes AI **governable**. Organizations with mature AI governance save an average of $1.9M per data breach and reduce response times by 80 days ([IBM 2025 Cost of a Data Breach Report](https://newsroom.ibm.com/2025-07-30-ibm-report-13-of-organizations-reported-breaches-of-ai-models-or-applications,-97-of-which-reported-lacking-proper-ai-access-controls)). Stanford's AI Index 2025 documents a [56.4% year-over-year increase](https://hai.stanford.edu/ai-index/2025-ai-index-report/responsible-ai) in AI-related incidents, underscoring the urgency.

---

## 3. The Solution

### What LMP Does in Plain Language

LMP gives developers a structured way to **author** AI programs in C#, **build** them with compile-time validation, **run** them with full observability, **evaluate** quality on datasets, **optimize** automatically, **package** as versioned artifacts, and **deploy** with hot-swap capability. Every step produces inspectable output.

### The 7-Step Developer Story Arc

| Step | What Happens | Output |
|------|-------------|--------|
| **1. Author** | Developer writes a typed LM signature and program in C# | `.cs` source files |
| **2. Build** | Roslyn source generators emit metadata; analyzers catch errors in the IDE | `.g.cs` descriptors, diagnostics |
| **3. Run** | Runtime executes the program via `IChatClient` with OpenTelemetry traces | Structured trace output |
| **4. Evaluate** | Evaluators score outputs on a labeled dataset | Metric scores |
| **5. Compile** | Optimizer searches over instruction variants, few-shot examples, model choices, temperatures | Best valid variant |
| **6. Artifact** | System serializes the compiled variant as a versioned, hashed package | `.json` artifact file |
| **7. Deploy** | Runtime loads the pinned artifact; hot-swap supported without restart | Production execution |

### Code: Defining a Typed LM Signature

```csharp
using LMP;

[LmpSignature(
    Instructions = """
    You are a senior enterprise support triage assistant.

    Classify the issue severity, determine the owning team, and draft a grounded
    customer reply using only the provided evidence and policy context.

    If the evidence is insufficient, say so explicitly.
    """
)]
public partial class TriageTicket
{
    [Input(Description = "Raw customer issue or support ticket text")]
    public required string TicketText { get; init; }

    [Input(Description = "Customer plan tier such as Free, Pro, Enterprise")]
    public required string AccountTier { get; init; }

    [Input(Description = "Relevant knowledge base snippets")]
    public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

    [Input(Description = "Relevant support or compliance policy snippets")]
    public required IReadOnlyList<string> PolicySnippets { get; init; }

    [Output(Description = "Severity: Low, Medium, High, Critical")]
    public required string Severity { get; init; }

    [Output(Description = "Owning team name")]
    public required string RouteToTeam { get; init; }

    [Output(Description = "Grounded customer reply draft")]
    public required string DraftReply { get; init; }

    [Output(Description = "Reasoning for severity and routing")]
    public required string Rationale { get; init; }

    [Output(Description = "True if escalation to a human is required")]
    public required bool Escalate { get; init; }
}
```

**What This Does:** This defines a **contract** between your code and the language model. Every input and output has a name, type, and description. The `required` keyword means the compiler refuses to build if any field is missing. The `[LmpSignature]` attribute triggers a source generator that produces metadata used at every subsequent stage.

### Code: Composing a Multi-Step Program

```csharp
using LMP;

[LmpProgram("support-triage")]
public partial class SupportTriageProgram : LmpProgram<TicketInput, TriageResult>
{
    public override ProgramGraph Build()
    {
        var retrieveKb = Step.Retrieve(
            name: "retrieve-kb",
            from: input => input.TicketText,
            topK: 5);

        var retrievePolicy = Step.Retrieve(
            name: "retrieve-policy",
            from: input => input.TicketText,
            topK: 3);

        var triage = Step.Predict<TriageTicket>(
            name: "triage",
            bind: (input, ctx) => new TriageTicket
            {
                TicketText = input.TicketText,
                AccountTier = input.AccountTier,
                KnowledgeSnippets = ctx.OutputOf(retrieveKb).Documents,
                PolicySnippets = ctx.OutputOf(retrievePolicy).Documents
            });

        var groundedness = Step.Evaluate(
            name: "groundedness-check",
            after: triage,
            evaluator: new GroundednessEvaluator());

        var policy = Step.Evaluate(
            name: "policy-check",
            after: triage,
            evaluator: new CustomPolicyEvaluator("support-policy"));

        var repair = Step.If(
            name: "repair-if-needed",
            condition: ctx =>
                ctx.ScoreOf(groundedness) < 0.90 || !ctx.Passed(policy),
            then: Step.Repair<TriageTicket>(
                name: "repair-triage",
                usingFeedbackFrom: new[] { groundedness, policy }));

        return Graph
            .StartWith(retrieveKb)
            .Then(retrievePolicy)
            .Then(triage)
            .Then(groundedness)
            .Then(policy)
            .Then(repair)
            .Return(ctx => new TriageResult
            {
                Severity = ctx.Latest<TriageTicket>().Severity,
                RouteToTeam = ctx.Latest<TriageTicket>().RouteToTeam,
                DraftReply = ctx.Latest<TriageTicket>().DraftReply,
                Escalate = ctx.Latest<TriageTicket>().Escalate,
                GroundednessScore = ctx.ScoreOf(groundedness),
                PolicyPassed = ctx.Passed(policy)
            });
    }
}

public sealed record TicketInput(string TicketText, string AccountTier);

public sealed record TriageResult(
    string Severity,
    string RouteToTeam,
    string DraftReply,
    bool Escalate,
    double GroundednessScore,
    bool PolicyPassed);
```

**What This Does:** This defines the **program graph** — a directed sequence of steps: retrieve knowledge, retrieve policy, predict a triage result, evaluate groundedness, check policy compliance, and optionally repair if quality thresholds aren't met. Each step's data flow is explicit: `ctx.OutputOf(retrieveKb).Documents` tells the framework exactly where data comes from. The graph is a first-class object the framework can inspect, validate, optimize, and serialize.

### Code: Defining a Compile Spec

```csharp
using LMP.Compilation;

var compileSpec = CompileSpec
    .For<SupportTriageProgram>()
    .WithTrainingSet("data/support-triage-train.jsonl")
    .WithValidationSet("data/support-triage-val.jsonl")
    .Optimize(search =>
    {
        search.Instructions(step: "triage");
        search.FewShotExamples(step: "triage", min: 0, max: 6);
        search.RetrievalTopK(step: "retrieve-kb", min: 3, max: 10);
        search.RetrievalTopK(step: "retrieve-policy", min: 2, max: 6);
        search.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]);
        search.Temperature(step: "triage", min: 0.0, max: 0.7);
    })
    .ScoreWith(Metrics.Weighted(
        ("routing_accuracy", 0.35),
        ("severity_accuracy", 0.25),
        ("groundedness", 0.20),
        ("policy_pass_rate", 0.20)))
    .Constrain(rules =>
    {
        rules.Require("policy_pass_rate == 1.0");
        rules.Require("p95_latency_ms <= 2500");
        rules.Require("avg_cost_usd <= 0.03");
    })
    .UseOptimizer(Optimizers.RandomSearch());
```

**What This Does:** This tells the compiler what to optimize and what constraints must hold. The `.Optimize()` block defines the **search space** — instructions, few-shot examples, model choice, temperature, retrieval depth. The `.Constrain()` block defines **hard requirements**: policy compliance must be 100%, latency under 2.5s, cost under $0.03. The compiler rejects any variant violating constraints, even if it scores higher on metrics.

### Code: Runtime Registration and Execution

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddOpenTelemetry();

// Keyed DI enables per-step model routing
services.AddKeyedSingleton<IChatClient>("triage", (sp, _) =>
    new ChatClientBuilder(new OpenAIChatClient("gpt-4.1-mini",
        sp.GetRequiredService<OpenAIClient>()))
        .UseFunctionInvocation()
        .UseOpenTelemetry()          // M.E.AI built-in
        .UseDistributedCache(cache)  // M.E.AI built-in
        .UseLogging(logger)          // M.E.AI built-in
        .UseLmpStepContext()         // LMP-specific
        .UseLmpCostTracking()        // LMP-specific
        .Build());

services.AddLmpPrograms()
    .AddProgram<SupportTriageProgram>();

var provider = services.BuildServiceProvider();
var program = provider.GetRequiredService<SupportTriageProgram>();

var result = await program.RunAsync(new TicketInput(
    TicketText: "Since the latest release, SSO login intermittently fails for 300+ users...",
    AccountTier: "Enterprise"));
```

**What This Does:** This wires the program into .NET's standard dependency injection, logging, and telemetry infrastructure. `AddKeyedSingleton<IChatClient>` registers a named model client enabling different steps to use different models. LMP leverages M.E.AI's built-in middleware (`UseOpenTelemetry()`, `UseDistributedCache()`, `UseLogging()`) rather than reinventing cross-cutting concerns; LMP adds only `UseLmpStepContext()` and `UseLmpCostTracking()` as framework-specific middleware. `RunAsync` executes the full program graph and returns a strongly-typed result.

---

## 4. Why .NET (Written for Skeptics)

LMP belongs in .NET not because .NET is "more AI-native" — it isn't. LMP belongs in .NET because .NET is **excellent at turning complicated behavior into disciplined software**. Five convergences of .NET primitives create capabilities Python fundamentally cannot replicate.

### Convergence 1: Errors Move From Production to Build Time

Source generators + Roslyn analyzers + `required` keyword + nullable reference types + pattern matching + generic constraints = an LM program that **cannot be misconfigured at compile time**. In Python, all of these are discovered at runtime — potentially deep into an expensive optimization run.

**Python comparison:** `dspy.Signature("question -> answer")` parses a string at runtime. Forget a field? Runtime crash. LMP uses `required` properties — forget a field and the compiler refuses to build.

### Convergence 2: The Compiler Can Reason About Programs

Source generators + the three-tier binding model + `FrozenDictionary` + `ImmutableArray` + records = the framework has a **complete, inspectable, immutable representation** of every LM program at build time. The optimizer operates on typed, validated, deterministic descriptors — not opaque Python objects.

**Python comparison:** DSPy programs are Python classes with a `forward()` method. The framework cannot inspect how data flows between steps. LMP uses a three-tier binding model — convention-based auto-binding, `[BindFrom]` attributes, and C# 14 interceptor-based lambda binding — to capture data flow as inspectable, AOT-safe structures. Expression trees serve only as a runtime fallback.

### Convergence 3: Optimization Is Safe and Observable

Records with `with` expressions (safe variant generation) + rate limiting (API safety) + `HybridCache` (response deduplication) + `TimeProvider` (deterministic traces) + `ActivitySource` (OpenTelemetry tracing) + `InMemoryExporter` (in-process feedback loop) + TPL Dataflow (`TransformBlock`/`ActionBlock` pipelines for step graph execution) + `System.Numerics.Tensors` (SIMD-accelerated metric computation) = an optimization pipeline that is **observable, reproducible, cost-controlled, and debuggable**.

**Python comparison:** Records with `with` expressions guarantee all fields are copied when creating a variant. `dataclasses.replace()` works but Python offers no compiler guarantee against accidental field omission during manual construction.

### Convergence 4: Artifacts Are First-Class Deployment Units

`JsonSerializerContext` (AOT-safe serialization) + `[JsonDerivedType]` (polymorphism) + `AssemblyLoadContext` (hot-swap) + `IOptionsMonitor` (auto-reload) + `System.IO.Hashing` (provenance) + Native AOT (~50ms cold start) = compiled LM programs as **versioned, deployable, hot-swappable software artifacts**.

**Python comparison:** `pickle.load()` is Python's serialization story — and it's an arbitrary code execution vulnerability. `importlib.reload()` leaves old references alive and leaks memory. LMP loads artifacts into isolated contexts and can swap versions without restarting the service.

### Convergence 5: Enterprise Infrastructure Comes Free

Keyed DI (multi-model routing) + `IHost` (lifecycle management) + `BackgroundService` (long-running jobs) + Aspire 13.1 GA (orchestration, Modern Lifecycle Policy) + OpenTelemetry (observability) + `IOptions` (validated config) = **production-grade infrastructure without building it**.

**Python comparison:** Each of these requires a separate library, separate configuration, and custom glue code. Python has no standard DI container, no standard config system, no standard rate limiter, no standard host lifecycle.

> **Why This Matters:** "In Python, if you add a new step type and forget to handle it, you get a silent bug in production. In LMP, you get a compile error." This is not a marginal improvement — it's a structural guarantee that scales with codebase complexity.

---

## 5. Relationship to DSPy

LMP is **inspired by** DSPy (Stanford NLP), **not a port of it**.

### What We Adopt From DSPy (4 concepts)

1. **The core insight** that LM programs should be optimized programmatically, not manually tuned
2. **Typed Signatures** as LM contracts (`dspy.Signature` → `[LmpSignature]`)
3. **Predict and Retrieve** step abstractions (`dspy.Predict`, `dspy.Retrieve`)
4. **A compile loop** that searches over program variants to find the best one

### What We Invented (12+ concepts, no DSPy equivalent)

Directed program graphs · compile-time validation via Roslyn · three-tier binding model (convention, attribute, C# 14 interceptor) with expression-tree runtime fallback · constraint-based optimization · Evaluate, If, and Repair as first-class program steps · model choice as a tunable parameter · temperature as a tunable parameter · typed artifact serialization with AOT safety and hot-swap · enterprise observability via OpenTelemetry with in-process trace feedback · the entire build-time architecture (analyzers, code fixes, diagnostics).

### DSPy Comparison Table

| Capability | DSPy | LMP |
|---|---|---|
| Program model | Python class with `forward()` | Directed graph of typed steps |
| Compile-time validation | None | Roslyn analyzers + source generators |
| Data flow inspection | Opaque Python bytecode | Three-tier binding (convention → attribute → C# 14 interceptor; expression tree fallback) |
| Constraint model | None | Hard/soft constraints with diagnostics |
| Observability | `execution_log` (flat list of dicts) | OpenTelemetry (W3C spans, metrics) |
| Artifact model | `Module.save()`/`.load()` | Versioned JSON with provenance hashing |
| Hot-swap deployment | `importlib.reload()` (fragile) | `AssemblyLoadContext` (safe, zero-downtime) |
| IDE feedback | External linters (mypy, pylint) | In-IDE diagnostics while typing |

> **Why This Matters:** Approximately **60% of the architecture is original .NET innovation**. LMP is not "DSPy but in C#." It's a fundamentally different architecture that borrows DSPy's best insight and builds far beyond it.

---

## 6. Target Users

### Enterprise AI/ML Teams Using .NET

**What they care about:** Governance, auditability, reproducibility, cost controls.
**How LMP helps:** Compiled artifacts with provenance hashing, constraint-based optimization that enforces policy compliance, OpenTelemetry traces for every step.

### .NET Developers Building AI-Powered Features

**What they care about:** Familiar patterns, type safety, IDE support, DI/logging integration.
**How LMP helps:** Standard C# authoring with attributes and records, Roslyn diagnostics in the IDE, `IServiceCollection` registration, `ILogger<T>` structured logging — nothing new to learn except the domain concepts.

### Platform Teams Needing Governance Over LM Usage

**What they care about:** Cost visibility, model selection control, deployment safety, version pinning.
**How LMP helps:** Per-step model routing via keyed DI, hard constraints on cost/latency, versioned artifacts that are the exact unit of deployment, hot-swap without downtime.

---

## 7. MVP Scope

### In Scope

| Capability | Details |
|---|---|
| **Typed LM signatures** | `[LmpSignature]` with input/output fields, descriptions, types |
| **Program graph abstraction** | Directed graph with Predict, Retrieve, Evaluate, If, Repair steps |
| **Source generation** | Signature and program metadata descriptors (`.g.cs`) |
| **Roslyn analyzers** | A small set of high-value diagnostics (LMP001–007) |
| **Runtime execution** | Execute programs via `IChatClient` with OpenTelemetry tracing |
| **Evaluation integration** | Score outputs using `Microsoft.Extensions.AI.Evaluation` |
| **Compile/optimize loop** | Search over instruction, few-shot, model, temperature, topK variants |
| **Artifact serialization** | Save/load compiled variants as versioned JSON with provenance |
| **CLI** | `dotnet lmp compile`, `dotnet lmp run`, `dotnet lmp eval` |
| **Sample program** | Ticket triage program demonstrating end-to-end story |
| **Automated tests** | Unit, golden, analyzer, compiler, artifact snapshot, end-to-end |

### Post-MVP Capabilities

| Capability | Phase | Details |
|---|---|---|
| **MSBuild build targets** | Phase 10 | `LmpEmitIr`, `LmpValidateGraph` — `dotnet build` catches structural errors without calling LM APIs |
| **NuGet artifact distribution** | Phase 11 | `dotnet lmp pack` — package compiled artifacts as NuGet packages for enterprise distribution via Azure Artifacts / GitHub Packages |
| **LMP.Sdk** | Phase 12 | Custom MSBuild SDK: `<Project Sdk="LMP.Sdk/1.0.0">` — zero-config, one-line project setup |
| **Convention-based discovery** | Phase 12 | Auto-discover programs, auto-generate DI registration |
| **Aspire hosting** | Phase 13 | `AddLmpCompiler()` as an Aspire resource with dashboard telemetry |

**Business value of NuGet distribution:** Enterprise teams distribute optimized LM programs the same way they distribute libraries — with versioning, rollback, audit trails, and private feeds. A data science team compiles and publishes `MyCompany.Lmp.SupportTriage@2.0.0`; consuming services add a `<PackageReference>` and get the optimized program without touching prompt engineering.

### Out of Scope (and Why)

| Excluded | Reason |
|---|---|
| Full DSPy feature parity | Inspired by DSPy, not a port — selective adoption is intentional |
| Distributed optimization | Single-machine compile loops sufficient to prove concept |
| Advanced probabilistic optimizers | RandomSearch sufficient for MVP; sophistication follows validation |
| Graphical UI | CLI + IDE diagnostics sufficient; UI layered later |
| Full IDE extension | Roslyn analyzers provide IDE feedback without a custom extension |
| Memory / multimodal / agent loops | Stateless text-only execution is simpler and sufficient for MVP |
| Full Native AOT optimization | AOT-compatible design now; full optimization deferred |
| Production persistence | File-based artifacts sufficient for MVP |

**Scope principle:** When in doubt, prefer a simpler abstraction, fewer steps, deterministic behavior, and visible artifacts over completeness.

---

## 8. Success Criteria

### Core Acceptance Criteria (all must be true)

1. A developer can author a signature using attributes
2. Source generation emits a deterministic signature descriptor
3. A developer can author a multi-step triage program
4. The runtime executes the program using `IChatClient`
5. Evaluators can score outputs on a dataset
6. A compile loop can search over at least 3 tunable dimensions
7. The compiler emits a best valid variant and a report
8. A compiled artifact can be saved and loaded
9. At least 3 meaningful diagnostics exist
10. The sample ticket triage demo works end to end

### Demo Acceptance Criteria

The sample demo must visibly show:

- Authored source code
- Generated source / metadata (`.g.cs` files)
- Runtime trace output (steps, timings, model, tokens, cost)
- Evaluation results (metric scores)
- Compiler report (variants tried, best selected, constraints checked)
- Compiled artifact (versioned JSON with provenance hash)

### What "Done" Looks Like

A developer can clone the repo, `dotnet build` the sample (seeing analyzer diagnostics), run the triage program (seeing trace output), run `dotnet lmp compile` to optimize it (seeing variant search), and load the resulting artifact for production execution. Demonstrable in under 10 minutes.

---

## 9. Ecosystem Dependencies

| Dependency | NuGet Package | Provides | Status |
|---|---|---|---|
| **IChatClient** | [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI) v10.0+ | Unified LM runtime abstraction — the substrate for all model calls | ✅ GA |
| **Evaluation** | [`Microsoft.Extensions.AI.Evaluation.Quality`](https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation.Quality) | Production-grade evaluators: `GroundednessEvaluator`, coherence, fluency, relevance | ✅ GA |
| **OpenTelemetry** | [`OpenTelemetry`](https://www.nuget.org/packages/OpenTelemetry) v1.10+ | Distributed tracing (`ActivitySource`), metrics (`Meter`), in-process export (`InMemoryExporter`) | ✅ Stable |

> **Why This Matters:** Every dependency is GA and actively maintained. LMP builds on Microsoft's own AI infrastructure stack — not third-party wrappers that may break or disappear.

---

## 10. Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| **DSPy community confusion** — perceived as "just a C# port" | Medium | Clear "inspired by, not a port" positioning. Emphasize 60% original architecture. Use "LMP" name, not DSPy references, in user-facing surfaces. |
| **API stability of M.E.AI** — breaking `IChatClient` changes | Medium | Thin abstraction layer. Pin NuGet versions. Adapter pattern isolates from upstream changes. |
| **Ecosystem adoption** — .NET has less ML community momentum | Medium | First-mover advantage. Target enterprise .NET shops (large market) rather than ML researchers. |
| **Optimizer sophistication** — RandomSearch may be insufficient | Low (MVP) | Proves architecture. Sophisticated optimizers are pluggable backends added post-MVP. |
| **Expression tree limitations** — no async/statement capture | Low | Expression trees are Tier 4 runtime-only fallback. Primary binding uses convention, `[BindFrom]` attributes, and C# 14 interceptors (stable in .NET 10). |

---

*This document is derived from `spec.org`. If any downstream document conflicts with the spec, the spec wins.*
