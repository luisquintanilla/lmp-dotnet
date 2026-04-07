# MVP Demo Script

> **Derived from:** Spec Sections 3 (Canonical MVP Story), 6 (Canonical Developer Experience), 17 (Acceptance Criteria).
>
> **Audience:** Anyone running or presenting the LMP MVP demo.

---

## Prerequisites

### Software

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 10.0+ | `winget install Microsoft.DotNet.SDK.10` |
| Git | Any | `winget install Git.Git` |
| Terminal | Windows Terminal, iTerm2, or similar | — |

### API Keys (Live Mode)

```bash
# Set your OpenAI API key for live LLM calls
export OPENAI_API_KEY="sk-..."

# Or for Azure OpenAI:
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-key"
```

### Mock Mode (No API Key Needed)

If presenting without API access, set:

```bash
export LMP_USE_MOCK=true
```

This activates the `FakeChatClient` which returns deterministic responses. All steps work identically — you just won't see real model latency or output variation.

> **Junior Dev Note:** Mock mode is the default in the sample project. You don't need an API key to run the demo for the first time. Live mode is only needed to show real model variation.

### Clone and Build

```bash
git clone https://github.com/your-org/lmp-framework.git
cd lmp-framework
dotnet build LMP.sln
```

Expected output:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## Opening Pitch

> **"Compiled prompts are to AI what compiled code was to software."**
>
> Enterprise AI is bleeding money — 70–85% of GenAI deployments fail to meet ROI expectations (NTT Data 2024), only ~25% of AI initiatives deliver expected ROI (Google Cloud/Deloitte 2025), and organizations without AI governance pay $1.9M more per data breach (IBM 2025). LMP brings the discipline of compiled software — type safety, versioned artifacts, constraint-based optimization — to AI workflows. What you're about to see is the first framework that treats LM programs as compilable, testable, deployable software.

---

## Step 1: Show the Authored Source Code

**Goal:** Demonstrate that LM programs are typed, declared C# — not prompt strings in files.

### Terminal Commands

```bash
# Show the signature
cat src/LMP.Samples.SupportTriage/TriageTicket.cs
```

### What the Audience Sees

```csharp
[LmpSignature(
    Instructions = """
    You are a senior enterprise support triage assistant.
    Classify the issue severity, determine the owning team, and draft a grounded
    customer reply using only the provided evidence and policy context.
    If the evidence is insufficient, say so explicitly.
    """)]
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

```bash
# Show the program
cat src/LMP.Samples.SupportTriage/SupportTriageProgram.cs
```

### Talking Points

- **"Notice how** this is regular C# — `required`, `init`, strong types. No YAML, no magic strings."
- **"Notice how** every field has a `Description`. If you remove one, analyzer LMP001 fires as a build warning."
- **"Notice how** the program composes steps into a graph: retrieve → predict → evaluate → conditionally repair. This is a *software architecture*, not a prompt chain."

### Show the Host Setup

```bash
# Show the DI registration and middleware
cat src/LMP.Samples.SupportTriage/Program.cs
```

### What the Audience Sees

```csharp
var builder = Host.CreateApplicationBuilder(args);

// M.E.AI provides built-in middleware — LMP doesn't reinvent these
builder.Services.AddKeyedSingleton<IChatClient>("triage", (sp, _) =>
    new ChatClientBuilder(new OpenAIChatClient("gpt-4.1-mini",
        sp.GetRequiredService<OpenAIClient>()))
        .UseOpenTelemetry()          // M.E.AI built-in
        .UseDistributedCache(cache)  // M.E.AI built-in
        .UseLogging(logger)          // M.E.AI built-in
        .UseLmpStepContext()         // LMP-specific: injects step metadata
        .UseLmpCostTracking()        // LMP-specific: accumulates cost per trial
        .Build());

// Register LMP programs
builder.Services.AddLmpPrograms()
    .AddProgram<SupportTriageProgram>();

// Constraints use simple predicates — no solver dependency
builder.Services.AddLmpCompiler(compiler =>
{
    compiler.Constrain(c =>
    {
        c.Require("policy_pass_rate", m => m["policy_pass_rate"] >= 1.0);
        c.Require("p95_latency_ms",   m => m["p95_latency_ms"] <= 2500);
        c.Require("avg_cost_usd",     m => m["avg_cost_usd"] <= 0.03);
    });
});

var app = builder.Build();
await app.RunAsync();
```

### Talking Points (Host Setup)

- **"Notice how** LMP uses M.E.AI's built-in `UseOpenTelemetry()`, `UseDistributedCache()`, and `UseLogging()` middleware — we don't reinvent what the platform already provides."
- **"Notice how** LMP adds only two custom middleware: `UseLmpStepContext()` for step metadata and `UseLmpCostTracking()` for cost accumulation. Everything else is standard .NET."
- **"Notice how** constraints are simple C# predicates — lambda expressions the compiler can inline. No solver dependency for the MVP."

---

## Step 2: Build and Inspect Generated Code

**Goal:** Show that the build produces deterministic metadata and diagnostics — the framework understands the program at compile time.

### Terminal Commands

```bash
# Build the sample project
dotnet build src/LMP.Samples.SupportTriage/LMP.Samples.SupportTriage.csproj

# Show generated files
find src/LMP.Samples.SupportTriage/obj -name "*.g.cs" -type f
# On Windows:
# Get-ChildItem -Recurse src\LMP.Samples.SupportTriage\obj -Filter "*.g.cs"
```

### Expected Output

```
src/LMP.Samples.SupportTriage/obj/Debug/net10.0/Generated/LMP.Roslyn/TriageTicket.g.cs
src/LMP.Samples.SupportTriage/obj/Debug/net10.0/Generated/LMP.Roslyn/SupportTriageProgram.g.cs
```

```bash
# Show the generated descriptor
cat src/LMP.Samples.SupportTriage/obj/Debug/net10.0/Generated/LMP.Roslyn/TriageTicket.g.cs
```

### What the Audience Sees

```csharp
// <auto-generated />
namespace LMP.Samples.SupportTriage.Generated;

internal static class TriageTicket_Descriptor
{
    public static readonly SignatureDescriptor Instance = new(
        Id: "triageticket",
        Name: "TriageTicket",
        Instructions: """
        You are a senior enterprise support triage assistant.
        ...
        """,
        Inputs: new[]
        {
            new FieldDescriptor("TicketText", "Input", "System.String",
                "Raw customer issue or support ticket text", true),
            // ... all 4 inputs
        },
        Outputs: new[]
        {
            new FieldDescriptor("Severity", "Output", "System.String",
                "Severity: Low, Medium, High, Critical", true),
            // ... all 5 outputs
        });
}
```

### Show a Diagnostic Firing

```bash
# Temporarily break the signature to trigger LMP001
# (Remove Description from an Input, rebuild, see the warning)
```

Expected diagnostic:

```
warning LMP001: Field 'TicketText' is missing a Description.
    Add Description to the [Input] attribute.
```

### Talking Points

- **"Notice how** the generated code is pure C# records — deterministic, inspectable, testable. No reflection at runtime."
- **"Notice how** the framework caught a missing description *at build time*, not when a customer hits the API."
- **"This is the .NET advantage:** Python frameworks discover program structure at import time. We discover it at compile time."

---

## Step 2b: Show the Three-Tier Binding Model

**Goal:** Demonstrate how steps bind to each other — from zero-config conventions to explicit attributes.

### Terminal Commands

```bash
# Show the signature with [BindFrom] attributes
cat src/LMP.Samples.SupportTriage/TriageTicket.cs
```

### What the Audience Sees

```csharp
// Tier 2: Explicit attribute binding — data flows from retrieve step into predict step
[Input(Description = "Relevant knowledge base snippets")]
[BindFrom("retrieve-kb", nameof(RetrieveResult.Documents))]
public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

[Input(Description = "Relevant support or compliance policy snippets")]
[BindFrom("retrieve-policy", nameof(RetrieveResult.Documents))]
public required IReadOnlyList<string> PolicySnippets { get; init; }
```

### Talking Points

- **"Notice how** `[BindFrom]` declares exactly where each input comes from — no lambda needed, no expression trees. This is Tier 2 of our binding model."
- **"Tier 1 is convention-based:** if property names and types match between steps, binding is automatic."
- **"Tier 3 uses C# 14 interceptors** — the compiler rewrites lambda bindings into direct method calls. Zero runtime overhead, fully AOT-safe."
- **"Tier 4 is expression trees** — the same pattern Entity Framework uses, but only as a runtime fallback for dynamically constructed programs."

---

## Step 3: Run on a Single Ticket

**Goal:** Show end-to-end program execution with trace output.

### Terminal Commands

```bash
dotnet run --project src/LMP.Samples.SupportTriage -- run \
  --input '{"ticketText":"Since the latest release, SSO login intermittently fails for 300+ users in our EU tenant. This is blocking our support operations. Please advise ASAP.","accountTier":"Enterprise"}'
```

Or using a file:

```bash
dotnet run --project src/LMP.Samples.SupportTriage -- run \
  --input-file data/sample-ticket.json
```

### Expected Output

```
Program: support-triage
ProgramVersion: 0.1.0
VariantId: baseline

[retrieve-kb]
  Retrieved: 5 docs
  LatencyMs: 35

[retrieve-policy]
  Retrieved: 3 docs
  LatencyMs: 18

[triage]
  Signature: TriageTicket
  Model: gpt-4.1-mini
  Temperature: 0.3
  PromptTokens: 2104
  CompletionTokens: 262
  CostUsd: 0.017
  LatencyMs: 1480

[groundedness-check]
  Score: 0.96

[policy-check]
  Passed: True

Final:
  Severity: Critical
  RouteToTeam: Identity Platform
  DraftReply: "We are actively investigating the SSO login failures..."
  Escalate: True
  TotalLatencyMs: 1642
  TotalCostUsd: 0.017
```

### Talking Points

- **"Notice how** every step has structured trace output — model, tokens, cost, latency. This is production observability, not print debugging."
- **"Notice how** the groundedness check and policy check run as steps in the graph, not as afterthoughts."

---

## Step 4: Run Evaluation on a Dataset

**Goal:** Show that program quality is measurable, not anecdotal.

### Terminal Commands

```bash
dotnet run --project src/LMP.Samples.SupportTriage -- eval \
  --dataset data/support-triage-val.jsonl
```

### Expected Output

```
Evaluation: support-triage
Dataset: data/support-triage-val.jsonl (20 examples)

Results:
  routing_accuracy:  0.75
  severity_accuracy: 0.70
  groundedness:      0.88
  policy_pass_rate:  0.90
  avg_latency_ms:    1580
  avg_cost_usd:      0.018

Weighted Score: 0.81
```

### Talking Points

- **"Notice how** we have quantified scores across a real dataset. This is how you make 'is the AI good enough?' an engineering question, not a vibes question."
- **"A score of 0.81 with 90% policy pass rate is our baseline. Can we do better? That's what the compiler is for."

---

## Step 5: Compile the Program

**Goal:** Show the optimization loop in action — trial execution, constraint checking, and variant selection.

### Terminal Commands

```bash
dotnet run --project src/LMP.Samples.SupportTriage -- compile \
  --train data/support-triage-train.jsonl \
  --validate data/support-triage-val.jsonl \
  --output artifacts/support-triage \
  --max-trials 20
```

### Expected Output

```
Compiling: support-triage
Search space: 3 dimensions
  - triage.temperature: [0.0, 0.7]
  - triage.model: [gpt-4.1-mini, gpt-4.1]
  - retrieve-kb.topK: [3, 10]

Constraints:
  - policy_pass_rate == 1.0 (Hard)
  - p95_latency_ms <= 2500 (Hard)
  - avg_cost_usd <= 0.03 (Hard)

Trial  1/20: variant=triage-v1  score=0.78  constraints=PASS
Trial  2/20: variant=triage-v2  score=0.82  constraints=PASS
Trial  3/20: variant=triage-v3  score=0.85  constraints=FAIL (policy_pass_rate=0.90)
Trial  4/20: variant=triage-v4  score=0.79  constraints=PASS
...
Trial 17/20: variant=triage-v17 score=0.91  constraints=PASS  ★ new best
...
Trial 20/20: variant=triage-v20 score=0.88  constraints=PASS

────────────────────────────
Compile Report:
  Trials executed: 20
  Valid trials: 14
  Rejected trials: 6
    policy_pass_rate: 4
    p95_latency_ms: 1
    avg_cost_usd: 1

  Best valid variant: triage-v17
  Score: 0.91

  Artifact saved to: artifacts/support-triage/artifact.json
────────────────────────────
```

### Talking Points

- **"Notice how** trial 3 scored 0.85 but was *rejected* because it violated the policy constraint. This is the constraint model in action — high scores don't override business rules."
- **"Notice how** 6 out of 20 trials were rejected. The compiler tells you *why* each one failed. This is enterprise-grade optimization."
- **"The winning variant (0.91) vs our baseline (0.81) — a 12% improvement, found automatically."

---

## Step 6: Inspect the Compiled Artifact

**Goal:** Show that the compiled output is a versioned, inspectable JSON file — not a black box.

### Terminal Commands

```bash
cat artifacts/support-triage/artifact.json
```

### Expected Output

```json
{
  "program": "support-triage",
  "compiledVersion": "0.1.0",
  "variantId": "triage-v17",
  "baseProgramHash": "sha256:abc123...",
  "selectedParameters": {
    "triage.instructionsVariant": "inst-3",
    "triage.fewShotExampleIds": ["ex-12", "ex-44", "ex-78", "ex-121"],
    "triage.model": "gpt-4.1-mini",
    "triage.temperature": 0.1,
    "retrieve-kb.topK": 6,
    "retrieve-policy.topK": 3
  },
  "validationMetrics": {
    "routing_accuracy": 0.89,
    "severity_accuracy": 0.83,
    "groundedness": 0.96,
    "policy_pass_rate": 1.0,
    "p95_latency_ms": 2210,
    "avg_cost_usd": 0.021
  },
  "approved": true
}
```

### Talking Points

- **"Notice how** the artifact is plain JSON — auditable, diffable, version-controllable. You can check this into git."
- **"Notice how** the artifact records exactly *which* parameters were selected — this is full provenance. You can explain to compliance *why* the system behaves the way it does."
- **"Notice how** `approved: true` — only artifacts that passed all hard constraints get approved."

---

## Step 7: Run with the Compiled Artifact

**Goal:** Show that production can pin to a specific compiled variant.

### Terminal Commands

```bash
dotnet run --project src/LMP.Samples.SupportTriage -- run \
  --input-file data/sample-ticket.json \
  --artifact artifacts/support-triage/artifact.json
```

### Expected Output

```
Program: support-triage
ProgramVersion: 0.1.0-compiled
VariantId: triage-v17

[retrieve-kb]
  Retrieved: 6 docs        ← topK=6 from artifact
  LatencyMs: 42

[retrieve-policy]
  Retrieved: 3 docs
  LatencyMs: 15

[triage]
  Signature: TriageTicket
  Model: gpt-4.1-mini      ← model from artifact
  Temperature: 0.1          ← temperature from artifact
  FewShotExamples: 4        ← examples from artifact
  PromptTokens: 2104
  CompletionTokens: 262
  CostUsd: 0.017
  LatencyMs: 1380

[groundedness-check]
  Score: 0.97

[policy-check]
  Passed: True

Final:
  Severity: Critical
  RouteToTeam: Identity Platform
  DraftReply: "We are actively investigating the SSO login failures..."
  Escalate: True
  TotalLatencyMs: 1520
  TotalCostUsd: 0.017
```

### Talking Points

- **"Notice how** `VariantId: triage-v17` — production is pinned to the compiled variant. No drift, no surprises."
- **"Notice how** the artifact's selected parameters (temperature=0.1, topK=6, 4 few-shot examples) are applied automatically."
- **"This is the full lifecycle:** Author → Build → Evaluate → Compile → Deploy. Every step is typed, traced, and auditable."

---

## Fallback Plan: Mock Mode

If API keys are unavailable or you want a fully offline demo:

### Setup

```bash
export LMP_USE_MOCK=true
dotnet build LMP.sln
```

### What Changes

| Step | Live Mode | Mock Mode |
|------|-----------|-----------|
| Build + Generated Code | Identical | Identical |
| Single Run | Real model call, ~1.5s latency | Instant, deterministic response |
| Evaluation | Real scores, some variation | Fixed scores (routing=0.75, etc.) |
| Compile | Real trials, ~2-5 min | Fast trials, ~10s |
| Artifact | Real metrics | Synthetic metrics |
| Run with Artifact | Real model call | Deterministic response |

### Mock Mode Demo Commands

All commands are the same. The only difference is the environment variable. Every step produces structured output that looks real — the audience cannot tell the difference visually.

> **Junior Dev Note:** Mock mode is great for developing and testing. Use it when iterating on the demo flow. Switch to live mode only for the final rehearsal or when you want to show real model variation.

---

## Quick Reference: All Commands

```bash
# Build
dotnet build LMP.sln

# Show generated code
Get-ChildItem -Recurse src\LMP.Samples.SupportTriage\obj -Filter "*.g.cs"

# Run single ticket
dotnet run --project src/LMP.Samples.SupportTriage -- run --input-file data/sample-ticket.json

# Evaluate on dataset
dotnet run --project src/LMP.Samples.SupportTriage -- eval --dataset data/support-triage-val.jsonl

# Compile
dotnet run --project src/LMP.Samples.SupportTriage -- compile \
  --train data/support-triage-train.jsonl \
  --validate data/support-triage-val.jsonl \
  --output artifacts/support-triage \
  --max-trials 20

# Inspect artifact
cat artifacts/support-triage/artifact.json

# Run with artifact
dotnet run --project src/LMP.Samples.SupportTriage -- run \
  --input-file data/sample-ticket.json \
  --artifact artifacts/support-triage/artifact.json
```
