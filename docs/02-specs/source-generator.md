# Source Generator Specification

> **Derived from:** spec.org §5A (Authoring-Time Architecture) and §9 (Build-Time Responsibilities)
>
> **Audience:** Implementer — a junior developer should be able to build the generators from this document alone.

---

## 1. Why Source Generators (for Non-Experts)

### What Source Generators Are

Source generators are **code that writes code at compile time**. They are a Roslyn compiler feature (shipped since .NET 5) that lets you inspect the developer's source code during compilation and emit additional `.g.cs` files into the build. Those generated files are compiled alongside hand-written code — no runtime reflection needed.

### Why LMP Needs Them

The LMP framework uses attributed C# types to define LM program signatures and programs. At some point, the runtime needs structured metadata about those types: which properties are inputs, which are outputs, what the instructions say, what steps a program defines. There are two ways to get that metadata:

| Approach | Startup Cost | AOT Support | IDE Feedback | Deployment |
|---|---|---|---|---|
| **Reflection at runtime** | Slow (scan assemblies) | ❌ Breaks AOT | ❌ None | Fragile |
| **Source generators at build time** | Zero | ✅ Full AOT | ✅ Compile errors | Deterministic |

Source generators give us:

1. **No reflection** — descriptor records are emitted as plain C# at compile time.
2. **AOT compatibility** — no `Type.GetProperties()` or `Activator.CreateInstance` at runtime.
3. **IDE feedback** — diagnostics appear as squiggly underlines while typing.
4. **Deterministic output** — generated code is inspectable, testable, and version-controllable.

### Precedent

This pattern is well-established in .NET:

- **System.Text.Json** — `JsonSerializerContext` generates serialization metadata at compile time.
- **gRPC / Protobuf** — service stubs are generated from `.proto` files.
- **Entity Framework** — compiled models are generated for startup performance.

### What Happens WITHOUT Generators

Without generators the framework would rely on runtime reflection to discover `[LmpSignature]` types, scan properties for `[Input]`/`[Output]` attributes, and build descriptors on first use. This means slower startup, no AOT, no IDE-time validation, and opaque metadata that cannot be snapshot-tested.

---

## 2. Generator Architecture

### The IIncrementalGenerator Pipeline

All LMP generators implement `IIncrementalGenerator` (not the older `ISourceGenerator`). The incremental API lets Roslyn cache intermediate models and re-run generation only when the relevant source actually changes.

```csharp
using Microsoft.CodeAnalysis;

[Generator(LanguageNames.CSharp)]
public sealed class LmpSignatureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Discover candidate types
        var pipeline = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "LMP.LmpSignatureAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModel(ctx, ct))
            .Where(static m => m is not null);

        // 2. Register output
        context.RegisterSourceOutput(pipeline, static (spc, model) =>
        {
            var source = GenerateSource(model!);
            spc.AddSource($"{model!.TypeName}.g.cs", source);
        });
    }
}
```

> **Junior Dev Note:** `ForAttributeWithMetadataName` is the preferred discovery API. It tells Roslyn to only call your transform when a type actually carries the named attribute, skipping everything else. This is far more efficient than scanning every syntax node.

### Incremental Model Requirements

The model objects flowing through the pipeline **must** implement `IEquatable<T>` (or be `record` types, which do so automatically). Roslyn uses equality checks to decide whether to re-run downstream stages. If equality is broken, the generator re-runs on every keystroke.

```csharp
// Records give you structural equality for free
public sealed record SignatureDescriptorModel(
    string Namespace,
    string TypeName,
    string NormalizedId,
    string Instructions,
    EquatableArray<FieldModel> Inputs,
    EquatableArray<FieldModel> Outputs);

public sealed record FieldModel(
    string Name,
    string Direction,      // "Input" or "Output"
    string ClrTypeName,
    string Description,
    bool IsRequired);
```

> **Junior Dev Note:** `ImmutableArray<T>` does NOT have structural equality — `default == default` is `true`, but two arrays with the same elements are NOT equal. Wrap it in an `EquatableArray<T>` helper (a thin struct that implements element-wise equality). This is the #1 incremental-generator cache bug.

### How Generated Files Appear

Generated files are added to the compilation with a hint name like `TriageTicket.g.cs`. They appear in the IDE under **Dependencies → Analyzers → LMP.Generators → LMP.Generators.LmpSignatureGenerator**. They are never written to disk in the source tree.

---

## 3. Signature Generator

### 3.1 What It Discovers

The Signature Generator finds every type decorated with `[LmpSignature]` and extracts:

| Data | Source |
|---|---|
| Type name + namespace | The class declaration's semantic symbol |
| Normalized ID | Type name lower-cased (e.g., `"triageticket"`) |
| Instructions text | `LmpSignatureAttribute.Instructions` constructor argument |
| Input fields | Properties decorated with `[Input]` |
| Output fields | Properties decorated with `[Output]` |
| Field descriptions | `Description` property on each `[Input]`/`[Output]` attribute |
| Field CLR type | Fully qualified type name from the semantic model |
| Field required flag | Presence of `required` modifier on the property |

### 3.2 What It Generates

For the canonical `TriageTicket` signature, the generator emits the following `TriageTicket.g.cs`:

```csharp
// <auto-generated />
// Generated by LMP.Generators.LmpSignatureGenerator
#nullable enable

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using LMP;

namespace Demo;

[GeneratedCode("LMP.Generators", "1.0.0")]
file static class TriageTicketDescriptor
{
    public static readonly SignatureDescriptor Instance = new(
        Id: "triageticket",
        Name: "TriageTicket",
        Instructions: """
            You are a senior enterprise support triage assistant.

            Classify the issue severity, determine the owning team, and draft a grounded
            customer reply using only the provided evidence and policy context.

            If the evidence is insufficient, say so explicitly.
            """,
        Inputs: ImmutableArray.Create(
            new FieldDescriptor("TicketText", "Input", "System.String",
                "Raw customer issue or support ticket text", true),
            new FieldDescriptor("AccountTier", "Input", "System.String",
                "Customer plan tier such as Free, Pro, Enterprise", true),
            new FieldDescriptor("KnowledgeSnippets", "Input",
                "System.Collections.Generic.IReadOnlyList<System.String>",
                "Relevant knowledge base snippets", true),
            new FieldDescriptor("PolicySnippets", "Input",
                "System.Collections.Generic.IReadOnlyList<System.String>",
                "Relevant support or compliance policy snippets", true)),
        Outputs: ImmutableArray.Create(
            new FieldDescriptor("Severity", "Output", "System.String",
                "Severity: Low, Medium, High, Critical", true),
            new FieldDescriptor("RouteToTeam", "Output", "System.String",
                "Owning team name", true),
            new FieldDescriptor("DraftReply", "Output", "System.String",
                "Grounded customer reply draft", true),
            new FieldDescriptor("Rationale", "Output", "System.String",
                "Reasoning for severity and routing", true),
            new FieldDescriptor("Escalate", "Output", "System.Boolean",
                "True if escalation to a human is required", true)));
}

// Partial class extension: wire the descriptor into the user's type
[GeneratedCode("LMP.Generators", "1.0.0")]
partial class TriageTicket : ISignature
{
    static partial SignatureDescriptor Descriptor { get; }
        = TriageTicketDescriptor.Instance;
}

// DI registration helper
[GeneratedCode("LMP.Generators", "1.0.0")]
file static class TriageTicketRegistration
{
    public static IServiceCollection AddTriageTicketSignature(
        this IServiceCollection services)
    {
        services.AddSingleton(TriageTicketDescriptor.Instance);
        return services;
    }
}
```

Key patterns in the generated code:

- **`file class`** (C# 11) — `TriageTicketDescriptor` and `TriageTicketRegistration` use the `file` modifier so they are invisible outside the generated file. This prevents polluting the user's namespace.
- **`[GeneratedCode]`** — marks all generated types for tooling (code coverage exclusion, static analysis suppression).
- **`ISignature.Descriptor`** — a `static partial` property (C# 14) that the generator implements on the user's `partial class`. The runtime reads this property to obtain metadata without reflection. The partial property pattern replaces the previous `CreateDescriptor()` method — it is more idiomatic, discoverable via IntelliSense, and cannot be accidentally shadowed.
- **`ImmutableArray`** — field lists are immutable, matching the framework's determinism requirement.

### 3.3 Step-by-Step Implementation

#### Step 1 — Create the Generator Project

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

#### Step 2 — Register the ForAttributeWithMetadataName Pipeline

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class LmpSignatureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var signatures = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "LMP.LmpSignatureAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractSignatureModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(signatures, static (spc, model) =>
            Emitter.EmitSignature(spc, model));
    }
}
```

#### Step 3 — Extract Metadata from the Semantic Model

```csharp
private static SignatureDescriptorModel? ExtractSignatureModel(
    GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        return null;

    // Read [LmpSignature(Instructions = "...")]
    var attr = ctx.Attributes.FirstOrDefault(a =>
        a.AttributeClass?.ToDisplayString() == "LMP.LmpSignatureAttribute");
    if (attr is null)
        return null;

    var instructions = attr.NamedArguments
        .FirstOrDefault(kvp => kvp.Key == "Instructions")
        .Value.Value as string ?? "";

    // Scan properties for [Input] / [Output]
    var inputs = new List<FieldModel>();
    var outputs = new List<FieldModel>();

    foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
    {
        ct.ThrowIfCancellationRequested();

        foreach (var propAttr in member.GetAttributes())
        {
            var attrName = propAttr.AttributeClass?.ToDisplayString();
            string? direction = attrName switch
            {
                "LMP.InputAttribute" => "Input",
                "LMP.OutputAttribute" => "Output",
                _ => null
            };
            if (direction is null) continue;

            var desc = propAttr.NamedArguments
                .FirstOrDefault(kvp => kvp.Key == "Description")
                .Value.Value as string ?? "";

            bool isRequired = member.IsRequired;
            var clrType = member.Type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");

            var field = new FieldModel(member.Name, direction, clrType, desc, isRequired);

            if (direction == "Input") inputs.Add(field);
            else outputs.Add(field);
        }
    }

    var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
        ? ""
        : typeSymbol.ContainingNamespace.ToDisplayString();

    return new SignatureDescriptorModel(
        Namespace: ns,
        TypeName: typeSymbol.Name,
        NormalizedId: typeSymbol.Name.ToLowerInvariant(),
        Instructions: instructions,
        Inputs: new EquatableArray<FieldModel>(inputs.ToImmutableArray()),
        Outputs: new EquatableArray<FieldModel>(outputs.ToImmutableArray()));
}
```

> **Junior Dev Note:** Always call `ct.ThrowIfCancellationRequested()` in loops. Roslyn cancels generators frequently during typing — not checking the token makes the IDE sluggish.

#### Step 4 — Build the SignatureDescriptorModel

The model record (shown in §2) is the single data structure flowing through the pipeline. It must be:

- A `record` (for structural equality).
- Free of Roslyn symbols (symbols are not equatable and hold compilation references that prevent caching).
- Composed of primitives, strings, and `EquatableArray<T>`.

#### Step 5 — Generate the .g.cs File

```csharp
internal static class Emitter
{
    public static void EmitSignature(
        SourceProductionContext spc, SignatureDescriptorModel model)
    {
        var ns = string.IsNullOrEmpty(model.Namespace)
            ? "" : $"namespace {model.Namespace};\n";

        var inputFields = string.Join(",\n            ",
            model.Inputs.Select(FormatField));
        var outputFields = string.Join(",\n            ",
            model.Outputs.Select(FormatField));

        var source = $$"""
            // <auto-generated />
            #nullable enable
            
            using System;
            using System.CodeDom.Compiler;
            using System.Collections.Immutable;
            using LMP;
            
            {{ns}}
            [GeneratedCode("LMP.Generators", "1.0.0")]
            file static class {{model.TypeName}}Descriptor
            {
                public static readonly SignatureDescriptor Instance = new(
                    Id: "{{model.NormalizedId}}",
                    Name: "{{model.TypeName}}",
                    Instructions: """
                        {{model.Instructions}}
                        """,
                    Inputs: ImmutableArray.Create(
                        {{inputFields}}),
                    Outputs: ImmutableArray.Create(
                        {{outputFields}}));
            }
            
            [GeneratedCode("LMP.Generators", "1.0.0")]
            partial class {{model.TypeName}} : ISignature
            {
                static partial SignatureDescriptor Descriptor { get; }
                    = {{model.TypeName}}Descriptor.Instance;
            }
            
            [GeneratedCode("LMP.Generators", "1.0.0")]
            file static class {{model.TypeName}}Registration
            {
                public static IServiceCollection Add{{model.TypeName}}Signature(
                    this IServiceCollection services)
                {
                    services.AddSingleton({{model.TypeName}}Descriptor.Instance);
                    return services;
                }
            }
            """;

        spc.AddSource($"{model.TypeName}.g.cs", source);
    }

    private static string FormatField(FieldModel f)
        => $"""new FieldDescriptor("{f.Name}", "{f.Direction}", "{f.ClrTypeName}", "{f.Description}", {(f.IsRequired ? "true" : "false")})""";
}
```

---

## 4. Program Generator

### 4.1 What It Discovers

The Program Generator finds types decorated with `[LmpProgram]` that inherit `LmpProgram<TIn, TOut>`. It inspects the `Build()` method to extract:

| Data | Source |
|---|---|
| Program name | `[LmpProgram("support-triage")]` attribute argument |
| Input/Output types | Generic type arguments of `LmpProgram<TIn, TOut>` |
| Steps | `Step.Predict`, `Step.Retrieve`, `Step.Evaluate`, `Step.If`, `Step.Repair` calls |
| Step names | The `name:` argument of each step call |
| Graph edges | `StartWith().Then()` chain order |
| Signature references | Generic argument of `Step.Predict<T>` / `Step.Repair<T>` |
| Tunable parameters | Step properties like `topK`, model, temperature |

### 4.2 What It Generates

For `SupportTriageProgram`, the generator emits `SupportTriageProgram.g.cs`:

```csharp
// <auto-generated />
#nullable enable

using System;
using System.CodeDom.Compiler;
using System.Collections.Immutable;
using LMP;

namespace Demo;

[GeneratedCode("LMP.Generators", "1.0.0")]
file static class SupportTriageProgramDescriptor
{
    public static readonly ProgramDescriptor Instance = new(
        Id: "support-triage",
        Name: "SupportTriageProgram",
        InputType: "Demo.TicketInput",
        OutputType: "Demo.TriageResult",
        Steps: ImmutableArray.Create(
            new StepDescriptor("retrieve-kb", StepKind.Retrieve, SignatureRef: null),
            new StepDescriptor("retrieve-policy", StepKind.Retrieve, SignatureRef: null),
            new StepDescriptor("triage", StepKind.Predict, SignatureRef: "triageticket"),
            new StepDescriptor("groundedness-check", StepKind.Evaluate, SignatureRef: null),
            new StepDescriptor("policy-check", StepKind.Evaluate, SignatureRef: null),
            new StepDescriptor("repair-if-needed", StepKind.If, SignatureRef: null),
            new StepDescriptor("repair-triage", StepKind.Repair, SignatureRef: "triageticket")),
        Edges: ImmutableArray.Create(
            new EdgeDescriptor("retrieve-kb", "retrieve-policy"),
            new EdgeDescriptor("retrieve-policy", "triage"),
            new EdgeDescriptor("triage", "groundedness-check"),
            new EdgeDescriptor("groundedness-check", "policy-check"),
            new EdgeDescriptor("policy-check", "repair-if-needed")),
        Tunables: ImmutableArray.Create(
            new TunableDescriptor("retrieve-kb", "TopK", "System.Int32", "5"),
            new TunableDescriptor("retrieve-policy", "TopK", "System.Int32", "3")));
}

[GeneratedCode("LMP.Generators", "1.0.0")]
partial class SupportTriageProgram : IProgram
{
    static partial ProgramDescriptor Descriptor { get; }
        = SupportTriageProgramDescriptor.Instance;
}

[GeneratedCode("LMP.Generators", "1.0.0")]
file static class SupportTriageProgramRegistration
{
    public static IServiceCollection AddSupportTriageProgram(
        this IServiceCollection services)
    {
        services.AddSingleton(SupportTriageProgramDescriptor.Instance);
        services.AddTransient<SupportTriageProgram>();
        return services;
    }
}
```

### 4.3 Step-by-Step Implementation

#### Step 1 — Generator Class

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class LmpProgramGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var programs = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "LMP.LmpProgramAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractProgramModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(programs, static (spc, model) =>
            ProgramEmitter.Emit(spc, model));
    }
}
```

#### Step 2 — Extract Program Metadata

The `Build()` method is analyzed via the semantic model. The generator walks method invocations looking for `Step.*` factory calls and `Graph` builder chains:

```csharp
private static ProgramDescriptorModel? ExtractProgramModel(
    GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
{
    if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        return null;

    // Read [LmpProgram("support-triage")]
    var attr = ctx.Attributes.First();
    var programId = attr.ConstructorArguments.FirstOrDefault().Value as string ?? "";

    // Resolve TIn / TOut from LmpProgram<TIn, TOut>
    var baseType = typeSymbol.BaseType;
    var inputType = baseType?.TypeArguments[0].ToDisplayString() ?? "object";
    var outputType = baseType?.TypeArguments[1].ToDisplayString() ?? "object";

    // Walk the Build() method body for Step.* invocations
    var buildMethod = typeSymbol.GetMembers("Build")
        .OfType<IMethodSymbol>().FirstOrDefault();
    if (buildMethod is null)
        return null;

    var syntaxRef = buildMethod.DeclaringSyntaxReferences.FirstOrDefault();
    if (syntaxRef is null)
        return null;

    var methodSyntax = syntaxRef.GetSyntax(ct) as MethodDeclarationSyntax;
    var semanticModel = ctx.SemanticModel;

    var steps = new List<StepModel>();
    var edges = new List<EdgeModel>();

    // Collect Step.* invocations
    foreach (var invocation in methodSyntax!.DescendantNodes()
        .OfType<InvocationExpressionSyntax>())
    {
        ct.ThrowIfCancellationRequested();

        if (semanticModel.GetSymbolInfo(invocation, ct).Symbol
            is not IMethodSymbol methodSymbol)
            continue;

        if (methodSymbol.ContainingType.Name != "Step")
            continue;

        var stepKind = methodSymbol.Name; // Predict, Retrieve, etc.
        var nameArg = GetNamedArgument(invocation, "name");
        var sigRef = methodSymbol.IsGenericMethod
            ? methodSymbol.TypeArguments[0].Name.ToLowerInvariant()
            : null;

        if (nameArg is not null)
            steps.Add(new StepModel(nameArg, stepKind, sigRef));
    }

    // Collect graph edges from .Then() chain
    foreach (var invocation in methodSyntax.DescendantNodes()
        .OfType<InvocationExpressionSyntax>())
    {
        ct.ThrowIfCancellationRequested();
        // Parse Graph.StartWith().Then() chains into ordered edges
        // (implementation depends on how the Graph builder API is shaped)
    }

    return new ProgramDescriptorModel(
        Namespace: typeSymbol.ContainingNamespace.ToDisplayString(),
        TypeName: typeSymbol.Name,
        ProgramId: programId,
        InputType: inputType,
        OutputType: outputType,
        Steps: new EquatableArray<StepModel>(steps.ToImmutableArray()),
        Edges: new EquatableArray<EdgeModel>(edges.ToImmutableArray()));
}
```

> **Junior Dev Note:** Extracting step metadata from method bodies is harder than reading attributes. The semantic model gives you `IMethodSymbol` for each call site. Focus on the `Step.*` factory methods and their `name:` arguments first — graph edge extraction can be iterated on.

---

## 4A. Binding Generator

The Binding Generator resolves how data flows between steps at compile time. It implements the three-tier binding model, emitting zero-overhead generated code for Tiers 1–3 and leaving Tier 4 (expression trees) as a runtime-only fallback.

### 4A.1 Three-Tier Binding Model

| Tier | Mechanism | Generator Responsibility |
|------|-----------|------------------------|
| **Tier 1** — Convention-based auto-binding | Matching property names and types between upstream outputs and downstream inputs | Generator scans upstream step output types and downstream input types, emitting direct property assignments for unambiguous name+type matches |
| **Tier 2** — `[BindFrom]` attribute | Developer applies `[BindFrom("stepName.Property")]` to an input property | Generator reads the attribute, validates the source path exists, and emits a direct property assignment |
| **Tier 3** — C# 14 interceptor-based lambda binding | Developer writes a `bind:` lambda on `Step.Predict<T>()` | Generator uses C# 14 interceptors (stable in .NET 10) to intercept the lambda call site, analyze the expression, and emit a generated binding method that replaces the lambda at compile time |
| **Tier 4** — Expression tree fallback | Developer writes a `bind:` lambda that cannot be statically analyzed | No generator action — the runtime calls `.Compile()` on the expression tree at execution time |

> **Note:** C# 14 interceptors are **stable** in .NET 10 — they are no longer experimental. The `[InterceptsLocation]` attribute is part of the supported language surface.

### 4A.2 Convention-Based Auto-Binding (Tier 1)

The generator walks the program graph and, for each `Step.Predict<TSig>` step, compares the input properties of `TSig` against available upstream outputs:

```csharp
// Generator logic (simplified)
foreach (var inputProp in signatureInputProperties)
{
    // Look for an exact name+type match in any upstream step's output
    var match = upstreamOutputs.FirstOrDefault(o =>
        o.Name == inputProp.Name &&
        o.Type.Equals(inputProp.Type, SymbolEqualityComparer.Default));

    if (match is not null)
    {
        // Emit: target.Property = ctx.OutputOf<UpstreamType>("stepId").Property;
        EmitDirectAssignment(spc, inputProp, match);
    }
}
```

**Generated output example:**

```csharp
// <auto-generated /> — Convention binding for step "triage"
[GeneratedCode("LMP.Generators", "1.0.0")]
file static class TriageStepBindings
{
    public static TriageTicket Bind(TicketInput input, IExecutionContext ctx)
    {
        return new TriageTicket
        {
            // Tier 1: convention-matched by name "TicketText" (string → string)
            TicketText = input.TicketText,
        };
    }
}
```

### 4A.3 Attribute-Based Binding (Tier 2)

When `[BindFrom]` is present, the generator reads the source expression and emits a direct assignment:

```csharp
// Generator reads [BindFrom("retrieve-kb.Documents")] on KnowledgeSnippets
// and emits:
KnowledgeSnippets = ctx.OutputOf<RetrieveResult>("retrieve-kb").Documents,
```

The generator validates at compile time that:
1. The referenced step name exists in the program graph.
2. The referenced property exists on the step's output type.
3. The types are assignment-compatible.

If validation fails, the generator reports a diagnostic and falls through to Tier 4.

### 4A.4 Interceptor-Based Lambda Binding (Tier 3)

For `bind:` lambdas on `Step.Predict<T>()` calls, the generator uses C# 14 interceptors to replace the lambda at the call site with a generated method:

```csharp
// User writes:
var triage = Step.Predict<TriageTicket>(
    name: "triage",
    bind: (input, ctx) => new TriageTicket
    {
        TicketText = input.TicketText,
        KnowledgeSnippets = ctx.OutputOf(retrieveKb).Documents
    });

// Generator emits an interceptor that replaces the bind: lambda:
[InterceptsLocation("SupportTriageProgram.cs", line: 14, column: 5)]
[GeneratedCode("LMP.Generators", "1.0.0")]
file static class TriageBindInterceptor
{
    public static Step Predict<T>(
        this StepFactory factory, string name,
        Func<TicketInput, IExecutionContext, TriageTicket> bind)
    {
        return factory.PredictWithBinding<T>(name,
            static (input, ctx) => new TriageTicket
            {
                TicketText = ((TicketInput)input).TicketText,
                KnowledgeSnippets = ctx.OutputOf<RetrieveResult>("retrieve-kb").Documents,
            });
    }
}
```

The interceptor eliminates the expression-tree overhead entirely — the `bind:` lambda is replaced with a direct method call that the JIT can inline. If the lambda body is too complex for static analysis (e.g., contains closures over local state, conditional logic, or method calls the generator cannot resolve), the generator does **not** emit an interceptor and the lambda falls through to Tier 4 (runtime `.Compile()`).

> **Junior Dev Note:** Interceptors work by matching a specific source location (file, line, column). The generator must compute the exact `InterceptsLocation` from the syntax tree. Use `invocation.GetLocation().GetLineSpan()` to get the line and column.

---

## 5. Generated Code Patterns

All generated code must follow these conventions:

| Rule | Rationale |
|---|---|
| Use `file class` for all internal helper types | Prevents namespace pollution; follows System.Text.Json precedent |
| Use `[GeneratedCode("LMP.Generators", "1.0.0")]` on every generated type | Enables code-coverage and analysis tool exclusion |
| Naming: `<TypeName>.g.cs` | Standard .NET generator naming convention |
| Namespace: same as the user's type | Generated partial classes must share the namespace |
| Use raw string literals `"""..."""` for prompt templates | Avoids escaping issues in multi-line instructions text |
| Include `// <auto-generated />` header | Tells IDEs/tools to suppress formatting and analysis |
| Use `ImmutableArray` for collections | Enforces immutability; prevents accidental mutation |
| Use `params ReadOnlySpan<T>` for variadic generated APIs | Zero-allocation variadic calls; avoids hidden `params T[]` heap allocations |
| Use `static partial` properties for descriptors | C# 14 partial properties; more idiomatic than `static abstract` interface methods |

---

## 6. Diagnostics Integration

### How Generators and Analyzers Share Information

Generators and analyzers both run inside Roslyn but are independent components. They share information through the **same semantic model** — they read the same attributed types and same syntax trees. They do NOT pass data to each other directly.

The division of labor is:

- **Analyzers** validate and report diagnostics (squiggly lines, warnings, errors).
- **Generators** produce code. If a generator encounters a type it cannot safely lower, it reports a diagnostic AND skips generation for that type (emit nothing rather than broken code).

### Diagnostic Definitions

The framework defines seven diagnostics. Each is a `DiagnosticDescriptor` instance:

```csharp
using Microsoft.CodeAnalysis;

namespace LMP.Analyzers;

public static class LmpDiagnostics
{
    private const string Category = "LMP.Authoring";

    public static readonly DiagnosticDescriptor LMP001_MissingFieldDescription = new(
        id: "LMP001",
        title: "Missing field description",
        messageFormat: "Property '{0}' on signature '{1}' is missing a Description in its [Input]/[Output] attribute",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every input and output field should have a Description so the LM knows what the field represents.");

    public static readonly DiagnosticDescriptor LMP002_MissingInstructions = new(
        id: "LMP002",
        title: "Missing or empty signature instructions",
        messageFormat: "Signature '{0}' has missing or empty Instructions in [LmpSignature]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Signatures without instructions produce poor LM outputs. Provide clear task instructions.");

    public static readonly DiagnosticDescriptor LMP003_DuplicateStepName = new(
        id: "LMP003",
        title: "Duplicate step name in program",
        messageFormat: "Step name '{0}' is used more than once in program '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Step names must be unique within a program. Traces, tunables, and the optimizer key on step name.");

    public static readonly DiagnosticDescriptor LMP004_NonDeterministicStepName = new(
        id: "LMP004",
        title: "Non-deterministic step name",
        messageFormat: "Step name for '{0}' in program '{1}' must be a compile-time constant",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Step names must be deterministic compile-time constants. "
                   + "Compiled variants, traces, and optimizer metadata require stable step identities.");

    public static readonly DiagnosticDescriptor LMP005_UnsupportedOutputType = new(
        id: "LMP005",
        title: "Unsupported output field type",
        messageFormat: "Output property '{0}' on signature '{1}' uses unsupported type '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Output field types must be types the framework can parse from structured LM output (string, bool, int, double, enum, IReadOnlyList<T>).");

    public static readonly DiagnosticDescriptor LMP006_InvalidGraphCycle = new(
        id: "LMP006",
        title: "Invalid graph cycle or self-reference",
        messageFormat: "Program '{0}' contains a cycle or unsupported self-reference involving step '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Program graphs must be acyclic in MVP. Cycles prevent deterministic execution ordering.");

    public static readonly DiagnosticDescriptor LMP007_ExpressionTreeBindingHint = new(
        id: "LMP007",
        title: "Expression tree used where attribute binding would suffice",
        messageFormat: "Binding for property '{0}' on step '{1}' uses a runtime expression tree; consider using [BindFrom(\"{2}\")] for zero-overhead compile-time binding",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Expression-tree bindings (Tier 4) incur runtime .Compile() cost. "
                   + "If the binding is a simple property mapping, use [BindFrom] (Tier 2) or let convention binding (Tier 1) handle it.");
}
```

### Reporting Diagnostics from a Generator

When the generator cannot safely lower a type, it reports a diagnostic on the `SourceProductionContext`:

```csharp
context.RegisterSourceOutput(signatures, static (spc, model) =>
{
    if (string.IsNullOrWhiteSpace(model.Instructions))
    {
        spc.ReportDiagnostic(Diagnostic.Create(
            LmpDiagnostics.LMP002_MissingInstructions,
            model.Location,
            model.TypeName));
        // Still emit the descriptor — instructions can be empty at runtime,
        // but the warning tells the developer to fix it.
    }

    Emitter.EmitSignature(spc, model);
});
```

> **Junior Dev Note:** Generators can report diagnostics but cannot prevent compilation. Use `DiagnosticSeverity.Error` for analyzers (which block the build), and `DiagnosticSeverity.Warning` for generator-side advisories. The analyzer project (`DiagnosticAnalyzer` subclass) is the proper home for build-blocking errors.

---

## 7. Testing Source Generators

### Golden File / Snapshot Testing

Generated output must be **snapshot-tested**: you provide a known input, run the generator, and compare the output to a saved "golden" file. If the output changes, the test fails, forcing a deliberate review of the change.

### Complete Test Example

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.Generators.Tests;

public class SignatureGeneratorTests
{
    [Fact]
    public void Generates_Descriptor_For_Simple_Signature()
    {
        // Arrange — the user's source code
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature(Instructions = "Classify the ticket severity.")]
            public partial class SimpleTicket
            {
                [Input(Description = "The raw ticket text")]
                public required string TicketText { get; init; }

                [Output(Description = "Low, Medium, High, Critical")]
                public required string Severity { get; init; }
            }
            """;

        // Arrange — build a compilation that includes framework types
        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(LmpSignatureAttribute).Assembly.Location),
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Act — run the generator
        var generator = new LmpSignatureGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        // Assert — no diagnostics
        Assert.Empty(diagnostics);

        // Assert — the generated source matches the golden snapshot
        var runResult = driver.GetRunResult();
        var generatedSource = runResult.GeneratedTrees.Single();
        var actualText = generatedSource.GetText().ToString();

        // Compare against golden file
        var goldenPath = Path.Combine("Snapshots", "SimpleTicket.g.verified.cs");
        var expectedText = File.ReadAllText(goldenPath);
        Assert.Equal(expectedText, actualText);

        // Assert — output compilation has no errors
        var outputDiagnostics = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(outputDiagnostics);
    }
}
```

> **Junior Dev Note:** The `Snapshots/` folder holds `.verified.cs` files that are checked into source control. When you intentionally change the generator's output, update the golden file. Consider using the [Verify](https://github.com/VerifyTests/Verify) library to automate snapshot management — it can auto-accept changes during development and diff them in CI.

---

## 8. Common Pitfalls

### Debugging Generators

Source generators run inside the compiler process. You cannot set breakpoints the normal way. Two approaches:

**Approach 1: `Debugger.Launch()`**

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
#if DEBUG
    if (!System.Diagnostics.Debugger.IsAttached)
        System.Diagnostics.Debugger.Launch();
#endif
    // ... rest of initialization
}
```

This pops a JIT debugger dialog when the build runs. Attach Visual Studio and step through.

**Approach 2: Diagnostic dump mode**

Write intermediate state to a file for inspection without a debugger:

```csharp
// Emit a diagnostic with the model's contents for troubleshooting
spc.ReportDiagnostic(Diagnostic.Create(
    new DiagnosticDescriptor("LMPDBG", "Debug", "Model: {0}",
        "Debug", DiagnosticSeverity.Warning, true),
    Location.None,
    model.ToString()));
```

### Incremental Cache Invalidation

The #1 bug: your model type does not implement equality correctly, so the generator re-runs on every keystroke. Symptoms:

- IDE becomes sluggish when editing files that have nothing to do with your attributed types.
- Build times increase.

Fix: ensure every field in your model is a value type, `string`, or `EquatableArray<T>`. Never store `ISymbol`, `SyntaxNode`, or `Location` in the model — extract the data you need as strings/primitives.

### Handling Malformed User Code

The generator will encounter partially written code (the user is mid-keystroke). Defensive rules:

1. **Return `null` from the transform** if any required data is missing. The `.Where(m => m is not null)` filter removes it.
2. **Never throw exceptions** — an unhandled exception in a generator silently kills all generation for the compilation.
3. **Report diagnostics** for structurally invalid but syntactically complete code (e.g., a signature with no `[Input]` or `[Output]` fields).

---

## 9. Convention-Based Program Discovery (Post-MVP)

### Problem

MVP requires explicit DI registration for each LMP program:

```csharp
builder.Services.AddLmpProgram<SupportTriageProgram>();
builder.Services.AddLmpProgram<ContentModerationProgram>();
builder.Services.AddLmpProgram<TranslationProgram>();
```

This is tedious for large projects with many programs. Minimal APIs solved this exact problem — endpoints are discovered by convention.

### Solution: Auto-Discovery Source Generator

A post-MVP source generator discovers **all** types implementing `ILmProgram<TIn, TOut>` in the compilation and emits a single extension method:

```csharp
// Generated by LMP source generator
public static class LmpServiceCollectionExtensions
{
    /// <summary>
    /// Registers all LMP programs discovered in this assembly.
    /// Generated automatically by the LMP source generator.
    /// </summary>
    public static IServiceCollection AddLmpPrograms(
        this IServiceCollection services)
    {
        services.AddLmpProgram<SupportTriageProgram>();
        services.AddLmpProgram<ContentModerationProgram>();
        services.AddLmpProgram<TranslationProgram>();
        return services;
    }
}
```

### Consumer Experience

```csharp
// Before (MVP — explicit)
builder.Services.AddLmpProgram<SupportTriageProgram>();
builder.Services.AddLmpProgram<ContentModerationProgram>();

// After (Post-MVP — convention)
builder.Services.AddLmpPrograms(); // Discovers all ILmProgram<,> in this assembly
```

### Implementation

The generator scans for `ILmProgram<TIn, TOut>` implementations using the same `IncrementalGeneratorInitializationContext` pipeline as the existing signature generator:

```csharp
var programs = context.SyntaxProvider.ForAttributeWithMetadataName(
    "LMP.LmpProgramAttribute",
    predicate: static (node, _) => node is ClassDeclarationSyntax,
    transform: static (ctx, _) =>
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        return new ProgramRegistrationModel(
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ProgramName: symbol.Name);
    });

context.RegisterSourceOutput(programs.Collect(), static (spc, models) =>
{
    var registrations = string.Join("\n        ",
        models.Select(m =>
            $"services.AddLmpProgram<{m.FullyQualifiedName}>();"));

    spc.AddSource("LmpServiceCollectionExtensions.g.cs", $$"""
        public static class LmpServiceCollectionExtensions
        {
            public static IServiceCollection AddLmpPrograms(
                this IServiceCollection services)
            {
                {{registrations}}
                return services;
            }
        }
        """);
});
```

This follows the same pattern used by `Microsoft.Extensions.DependencyInjection` source generators for auto-registration.

```csharp
// Safe: returns null if the attribute is malformed
var instructions = attr?.NamedArguments
    .FirstOrDefault(kvp => kvp.Key == "Instructions")
    .Value.Value as string;

if (instructions is null)
    return null; // Skip this type — user is still typing
```

### Cross-Type Dependencies

A `[LmpProgram]` references `[LmpSignature]` types via `Step.Predict<TriageTicket>`. The Program Generator needs the signature's normalized ID (`"triageticket"`) to populate `StepDescriptor.SignatureRef`.

Since generators cannot depend on each other's outputs, the Program Generator must independently resolve the signature type from the semantic model:

```csharp
// Inside Program Generator — resolve signature reference
if (methodSymbol.IsGenericMethod)
{
    var sigType = methodSymbol.TypeArguments[0];
    // Check that sigType has [LmpSignature]
    var hasAttr = sigType.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "LMP.LmpSignatureAttribute");
    if (hasAttr)
        sigRef = sigType.Name.ToLowerInvariant();
}
```

This duplicates a small amount of discovery logic, but it is unavoidable — generators are isolated by design.

> **Junior Dev Note:** If you find yourself needing data that "only the other generator knows," step back. Both generators read the same Roslyn semantic model. Extract what you need directly from the symbol/attribute — don't try to chain generators.
