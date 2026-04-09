# Source Generator Specification

> **Target:** .NET 10 / C# 14  
> **Generator API:** `IIncrementalGenerator`  
> **Audience:** Implementer — a junior developer should be able to build the generators from this document alone.

---

## 1. Overview

The LMP source generator reads user-defined `TInput` and `TOutput` types at compile time and emits five categories of artifact:

| # | Artifact | Purpose |
|---|----------|---------|
| 1 | `PromptBuilder<TInput, TOutput>` | Assembles `ChatMessage[]` from instructions, field metadata, demos, and current input |
| 2 | `JsonTypeInfo<TOutput>` | Zero-reflection, AOT-safe JSON serialization context for structured output |
| 3 | `GetPredictors()` on `LmpModule` subclasses | Returns all `Predictor<,>` fields — no runtime reflection |
| 4 | ChainOfThought extended output | Internal record adding a `Reasoning` field to `TOutput` |
| 5 | Diagnostics (LMP001–LMP003) | IDE red squiggles for missing descriptions, non-serializable types, non-partial records |

**What is NOT generated:** No graph IR, no program descriptors, no step/edge descriptors, no binding generators, no MSBuild tasks, no DI registration helpers.

### Why Source Generators

| Approach | Startup Cost | AOT Support | IDE Feedback |
|---|---|---|---|
| **Reflection at runtime** | Slow | ❌ Breaks AOT | ❌ None |
| **Source generators at build time** | Zero | ✅ Full AOT | ✅ Compile errors |

Precedent: `System.Text.Json` source gen, gRPC stub generation, EF compiled models.

---

## 2. Generator Architecture

### Project Setup

The generator lives in its own project targeting `netstandard2.0` (Roslyn requirement):

```xml
<!-- LMP.Generators.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
```

### IIncrementalGenerator Pipeline

All LMP generators implement `IIncrementalGenerator` (not the older `ISourceGenerator`). The incremental API caches intermediate models and re-runs generation only when relevant source changes.

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class LmpGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Output types with [LmpSignature]
        var outputTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "LMP.LmpSignatureAttribute",
                predicate: static (node, _) => node is RecordDeclarationSyntax,
                transform: static (ctx, ct) => ExtractOutputModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(outputTypes, static (spc, model) =>
        {
            PromptBuilderEmitter.Emit(spc, model);
            JsonContextEmitter.Emit(spc, model);
        });

        // 2. LmpModule subclasses → GetPredictors()
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "LMP.LmpModuleAttribute",   // or discovered via base-type check
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModuleModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(modules, static (spc, model) =>
            ModuleEmitter.EmitGetPredictors(spc, model));

        // 3. ChainOfThought<TIn, TOut> usages → extended output types
        var cotUsages = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCotGenericName(node),
                transform: static (ctx, ct) => ExtractCotModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(cotUsages, static (spc, model) =>
            CotEmitter.EmitExtendedOutput(spc, model));
    }
}
```

> **Junior Dev Note:** `ForAttributeWithMetadataName` is the preferred discovery API. Roslyn only calls your transform when a type carries the named attribute — far more efficient than scanning every syntax node.

### Incremental Model Requirements

Model objects flowing through the pipeline **must** implement `IEquatable<T>` (or be `record` types). Roslyn uses equality checks to decide whether to re-run downstream stages.

```csharp
public sealed record OutputTypeModel(
    string Namespace,
    string TypeName,
    string Instructions,
    EquatableArray<InputFieldModel> InputFields,
    EquatableArray<OutputFieldModel> OutputFields);

public sealed record InputFieldModel(
    string Name,
    string ClrTypeName,
    string Description);

public sealed record OutputFieldModel(
    string Name,
    string ClrTypeName,
    string Description,
    bool IsRequired);
```

> **Junior Dev Note:** `ImmutableArray<T>` does NOT have structural equality. Wrap it in an `EquatableArray<T>` helper — a thin struct with element-wise equality. This is the #1 incremental-generator cache bug.

### How Generated Files Appear

Generated files are added with hint names like `ClassifyTicket.PromptBuilder.g.cs`. They appear in the IDE under **Dependencies → Analyzers → LMP.Generators**. They are never written to disk in the source tree.

---

## 3. Artifact 1 — `PromptBuilder<TInput, TOutput>`

### What It Does

`PromptBuilder` is a generated class that assembles `ChatMessage[]` from:

1. **Instructions** — from `[LmpSignature("...")]` on `TOutput`
2. **Input field metadata** — names, types, descriptions from `TInput`
3. **Output field metadata** — names, types, descriptions from `TOutput` properties
4. **Few-shot demos** — from `predictor.Demos`
5. **Current input values** — the actual `TInput` instance at call time

No output parsing is needed — `IChatClient.GetResponseAsync<TOutput>()` handles structured output via JSON schema negotiation with the provider.

### Input Field Description Sources

The source generator reads input descriptions from three sources, in priority order:

1. **XML doc comments** — `/// <param name="X">...</param>`
2. **`[Description]` on constructor parameters** — `[Description("...")] string X`
3. **`[Description]` on properties** — for property-based input records

No `property:` prefix is needed. The generator handles all three transparently.

```csharp
// Option A: [Description] on constructor parameters
public record TicketInput(
    [Description("The raw ticket text")] string TicketText,
    [Description("Customer plan tier")] string AccountTier);

// Option B: XML doc comments
/// <param name="TicketText">The raw ticket text</param>
/// <param name="AccountTier">Customer plan tier</param>
public record TicketInput(string TicketText, string AccountTier);

// Option C: [Description] on properties
public record TicketInput
{
    [Description("The raw ticket text")]
    public required string TicketText { get; init; }
}
```

### Prompt Format

The generated `PromptBuilder` creates `ChatMessage[]` in this format:

```
┌─────────────────────────────────────────────────────────────┐
│ SYSTEM MESSAGE                                              │
│                                                             │
│ {Instructions from [LmpSignature]}                          │
│                                                             │
│ Input Fields:                                               │
│ - TicketText (string): The raw ticket text                  │
│ - AccountTier (string): Customer plan tier                  │
│                                                             │
│ Output Fields:                                              │
│ - Category (string): Category: billing, technical, account  │
│ - Urgency (int): Urgency from 1 (low) to 5 (critical)      │
├─────────────────────────────────────────────────────────────┤
│ USER MESSAGE (demo 1 input)                                 │
│                                                             │
│ TicketText: I was charged twice last month                  │
│ AccountTier: Pro                                            │
├─────────────────────────────────────────────────────────────┤
│ ASSISTANT MESSAGE (demo 1 output)                           │
│                                                             │
│ {"Category": "billing", "Urgency": 3}                       │
├─────────────────────────────────────────────────────────────┤
│ USER MESSAGE (current input)                                │
│                                                             │
│ TicketText: My API key stopped working after migration      │
│ AccountTier: Enterprise                                     │
└─────────────────────────────────────────────────────────────┘
```

**Key insight:** DSPy's `ChatAdapter` uses `[[ ## field_name ## ]]` delimiters and parses output text. LMP uses `GetResponseAsync<TOutput>()` for structured output — M.E.AI handles JSON schema negotiation with the provider natively. No output delimiters or parsing needed.

### What the Generator Discovers

For a given `Predictor<TInput, TOutput>`, the generator needs metadata from both types:

| Data | Source |
|---|---|
| Instructions text | `[LmpSignature("...")]` constructor argument on `TOutput` |
| Input field names | Properties (or constructor parameters) of `TInput` |
| Input field types | Fully qualified type names from the semantic model |
| Input field descriptions | XML doc comments, `[Description]` on params, or `[Description]` on properties |
| Output field names | Properties of `TOutput` |
| Output field types | Fully qualified type names from the semantic model |
| Output field descriptions | `[Description]` on each `TOutput` property |
| Output field required flag | Presence of `required` modifier |

### Generated Code

For user types:

```csharp
public record TicketInput(
    [Description("The raw ticket text")] string TicketText,
    [Description("Customer plan tier")] string AccountTier);

[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}
```

The generator emits `ClassifyTicket.PromptBuilder.g.cs`:

```csharp
// <auto-generated />
#nullable enable

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Demo;

[GeneratedCode("LMP.Generators", "1.0.0")]
file static class ClassifyTicketPromptBuilder
{
    private const string Instructions =
        "Classify a support ticket by category and urgency";

    private const string FieldDescriptions =
        """
        Input Fields:
        - TicketText (string): The raw ticket text
        - AccountTier (string): Customer plan tier

        Output Fields:
        - Category (string): Category: billing, technical, account
        - Urgency (int): Urgency from 1 (low) to 5 (critical)
        """;

    public static IList<ChatMessage> BuildMessages(
        TicketInput input,
        IReadOnlyList<(TicketInput Input, ClassifyTicket Output)>? demos = null)
    {
        var messages = new List<ChatMessage>();

        // System message: instructions + field descriptions
        messages.Add(new ChatMessage(ChatRole.System,
            Instructions + "\n\n" + FieldDescriptions));

        // Few-shot demo pairs
        if (demos is not null)
        {
            foreach (var (demoInput, demoOutput) in demos)
            {
                messages.Add(new ChatMessage(ChatRole.User, FormatInput(demoInput)));
                messages.Add(new ChatMessage(ChatRole.Assistant, FormatOutput(demoOutput)));
            }
        }

        // Current input
        messages.Add(new ChatMessage(ChatRole.User, FormatInput(input)));

        return messages;
    }

    private static string FormatInput(TicketInput input)
        => $"""
            TicketText: {input.TicketText}
            AccountTier: {input.AccountTier}
            """;

    private static string FormatOutput(ClassifyTicket output)
        => System.Text.Json.JsonSerializer.Serialize(output,
            ClassifyTicketJsonContext.Default.ClassifyTicket);
}
```

### Implementation — Extract Metadata

```csharp
private static OutputTypeModel? ExtractOutputModel(
    GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        return null;

    // Read [LmpSignature("instructions text")]
    var attr = ctx.Attributes.FirstOrDefault(a =>
        a.AttributeClass?.ToDisplayString() == "LMP.LmpSignatureAttribute");
    if (attr is null)
        return null;

    var instructions = attr.ConstructorArguments.FirstOrDefault().Value as string ?? "";

    // Collect output fields from TOutput properties
    var outputFields = new List<OutputFieldModel>();
    foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
    {
        ct.ThrowIfCancellationRequested();

        var desc = GetDescriptionFromAttribute(member) ?? "";
        var clrType = member.Type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "");

        outputFields.Add(new OutputFieldModel(
            member.Name, clrType, desc, member.IsRequired));
    }

    var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
        ? ""
        : typeSymbol.ContainingNamespace.ToDisplayString();

    return new OutputTypeModel(
        Namespace: ns,
        TypeName: typeSymbol.Name,
        Instructions: instructions,
        InputFields: default, // populated when Predictor<TIn, TOut> is resolved
        OutputFields: new EquatableArray<OutputFieldModel>(
            outputFields.ToImmutableArray()));
}

private static string? GetDescriptionFromAttribute(IPropertySymbol prop)
{
    // Check for [Description("...")] from System.ComponentModel
    var descAttr = prop.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass?.ToDisplayString() ==
            "System.ComponentModel.DescriptionAttribute");
    return descAttr?.ConstructorArguments.FirstOrDefault().Value as string;
}
```

> **Junior Dev Note:** Always call `ct.ThrowIfCancellationRequested()` in loops. Roslyn cancels generators frequently during typing — not checking the token makes the IDE sluggish.

---

## 4. Artifact 2 — `JsonTypeInfo<TOutput>`

### What It Does

Emits a `System.Text.Json` source-generated serialization context for the output type. This enables zero-reflection, AOT-safe serialization — required by `GetResponseAsync<TOutput>()`.

### Generated Code

For `ClassifyTicket`, the generator emits `ClassifyTicket.JsonContext.g.cs`:

```csharp
// <auto-generated />
#nullable enable

using System.CodeDom.Compiler;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo;

[GeneratedCode("LMP.Generators", "1.0.0")]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClassifyTicket))]
file partial class ClassifyTicketJsonContext : JsonSerializerContext;
```

The `JsonSerializerContext` source generator (built into `System.Text.Json`) picks this up and emits the actual `JsonTypeInfo<ClassifyTicket>` metadata. LMP's generator only needs to emit the `[JsonSerializable]` declaration — the STJ generator does the heavy lifting.

### Why This Matters

| Without source gen | With source gen |
|---|---|
| `JsonSerializer.Serialize<T>()` uses reflection | `JsonSerializer.Serialize(value, Context.Default.T)` — no reflection |
| Breaks AOT compilation | Full AOT support |
| Slower first-call (JIT type analysis) | Zero startup cost |

---

## 5. Artifact 3 — `GetPredictors()` on `LmpModule`

### What It Does

For every class that extends `LmpModule`, the generator emits a `GetPredictors()` method that returns all `Predictor<,>` fields. This replaces Python's `named_predictors()`, which walks `__dict__` at runtime.

### What the Generator Discovers

| Data | Source |
|---|---|
| Module type name + namespace | The class declaration |
| Predictor fields | Fields whose type is `Predictor<TIn, TOut>` or a subclass (e.g., `ChainOfThought<TIn, TOut>`) |
| Field names | The field identifier (used as predictor name for optimization) |
| Generic type arguments | `TInput` and `TOutput` from `Predictor<TIn, TOut>` |

### User Code

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

    public async Task<DraftReply> ForwardAsync(TicketInput input)
    {
        var classification = await _classify.PredictAsync(input);
        return await _draft.PredictAsync(classification);
    }
}
```

### Generated Code

`TicketTriageModule.Predictors.g.cs`:

```csharp
// <auto-generated />
#nullable enable

using System.CodeDom.Compiler;
using System.Collections.Generic;
using LMP;

namespace Demo;

[GeneratedCode("LMP.Generators", "1.0.0")]
partial class TicketTriageModule
{
    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        =>
        [
            ("_classify", _classify),
            ("_draft", _draft),
        ];
}
```

### Why This Matters

Optimizers (`BootstrapFewShot`, `BootstrapRandomSearch`) need to enumerate a module's predictors to:
- Collect traces during optimization
- Fill `predictor.Demos` with bootstrapped few-shot examples
- Save/load learnable state

In DSPy, `named_predictors()` walks `self.__dict__` at runtime. LMP does this at compile time — zero reflection, AOT-safe, and visible in IntelliSense.

### Implementation

```csharp
private static ModuleModel? ExtractModuleModel(
    GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
{
    if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        return null;

    // Find all fields of type Predictor<,> or derived
    var predictorFields = new List<PredictorFieldModel>();

    foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
    {
        ct.ThrowIfCancellationRequested();

        if (!IsPredictorType(member.Type))
            continue;

        predictorFields.Add(new PredictorFieldModel(
            FieldName: member.Name,
            InputType: GetTypeArg(member.Type, 0),
            OutputType: GetTypeArg(member.Type, 1)));
    }

    if (predictorFields.Count == 0)
        return null;

    return new ModuleModel(
        Namespace: typeSymbol.ContainingNamespace.ToDisplayString(),
        TypeName: typeSymbol.Name,
        Predictors: new EquatableArray<PredictorFieldModel>(
            predictorFields.ToImmutableArray()));
}

private static bool IsPredictorType(ITypeSymbol type)
{
    // Walk the base type chain looking for Predictor<,>
    var current = type;
    while (current is not null)
    {
        if (current is INamedTypeSymbol { IsGenericType: true } named &&
            named.ConstructedFrom.ToDisplayString() == "LMP.Predictor<TInput, TOutput>")
            return true;
        current = current.BaseType;
    }
    return false;
}
```

---

## 6. Artifact 4 — ChainOfThought Extended Output

### What It Does

When a user writes `ChainOfThought<TIn, TOut>`, the source generator creates an **internal extended record** that adds a `Reasoning` field to `TOut`. The LM produces step-by-step reasoning before the final answer, then the framework strips the reasoning and returns the original `TOut`.

### User Code

```csharp
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

// ChainOfThought wraps the output type
var cot = new ChainOfThought<TicketInput, ClassifyTicket>(client);
var result = await cot.PredictAsync(input);
// result has .Category + .Urgency (Reasoning is used internally, not exposed)
```

### Generated Code

`ClassifyTicket.ChainOfThought.g.cs`:

```csharp
// <auto-generated />
#nullable enable

using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Demo;

/// <summary>
/// Extended output type for ChainOfThought reasoning.
/// The Reasoning field is sent to the LM as an additional output field
/// positioned before the real output fields, encouraging step-by-step thinking.
/// </summary>
[GeneratedCode("LMP.Generators", "1.0.0")]
internal partial record ClassifyTicketWithReasoning
{
    [Description("Think step by step to work toward the answer")]
    [JsonPropertyOrder(-1)]
    public required string Reasoning { get; init; }

    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

[JsonSerializable(typeof(ClassifyTicketWithReasoning))]
[GeneratedCode("LMP.Generators", "1.0.0")]
internal partial class ClassifyTicketWithReasoningJsonContext : JsonSerializerContext;
```

### How `ChainOfThought<TIn, TOut>` Uses It

At runtime, `ChainOfThought` internally:

1. Calls `GetResponseAsync<ClassifyTicketWithReasoning>()` (the extended type)
2. The LM fills in `Reasoning` first, then `Category` and `Urgency`
3. Maps the result back to `ClassifyTicket` (strips `Reasoning`)

This mirrors DSPy's `ChainOfThought`, which prepends a `rationale` field to the signature at runtime. LMP does it at compile time.

### What the Generator Discovers

| Data | Source |
|---|---|
| `TOut` type and its properties | From `ChainOfThought<TIn, TOut>` usage in source |
| Output field descriptions | `[Description]` on each `TOut` property |
| Output field types | Semantic model |

The generator locates `ChainOfThought<TIn, TOut>` usages by scanning for the generic type name in syntax, then resolving the `TOut` type argument via the semantic model.

---

## 7. Diagnostics

The generator reports **2–3 focused diagnostics**. Avoid diagnostic sprawl — start small, add more based on real usage.

### Diagnostic Definitions

```csharp
using Microsoft.CodeAnalysis;

namespace LMP.Generators;

public static class LmpDiagnostics
{
    private const string Category = "LMP.Authoring";

    /// <summary>
    /// LMP001: Output type property is missing [Description].
    /// The LM needs field descriptions to understand what to produce.
    /// </summary>
    public static readonly DiagnosticDescriptor LMP001_MissingOutputDescription = new(
        id: "LMP001",
        title: "Output property missing [Description]",
        messageFormat:
            "Property '{0}' on output type '{1}' is missing a [Description] attribute. "
          + "Add [Description(\"...\")] so the LM knows what this field represents.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Every output property should have a [Description] attribute so the LM "
          + "knows what the field represents. Without it, the LM may produce "
          + "unpredictable values.");

    /// <summary>
    /// LMP002: Output type has a property with a non-JSON-serializable type.
    /// GetResponseAsync&lt;T&gt;() requires all output properties to be
    /// serializable by System.Text.Json.
    /// </summary>
    public static readonly DiagnosticDescriptor LMP002_NonSerializableOutputProperty = new(
        id: "LMP002",
        title: "Non-serializable output property type",
        messageFormat:
            "Property '{0}' on output type '{1}' has type '{2}' which cannot be "
          + "serialized by System.Text.Json source generation.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Output properties must use JSON-serializable types: string, bool, "
          + "int, long, float, double, decimal, enum, DateTime, "
          + "IReadOnlyList<T>, or nested records with [LmpSignature]. "
          + "GetResponseAsync<T>() uses System.Text.Json under the hood.");

    /// <summary>
    /// LMP003 (optional): [LmpSignature] is placed on a type that is not
    /// a partial record. The generator cannot extend non-partial types.
    /// </summary>
    public static readonly DiagnosticDescriptor LMP003_NonPartialRecord = new(
        id: "LMP003",
        title: "[LmpSignature] on non-partial record",
        messageFormat:
            "Type '{0}' has [LmpSignature] but is not declared as 'partial record'. "
          + "Change to 'public partial record {0}' so the source generator can extend it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "[LmpSignature] output types must be declared as 'partial record' so "
          + "the source generator can emit additional members (PromptBuilder, "
          + "JsonTypeInfo, etc.) in a companion .g.cs file.");
}
```

### When Diagnostics Fire

| ID | Fires when | Severity | Generator action |
|---|---|---|---|
| **LMP001** | An output property has no `[Description]` attribute | Warning | Still generates — uses property name as fallback description |
| **LMP002** | An output property type is not JSON-serializable (e.g., `Stream`, `Func<>`, `IntPtr`) | Error | Skips generation for this type (emit nothing rather than broken code) |
| **LMP003** | `[LmpSignature]` is on a `class`, `struct`, non-partial `record`, or non-partial type | Error | Skips generation for this type |

### Reporting from the Generator

```csharp
context.RegisterSourceOutput(outputTypes, static (spc, model) =>
{
    // LMP003: must be partial record
    if (!model.IsPartialRecord)
    {
        spc.ReportDiagnostic(Diagnostic.Create(
            LmpDiagnostics.LMP003_NonPartialRecord,
            model.Location,
            model.TypeName));
        return; // skip generation entirely
    }

    // LMP001: warn on missing descriptions
    foreach (var field in model.OutputFields)
    {
        if (string.IsNullOrWhiteSpace(field.Description))
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                LmpDiagnostics.LMP001_MissingOutputDescription,
                field.Location,
                field.Name,
                model.TypeName));
        }
    }

    // LMP002: error on non-serializable types
    foreach (var field in model.OutputFields)
    {
        if (!IsJsonSerializable(field.ClrTypeName))
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                LmpDiagnostics.LMP002_NonSerializableOutputProperty,
                field.Location,
                field.Name,
                model.TypeName,
                field.ClrTypeName));
            return; // skip generation
        }
    }

    PromptBuilderEmitter.Emit(spc, model);
    JsonContextEmitter.Emit(spc, model);
});
```

---

## 8. Generated Code Conventions

All generated code follows these rules:

| Rule | Rationale |
|---|---|
| `// <auto-generated />` header | Tells IDEs/tools to suppress formatting and analysis |
| `#nullable enable` | Match the user's nullable context |
| `[GeneratedCode("LMP.Generators", "1.0.0")]` on every generated type | Enables code-coverage and analysis tool exclusion |
| `file class` for all internal helper types | Prevents namespace pollution; follows `System.Text.Json` precedent |
| Hint name: `<TypeName>.<Artifact>.g.cs` | E.g., `ClassifyTicket.PromptBuilder.g.cs`, `ClassifyTicket.JsonContext.g.cs` |
| Namespace: same as the user's type | Generated partial classes must share the namespace |
| Raw string literals `"""..."""` for multi-line text | Avoids escaping issues in instructions |
| `static partial` properties (C# 14) for generated members | More idiomatic than `static abstract` interface methods |

---

## 9. Testing Source Generators

### Snapshot / Golden File Testing

Generated output must be **snapshot-tested**: provide a known input, run the generator, compare output to a saved golden file.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.Generators.Tests;

public class PromptBuilderGeneratorTests
{
    [Fact]
    public void Generates_PromptBuilder_For_Simple_Output_Type()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            public record TicketInput(
                [Description("The raw ticket text")] string TicketText);

            [LmpSignature("Classify the ticket severity.")]
            public partial record SimpleTicket
            {
                [Description("Low, Medium, High, Critical")]
                public required string Severity { get; init; }
            }
            """;

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(LmpSignatureAttribute).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new LmpGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        // No diagnostics
        Assert.Empty(diagnostics);

        // Snapshot match
        var runResult = driver.GetRunResult();
        var generatedSource = runResult.GeneratedTrees
            .Single(t => t.FilePath.Contains("PromptBuilder"));
        var actualText = generatedSource.GetText().ToString();

        var goldenPath = Path.Combine("Snapshots", "SimpleTicket.PromptBuilder.g.verified.cs");
        Assert.Equal(File.ReadAllText(goldenPath), actualText);

        // No compile errors
        Assert.Empty(outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Reports_LMP001_For_Missing_Description()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record BadOutput
            {
                public required string NoDescription { get; init; }
            }
            """;

        // ... run generator ...

        var diagnostics = runResult.Diagnostics;
        var lmp001 = Assert.Single(diagnostics, d => d.Id == "LMP001");
        Assert.Contains("NoDescription", lmp001.GetMessage());
    }
}
```

> **Junior Dev Note:** Consider using the [Verify](https://github.com/VerifyTests/Verify) library to automate snapshot management — it auto-accepts changes during development and diffs them in CI.

---

## 10. Common Pitfalls

### Debugging Generators

Source generators run inside the compiler process. Two debugging approaches:

**Approach 1: `Debugger.Launch()`**

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
#if DEBUG
    if (!System.Diagnostics.Debugger.IsAttached)
        System.Diagnostics.Debugger.Launch();
#endif
}
```

**Approach 2: Diagnostic dump**

```csharp
spc.ReportDiagnostic(Diagnostic.Create(
    new DiagnosticDescriptor("LMPDBG", "Debug", "Model: {0}",
        "Debug", DiagnosticSeverity.Warning, true),
    Location.None,
    model.ToString()));
```

### Incremental Cache Invalidation

The #1 bug: your model type does not implement equality correctly → generator re-runs on every keystroke.

**Fix:** Every field in your model must be a value type, `string`, or `EquatableArray<T>`. Never store `ISymbol`, `SyntaxNode`, or `Location` in the model.

### Handling Malformed User Code

The generator will encounter partially written code (user is mid-keystroke):

1. **Return `null` from the transform** if data is missing — the `.Where(m => m is not null)` filter removes it.
2. **Never throw exceptions** — an unhandled exception silently kills all generation.
3. **Report diagnostics** for structurally invalid but syntactically complete code.

---

## 11. What's Intentionally Excluded

| Dropped Concept | Why |
|---|---|
| `ProgramDescriptor` / `StepDescriptor` / graph IR | DSPy has no graph IR. Modules are plain classes with `forward()`. LMP mirrors this with `LmpModule.ForwardAsync()`. |
| Binding generators (convention, `[BindFrom]`, interceptors) | Over-engineered. `Predictor<TIn, TOut>` with source-gen `PromptBuilder` covers all binding. |
| MSBuild targets for graph validation | No graphs → no graph validation. `dotnet build` with source gen diagnostics is sufficient. |
| `SignatureDescriptor` / `FieldDescriptor` types | Replaced by `PromptBuilder` — the generator emits code that *does the work*, not metadata records. |
| DI registration helpers | Not the generator's job. Users register services the standard .NET way. |
| 7+ diagnostics with code fixes | Start with 2–3 essential diagnostics. Add more based on real usage. |
| Convention-based auto-discovery | Post-MVP. Explicit registration is fine for now. |

---

## 12. DSPy Comparison

| Capability | DSPy (Python, runtime) | LMP (C#, compile-time) |
|---|---|---|
| Prompt assembly | Runtime string formatting in `ChatAdapter` | Source gen emits `PromptBuilder` class with baked-in constants |
| Output parsing | `ChatAdapter` parses `[[ ## field ## ]]` delimiters | Not needed — `GetResponseAsync<T>()` handles structured output |
| Field type validation | Pydantic runtime validation | Source gen emits `JsonTypeInfo<T>` + LMP002 diagnostic |
| Predictor discovery | `named_predictors()` walks `__dict__` at runtime | Source gen emits `GetPredictors()` |
| Chain-of-thought | Prepends `rationale` field at runtime | Source gen creates extended record at build time |
| Missing descriptions | Runtime error | LMP001 IDE warning at build time |
| State serialization | Runtime introspection (`pickle`/JSON) | Source gen `JsonSerializerContext` — AOT-safe |

*Source: DSPy's [`dspy/signatures/signature.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/signatures/signature.py) and [`dspy/adapters/chat_adapter.py`](https://github.com/stanfordnlp/dspy/blob/main/dspy/adapters/chat_adapter.py).*
