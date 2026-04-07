# LM Program IR Specification

> **Status:** Draft · Derived from `spec.org` §8 + §7.4 + §10A  
> **Namespace:** `LMP.IR`

---

## 1. What Is an IR and Why Does LMP Need One?

An **Intermediate Representation (IR)** is a structured data model that represents your LM program in a way all framework layers can understand. Instead of every layer (runtime, compiler, artifact store) parsing your C# source code independently, they all read from the same canonical model — the IR.

**Why not just use the user's source code directly?**

Your authored `LmpProgram` class is expressive and ergonomic, but it lives as C# syntax — Roslyn syntax trees, expression lambdas, fluent builder calls. The runtime can't efficiently traverse syntax trees to figure out step ordering. The compiler can't reason about tunable parameter ranges by reading your `Build()` method. The artifact layer can't serialize a lambda to disk.

The IR solves this by lowering your authored program into a flat, explicit, serializable data model that every downstream layer can consume without Roslyn, without reflection, and without runtime object identity.

> **Analogy:** Like how the C# compiler turns your source code into IL (Intermediate Language) before the CLR runs it, LMP turns your `LmpProgram` into an IR before executing, optimizing, or serializing it.

> [!NOTE]
> **Junior Dev Note:** "Lowering" means transforming a high-level representation into a simpler one. When a source generator reads your `[LmpSignature]` class and emits a `SignatureDescriptor` record, that's lowering — it's turning syntax into data.

---

## 2. The Complete IR Type Hierarchy

All IR types live in the `LMP.IR` namespace. They are immutable records using `ImmutableArray<T>` for collections. Every mandatory field uses the `required` keyword.

### 2.1 FieldDescriptor

The atomic unit — describes a single input or output field of a signature.

```csharp
namespace LMP.IR;

/// <summary>
/// Describes one typed field in a signature contract.
/// </summary>
/// <param name="Name">
/// The field name as authored (e.g., "TicketText").
/// Used as the JSON property key during prompt construction and output parsing.
/// </param>
/// <param name="Direction">
/// "Input" or "Output". Determines whether the field appears in the
/// prompt (Input) or is expected in the model response (Output).
/// </param>
/// <param name="ClrTypeName">
/// Fully-qualified CLR type name (e.g., "System.String").
/// The runtime uses this to validate deserialization targets.
/// </param>
/// <param name="Description">
/// Human-readable description shown to the LM in the prompt.
/// Sourced from the [Input] or [Output] attribute's Description property.
/// </param>
/// <param name="IsRequired">
/// Whether the field must be present. Maps to the C# 'required' keyword
/// on the authored property.
/// </param>
/// <param name="Metadata">
/// Extensibility bag for custom tooling or future field-level constraints.
/// </param>
public sealed record FieldDescriptor(
    string Name,
    string Direction,
    string ClrTypeName,
    string Description,
    bool IsRequired = true,
    ImmutableDictionary<string, string>? Metadata = null);
```

**Why every field exists:** `ClrTypeName` is a string (not a `Type`) because the IR must be serializable without loading assemblies — the compiler might run in a separate process. `Direction` is a string rather than an enum so the JSON representation stays readable without custom converters.

**Example:**
```json
{
  "name": "TicketText",
  "direction": "Input",
  "clrTypeName": "System.String",
  "description": "Raw customer issue or support ticket text",
  "isRequired": true
}
```

### 2.2 SignatureDescriptor

Represents one authored `[LmpSignature]` class, fully lowered.

```csharp
/// <summary>
/// The IR representation of a single LM task contract.
/// Generated at build time by the source generator from an [LmpSignature] class.
/// </summary>
public sealed record SignatureDescriptor(
    /// <summary>Stable deterministic ID (normalized type name, e.g., "triageticket").</summary>
    required string Id,
    /// <summary>Human-readable name (e.g., "TriageTicket").</summary>
    required string Name,
    /// <summary>System prompt instructions for the LM.</summary>
    required string Instructions,
    /// <summary>All input fields in declaration order.</summary>
    required ImmutableArray<FieldDescriptor> Inputs,
    /// <summary>All output fields in declaration order.</summary>
    required ImmutableArray<FieldDescriptor> Outputs,
    /// <summary>Fully-qualified source type name. Used for diagnostics and tracing.</summary>
    string? SourceTypeName = null,
    /// <summary>Originating assembly. Used for artifact compatibility checks.</summary>
    string? AssemblyName = null,
    /// <summary>Extensibility bag for custom metadata.</summary>
    ImmutableDictionary<string, string>? Metadata = null);
```

**Why this type exists:** The signature descriptor decouples the *contract* (what goes in, what comes out) from the *C# class* that defined it. The runtime reads descriptors to build prompts; the compiler reads them to reason about output shapes.

### 2.3 StepKind

The discriminator for step behavior.

```csharp
/// <summary>
/// Identifies the runtime behavior of a step in the program graph.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepKind
{
    /// <summary>Invokes a retriever to fetch documents. Authored via Step.Retrieve().</summary>
    Retrieve,
    /// <summary>Calls an LM using a signature contract. Authored via Step.Predict().</summary>
    Predict,
    /// <summary>Runs an evaluator and produces scores. Authored via Step.Evaluate().</summary>
    Evaluate,
    /// <summary>Conditional branching. Authored via Step.If().</summary>
    If,
    /// <summary>Re-runs a Predict step incorporating evaluator feedback. Authored via Step.Repair().</summary>
    Repair
}
```

> [!NOTE]
> **Junior Dev Note:** `StepKind` is an `enum`, not a class hierarchy. The IR intentionally avoids polymorphism for steps — a flat `StepDescriptor` with a `Kind` discriminator is much easier to serialize, query, and validate than a `PredictStep` / `RetrieveStep` inheritance tree.

### 2.4 StepDescriptor

One node in the program graph.

```csharp
/// <summary>
/// Describes a single step in an LM program's execution graph.
/// </summary>
public sealed record StepDescriptor(
    /// <summary>Stable deterministic ID derived from the authored step name.</summary>
    required string Id,
    /// <summary>Human-readable step name as authored (e.g., "retrieve-kb").</summary>
    required string Name,
    /// <summary>What kind of step this is (Predict, Retrieve, etc.).</summary>
    required StepKind Kind,
    /// <summary>
    /// Data bindings: which upstream outputs feed into this step's inputs.
    /// Keys are input field names; values are binding expressions.
    /// </summary>
    required ImmutableDictionary<string, string> Bindings,
    /// <summary>
    /// For Predict/Repair steps: the ID of the SignatureDescriptor.
    /// Null for Retrieve, Evaluate, and If steps.
    /// </summary>
    string? SignatureId = null,
    /// <summary>
    /// For Evaluate steps: the ID of the bound evaluator.
    /// </summary>
    string? EvaluatorId = null,
    /// <summary>Compiler-visible tunable parameters for this step.</summary>
    ImmutableArray<TunableParameterDescriptor> TunableParameters = default,
    /// <summary>
    /// For If steps: a serialized representation of the branching condition.
    /// </summary>
    string? ConditionExpression = null,
    /// <summary>Extensibility bag.</summary>
    ImmutableDictionary<string, string>? Metadata = null);
```

**Why `Bindings` is a dictionary, not an expression tree:** Expression trees aren't serializable or AOT-safe. The IR stores bindings as resolvable string expressions (e.g., `"steps.retrieve-kb.Documents"`) that the source generator resolves at build time (Tiers 1–3) or the runtime resolves at execution time (Tier 4 fallback).

**Binding Metadata in the IR:**

Each binding entry in the `Bindings` dictionary carries an implicit tier classification based on its origin:

| Binding Kind | IR Representation | Resolution |
|---|---|---|
| Convention (Tier 1) | `"steps.retrieve-kb.Documents"` with `BindingKind = "Convention"` in `Metadata` | Source generator emits direct assignment — zero runtime cost |
| Attribute (Tier 2) | `"steps.retrieve-kb.Documents"` with `BindingKind = "Attribute"` in `Metadata` | Source generator reads `[BindFrom]` and emits direct assignment — zero runtime cost |
| Interceptor (Tier 3) | `"steps.retrieve-kb.Documents"` with `BindingKind = "Interceptor"` in `Metadata` | C# 14 interceptor replaces lambda at call site — zero runtime cost |
| Expression tree (Tier 4) | `"steps.retrieve-kb.Documents"` with `BindingKind = "ExpressionTree"` in `Metadata` | Runtime `.Compile()` fallback |

The `BindingKind` value is stored in the step's `Metadata` dictionary under the key `"binding.<fieldName>.kind"`. This allows the runtime to dispatch to the correct resolution path and enables the compiler optimizer to identify expression-tree bindings that could be upgraded to a higher tier.

### 2.5 EdgeKind

```csharp
/// <summary>
/// Classifies how control flows between steps.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdgeKind
{
    /// <summary>Unconditional sequential flow.</summary>
    Sequence,
    /// <summary>Taken when the parent If step's condition is true.</summary>
    ConditionalTrue,
    /// <summary>Taken when the parent If step's condition is false.</summary>
    ConditionalFalse
}
```

### 2.6 EdgeDescriptor

An edge in the program DAG.

```csharp
/// <summary>
/// Describes a directed edge between two steps in the program graph.
/// </summary>
public sealed record EdgeDescriptor(
    /// <summary>Step ID of the source node.</summary>
    required string FromStepId,
    /// <summary>Step ID of the target node.</summary>
    required string ToStepId,
    /// <summary>The kind of edge (Sequence, ConditionalTrue, ConditionalFalse).</summary>
    required EdgeKind EdgeKind);
```

### 2.7 TunableParameterDescriptor

A single compiler-visible knob.

```csharp
/// <summary>
/// Describes one parameter the compiler can vary during optimization trials.
/// </summary>
public sealed record TunableParameterDescriptor(
    /// <summary>Stable ID for this parameter.</summary>
    required string Id,
    /// <summary>ID of the step this parameter belongs to.</summary>
    required string StepId,
    /// <summary>What category of parameter (Instruction, FewShotCount, Model, etc.).</summary>
    required ParameterKind ParameterKind,
    /// <summary>Display name.</summary>
    required string Name,
    /// <summary>Minimum value (for numeric parameters).</summary>
    double? MinValue = null,
    /// <summary>Maximum value (for numeric parameters).</summary>
    double? MaxValue = null,
    /// <summary>For categorical parameters: the allowed discrete values.</summary>
    ImmutableArray<string> AllowedValues = default,
    /// <summary>The default value if the compiler doesn't override.</summary>
    string? DefaultValue = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParameterKind
{
    Instruction,
    FewShotCount,
    FewShotSelection,
    RetrievalTopK,
    Model,
    Temperature
}
```

### 2.8 ConstraintDescriptor

A rule the compiler enforces during optimization.

```csharp
/// <summary>
/// A constraint the compiler evaluates against trial metrics.
/// Hard constraints gate candidate validity. Soft constraints influence ranking.
/// </summary>
public sealed record ConstraintDescriptor(
    required string Id,
    /// <summary>Which metric this constraint checks (e.g., "policy_pass_rate").</summary>
    required string MetricName,
    /// <summary>Comparison operator (GreaterThanOrEqual, LessThan, etc.).</summary>
    required ConstraintOperator Operator,
    /// <summary>The threshold value.</summary>
    required double Threshold,
    /// <summary>Hard = must pass; Soft = preference only.</summary>
    required ConstraintSeverity Severity,
    /// <summary>When the constraint is checked (Compile, Trial, Program).</summary>
    required ConstraintScope Scope,
    /// <summary>Human-readable message shown on violation.</summary>
    string? Message = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConstraintOperator { Equal, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConstraintSeverity { Hard, Soft }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConstraintScope { Compile, Trial, Program }
```

### 2.9 ProgramDescriptor

The root IR type — represents an entire authored program.

```csharp
/// <summary>
/// The complete IR of an LM program. This is the single canonical model
/// consumed by the runtime, compiler, and artifact layers.
/// </summary>
public sealed record ProgramDescriptor(
    /// <summary>Stable deterministic ID (normalized program name).</summary>
    required string Id,
    /// <summary>Human-readable program name (e.g., "support-triage").</summary>
    required string Name,
    /// <summary>CLR type name of the program input (e.g., "Demo.TicketInput").</summary>
    required string InputTypeName,
    /// <summary>CLR type name of the program output (e.g., "Demo.TriageResult").</summary>
    required string OutputTypeName,
    /// <summary>All steps in the program, in topological order.</summary>
    required ImmutableArray<StepDescriptor> Steps,
    /// <summary>All edges defining the program DAG.</summary>
    required ImmutableArray<EdgeDescriptor> Edges,
    /// <summary>Schema version for forward/backward compatibility.</summary>
    int Version = 1,
    /// <summary>Extensibility bag for tooling, provenance, or deployment metadata.</summary>
    ImmutableDictionary<string, string>? Metadata = null);
```

### 2.10 CompileSpec

The user's optimization request, lowered into IR form.

```csharp
/// <summary>
/// Describes a compilation request: which program to optimize,
/// with which data, tunables, constraints, and optimizer.
/// </summary>
public sealed record CompileSpec(
    /// <summary>ID of the program to compile.</summary>
    required string ProgramId,
    /// <summary>Path to the JSONL training dataset.</summary>
    required string TrainingDataPath,
    /// <summary>Path to the JSONL validation dataset.</summary>
    required string ValidationDataPath,
    /// <summary>Tunable parameters the compiler may vary.</summary>
    required ImmutableArray<TunableParameterDescriptor> Tunables,
    /// <summary>Constraints that trials must satisfy.</summary>
    required ImmutableArray<ConstraintDescriptor> Constraints,
    /// <summary>Weighted metric definitions for scoring (metric name → weight).</summary>
    required ImmutableDictionary<string, double> ScoringWeights,
    /// <summary>Optimizer identifier (e.g., "RandomSearch").</summary>
    required string Optimizer);
```

---

## 3. IR Construction Pipeline

The IR is not hand-written. It is **generated** from your authored code through a deterministic pipeline:

```
┌──────────────────────┐
│  1. Author writes    │   [LmpSignature] class TriageTicket { ... }
│     C# source code   │   [LmpProgram] class SupportTriageProgram { ... }
└─────────┬────────────┘
          │  Roslyn compiles
          ▼
┌──────────────────────┐
│  2. Source generator  │   Discovers [LmpSignature] →
│     emits descriptors │     emits SignatureDescriptor in .g.cs
│                       │   Discovers [LmpProgram] →
│                       │     emits StepDescriptor[] + EdgeDescriptor[]
└─────────┬────────────┘
          │  Build output (.dll)
          ▼
┌──────────────────────┐
│  3. DI Registration  │   At startup, AddLmpPrograms() collects
│     assembles IR      │     generated descriptors into ProgramDescriptor
└─────────┬────────────┘
          │
     ┌────┴─────┐
     ▼          ▼
┌─────────┐ ┌──────────┐
│ Runtime │ │ Compiler │
│ compiles│ │ reads    │
│ IR to   │ │ IR to    │
│ Dataflow│ │ optimize │
└─────────┘ └──────────┘
```

**Step 1 — Source generator discovers `[LmpSignature]`:** For each class marked with `[LmpSignature]`, the generator emits a `SignatureDescriptor` containing the instructions, all `[Input]` and `[Output]` fields lowered to `FieldDescriptor` records, and a stable ID derived from the type name. Output file: `TriageTicket.g.cs`.

**Step 2 — Source generator discovers `[LmpProgram]`:** For each class marked with `[LmpProgram]`, the generator analyzes the `Build()` method to extract steps, edges, and tunable parameter hints. Output file: `SupportTriageProgram.g.cs`.

**Step 3 — DI assembly:** At application startup, `AddLmpPrograms()` collects all generated descriptors and assembles a `ProgramDescriptor` for each program, validating invariants.

**Step 4 — Runtime compiles the IR to TPL Dataflow:** The runtime reads the `ProgramDescriptor`, topologically sorts the steps via edges, and compiles the IR into a TPL Dataflow pipeline (`TransformBlock`, `ActionBlock`, `JoinBlock`). Each step becomes a Dataflow block; each edge becomes a link. This is the primary compilation target for the IR — see the runtime-execution spec for details.

**Step 5 — Compiler reads the IR:** The compiler reads the `ProgramDescriptor` plus a `CompileSpec` to enumerate candidate variants, run trials, and select the best valid configuration.

---

## 4. IR Invariants

These rules **must always hold** for a valid `ProgramDescriptor`. The DI registration phase validates them at startup; violations throw descriptive exceptions.

| # | Invariant | Rationale |
|---|-----------|-----------|
| 1 | Every `StepDescriptor.Id` must be unique within a program | Step IDs are used as keys in binding resolution and trace output |
| 2 | The graph defined by `Edges` must be a DAG (no cycles) | Cyclic graphs would cause infinite execution; topological sort requires acyclicity |
| 3 | Every `Predict` or `Repair` step must have a non-null `SignatureId` referencing a valid `SignatureDescriptor` | The runtime cannot build a prompt without a signature contract |
| 4 | Every `Predict` step must have a model binding (resolvable at runtime via keyed DI) | An LM call requires a model endpoint |
| 5 | Every `If` step must have a non-null `ConditionExpression` and at least one `ConditionalTrue` outgoing edge | A branch without a condition or a then-path is dead code |
| 6 | Every `Evaluate` step must have a non-null `EvaluatorId` | The runtime needs to know which evaluator to invoke |
| 7 | All edge `FromStepId` / `ToStepId` values must reference existing step IDs | Dangling edges indicate a generation or authoring bug |
| 8 | All `TunableParameterDescriptor.StepId` values must reference existing step IDs | Tunables for nonexistent steps are meaningless |
| 9 | Field `ClrTypeName` values must be serializable by `System.Text.Json` | The prompt builder and output parser depend on JSON round-tripping |

> [!NOTE]
> **Junior Dev Note:** Invariant #2 (DAG check) is why the framework uses a graph IR instead of just a list of steps. A topological sort over the edge set detects cycles at startup — not halfway through a production request.

---

## 5. IR Serialization

The IR serializes to JSON for artifact storage, CLI inspection (`dotnet lmp inspect`), and cross-process communication with the compiler.

### JsonSerializerContext

```csharp
[JsonSerializable(typeof(ProgramDescriptor))]
[JsonSerializable(typeof(StepDescriptor))]
[JsonSerializable(typeof(EdgeDescriptor))]
[JsonSerializable(typeof(SignatureDescriptor))]
[JsonSerializable(typeof(FieldDescriptor))]
[JsonSerializable(typeof(TunableParameterDescriptor))]
[JsonSerializable(typeof(ConstraintDescriptor))]
[JsonSerializable(typeof(CompileSpec))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class IrJsonContext : JsonSerializerContext { }
```

All enums (`StepKind`, `EdgeKind`, `ParameterKind`, `ConstraintOperator`, `ConstraintSeverity`, `ConstraintScope`) serialize as strings via `[JsonConverter(typeof(JsonStringEnumConverter))]`, producing readable JSON like `"kind": "Predict"` instead of `"kind": 1`.

> [!NOTE]
> **Junior Dev Note:** `JsonSerializerContext` enables Native AOT support. Without it, `System.Text.Json` falls back to runtime reflection, which fails in AOT-compiled apps. This is why every IR type is registered explicitly.

---

## 6. IR Versioning

### Strategy

The `ProgramDescriptor.Version` field tracks schema evolution. The rules are simple:

| Change Type | Version Bump | Backward Compatible? |
|---|---|---|
| New optional field added | No bump required | ✅ Yes — old readers ignore unknown fields |
| New required field added | Minor bump (1 → 2) | ❌ No — old readers fail validation |
| Field renamed or removed | Major bump | ❌ No — requires migration |

**Forward compatibility:** The `JsonSerializerContext` is configured with `DefaultIgnoreCondition = WhenWritingNull`, so new optional fields serialized by a newer writer are silently ignored by older readers.

**Backward compatibility:** When the runtime loads an artifact, it checks `Version`. If the artifact version is greater than the runtime's known version, it fails with a descriptive error: *"Artifact was compiled with IR v2 but this runtime only supports v1. Update the LMP NuGet package."*

**ID stability:** IDs are deterministic — `program id = normalized program name`, `signature id = normalized type name`, `step id = normalized step name`. No GUIDs. This means artifacts remain valid across recompilations as long as the author hasn't renamed things.

---

## 7. Complete Example — Ticket Triage MVP

### C# Construction

```csharp
var triageSignature = new SignatureDescriptor(
    Id: "triageticket",
    Name: "TriageTicket",
    Instructions: """
        You are a senior enterprise support triage assistant.
        Classify the issue severity, determine the owning team, and draft a grounded
        customer reply using only the provided evidence and policy context.
        If the evidence is insufficient, say so explicitly.
        """,
    Inputs: [
        new FieldDescriptor("TicketText", "Input", "System.String",
            "Raw customer issue or support ticket text"),
        new FieldDescriptor("AccountTier", "Input", "System.String",
            "Customer plan tier such as Free, Pro, Enterprise"),
        new FieldDescriptor("KnowledgeSnippets", "Input",
            "System.Collections.Generic.IReadOnlyList<System.String>",
            "Relevant knowledge base snippets"),
        new FieldDescriptor("PolicySnippets", "Input",
            "System.Collections.Generic.IReadOnlyList<System.String>",
            "Relevant support or compliance policy snippets"),
    ],
    Outputs: [
        new FieldDescriptor("Severity", "Output", "System.String",
            "Severity: Low, Medium, High, Critical"),
        new FieldDescriptor("RouteToTeam", "Output", "System.String",
            "Owning team name"),
        new FieldDescriptor("DraftReply", "Output", "System.String",
            "Grounded customer reply draft"),
        new FieldDescriptor("Rationale", "Output", "System.String",
            "Reasoning for severity and routing"),
        new FieldDescriptor("Escalate", "Output", "System.Boolean",
            "True if escalation to a human is required"),
    ]);

var program = new ProgramDescriptor(
    Id: "support-triage",
    Name: "support-triage",
    InputTypeName: "Demo.TicketInput",
    OutputTypeName: "Demo.TriageResult",
    Steps: [
        new StepDescriptor(
            Id: "retrieve-kb", Name: "retrieve-kb",
            Kind: StepKind.Retrieve,
            Bindings: new Dictionary<string, string>
                { ["query"] = "input.TicketText" }.ToImmutableDictionary()),
        new StepDescriptor(
            Id: "retrieve-policy", Name: "retrieve-policy",
            Kind: StepKind.Retrieve,
            Bindings: new Dictionary<string, string>
                { ["query"] = "input.TicketText" }.ToImmutableDictionary()),
        new StepDescriptor(
            Id: "triage", Name: "triage",
            Kind: StepKind.Predict,
            SignatureId: "triageticket",
            Bindings: new Dictionary<string, string>
            {
                ["TicketText"] = "input.TicketText",
                ["AccountTier"] = "input.AccountTier",
                ["KnowledgeSnippets"] = "steps.retrieve-kb.Documents",
                ["PolicySnippets"] = "steps.retrieve-policy.Documents"
            }.ToImmutableDictionary()),
        new StepDescriptor(
            Id: "groundedness-check", Name: "groundedness-check",
            Kind: StepKind.Evaluate,
            EvaluatorId: "groundedness",
            Bindings: ImmutableDictionary<string, string>.Empty),
        new StepDescriptor(
            Id: "policy-check", Name: "policy-check",
            Kind: StepKind.Evaluate,
            EvaluatorId: "policy-compliance",
            Bindings: ImmutableDictionary<string, string>.Empty),
        new StepDescriptor(
            Id: "repair-if-needed", Name: "repair-if-needed",
            Kind: StepKind.If,
            ConditionExpression:
                "scores.groundedness-check < 0.90 || !scores.policy-check.passed",
            Bindings: ImmutableDictionary<string, string>.Empty),
        new StepDescriptor(
            Id: "repair-triage", Name: "repair-triage",
            Kind: StepKind.Repair,
            SignatureId: "triageticket",
            Bindings: new Dictionary<string, string>
            {
                ["feedbackFrom"] = "groundedness-check,policy-check"
            }.ToImmutableDictionary()),
    ],
    Edges: [
        new EdgeDescriptor("retrieve-kb", "retrieve-policy", EdgeKind.Sequence),
        new EdgeDescriptor("retrieve-policy", "triage", EdgeKind.Sequence),
        new EdgeDescriptor("triage", "groundedness-check", EdgeKind.Sequence),
        new EdgeDescriptor("triage", "policy-check", EdgeKind.Sequence),
        new EdgeDescriptor("groundedness-check", "repair-if-needed", EdgeKind.Sequence),
        new EdgeDescriptor("policy-check", "repair-if-needed", EdgeKind.Sequence),
        new EdgeDescriptor("repair-if-needed", "repair-triage", EdgeKind.ConditionalTrue),
    ]);
```

### JSON Representation

```json
{
  "id": "support-triage",
  "name": "support-triage",
  "inputTypeName": "Demo.TicketInput",
  "outputTypeName": "Demo.TriageResult",
  "version": 1,
  "steps": [
    {
      "id": "retrieve-kb",
      "name": "retrieve-kb",
      "kind": "Retrieve",
      "bindings": { "query": "input.TicketText" }
    },
    {
      "id": "retrieve-policy",
      "name": "retrieve-policy",
      "kind": "Retrieve",
      "bindings": { "query": "input.TicketText" }
    },
    {
      "id": "triage",
      "name": "triage",
      "kind": "Predict",
      "signatureId": "triageticket",
      "bindings": {
        "TicketText": "input.TicketText",
        "AccountTier": "input.AccountTier",
        "KnowledgeSnippets": "steps.retrieve-kb.Documents",
        "PolicySnippets": "steps.retrieve-policy.Documents"
      }
    },
    {
      "id": "groundedness-check",
      "name": "groundedness-check",
      "kind": "Evaluate",
      "evaluatorId": "groundedness",
      "bindings": {}
    },
    {
      "id": "policy-check",
      "name": "policy-check",
      "kind": "Evaluate",
      "evaluatorId": "policy-compliance",
      "bindings": {}
    },
    {
      "id": "repair-if-needed",
      "name": "repair-if-needed",
      "kind": "If",
      "conditionExpression": "scores.groundedness-check < 0.90 || !scores.policy-check.passed",
      "bindings": {}
    },
    {
      "id": "repair-triage",
      "name": "repair-triage",
      "kind": "Repair",
      "signatureId": "triageticket",
      "bindings": { "feedbackFrom": "groundedness-check,policy-check" }
    }
  ],
  "edges": [
    { "fromStepId": "retrieve-kb",         "toStepId": "retrieve-policy",   "edgeKind": "Sequence" },
    { "fromStepId": "retrieve-policy",     "toStepId": "triage",            "edgeKind": "Sequence" },
    { "fromStepId": "triage",              "toStepId": "groundedness-check", "edgeKind": "Sequence" },
    { "fromStepId": "triage",              "toStepId": "policy-check",       "edgeKind": "Sequence" },
    { "fromStepId": "groundedness-check",  "toStepId": "repair-if-needed",   "edgeKind": "Sequence" },
    { "fromStepId": "policy-check",        "toStepId": "repair-if-needed",   "edgeKind": "Sequence" },
    { "fromStepId": "repair-if-needed",    "toStepId": "repair-triage",      "edgeKind": "ConditionalTrue" }
  ]
}
```

---

## Appendix: Type Quick Reference

| Type | Role | Key Fields |
|---|---|---|
| `ProgramDescriptor` | Root IR node — one per program | Id, Steps, Edges, Version |
| `StepDescriptor` | One graph node | Id, Kind, SignatureId, Bindings |
| `EdgeDescriptor` | One graph edge | From, To, EdgeKind |
| `SignatureDescriptor` | LM task contract | Instructions, Inputs, Outputs |
| `FieldDescriptor` | One input/output field | Name, Direction, ClrTypeName |
| `TunableParameterDescriptor` | Compiler knob | StepId, ParameterKind, Range |
| `ConstraintDescriptor` | Optimization rule | MetricName, Operator, Threshold, Severity |
| `CompileSpec` | Compilation request | ProgramId, Tunables, Constraints, Optimizer |

---

## Appendix: Design-Time IR (MSBuild Layer 2)

### IR as a Build Artifact

The `LmpEmitIr` MSBuild target (see `docs/02-specs/msbuild-targets.md`) reads the compiled assembly after `CoreCompile` and emits IR JSON files to `obj/lmp/`. These files are **design-time artifacts** — they exist to bridge source generators (which can only emit C#) and the CLI optimization tool (which needs the full program graph).

```
obj/lmp/
├── SupportTriageProgram.ir.json     # IR for one program
├── ContentModerationProgram.ir.json # IR for another
└── validation-results.json          # Graph validation output
```

### Use Cases

| Consumer | How It Uses the IR |
|----------|-------------------|
| **`LmpValidateGraph` MSBuild target** | Validates bindings, detects cycles, checks types — during `dotnet build` |
| **`dotnet lmp compile` CLI** | Reads IR instead of re-discovering program metadata via reflection |
| **`dotnet lmp eval` CLI** | Reads IR to understand program structure for evaluation |
| **CI/CD pipelines** | Diff IR between commits to detect prompt/graph regressions |
| **IDE extensions (future)** | Visualize program graph from IR JSON |

### CI/CD Diffing

Because IR is deterministic JSON, teams can diff it between commits:

```yaml
# GitHub Actions example
- name: Check for LMP graph changes
  run: |
    dotnet build
    diff obj/lmp/SupportTriageProgram.ir.json \
         .lmp-baseline/SupportTriageProgram.ir.json \
      || echo "::warning::LMP program graph changed — review before merge"
```

This catches unintentional changes to program structure (e.g., someone renames a step or changes a binding) in PR review — before the expensive compile step.
