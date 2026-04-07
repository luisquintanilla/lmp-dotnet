# Diagnostics and Code Fixes Specification

> Source of truth: `spec.org` §5A (Authoring-Time Architecture) and §13 (Diagnostics and Code Fixes).

> **Note:** The `helpLinkUri` values in diagnostic descriptors (e.g., `https://lmp.dev/diagnostics/LMP001`) point to the project's documentation site. These URLs should be updated to the actual documentation host before release (e.g., the repository's GitHub Pages or wiki).

---

## 1. Why Diagnostics Are a Product Feature

Diagnostics in the LMP framework are **not** afterthought error reporting. They are a core part of the product value proposition — the primary mechanism through which the framework *teaches* developers how to author correct LM programs.

**IDE integration.** Every diagnostic appears as a red or yellow squiggle in Visual Studio and VS Code the moment the developer types invalid code. Code fixes appear as lightbulb actions. This means a developer who has never read the docs can still discover the correct authoring shape through the IDE alone.

**Precedent.** Shipping Roslyn analyzers alongside a framework is established .NET practice:

| Framework | Analyzer example |
|---|---|
| EF Core | `EF1001` — possible SQL injection via interpolated string |
| System.Text.Json | `SYSLIB0020` — `JsonSerializerOptions` instance reuse warning |
| Microsoft.Extensions.Logging | `SYSLIB1006` — invalid logging method signature |

LMP follows this pattern. The `LMP.Roslyn` project ships analyzers and code-fix providers in the same NuGet package consumed at build time.

---

## 2. Diagnostic Reference

All diagnostics live in the `LMP.Roslyn.Analyzers` namespace. Categories:

| Category | Meaning |
|---|---|
| `LMP.Authoring` | Problems in signature or program source code the developer wrote |
| `LMP.Structure` | Structural violations in program graphs |

---

### LMP001 — Missing Field Description

| Property | Value |
|---|---|
| **ID** | `LMP001` |
| **Title** | Missing field description |
| **Severity** | Warning |
| **Category** | `LMP.Authoring` |

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor MissingFieldDescription = new(
    id: "LMP001",
    title: "Missing field description",
    messageFormat: "Field '{0}' on signature '{1}' is missing a Description",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP001");
```

#### What Triggers It

Any property decorated with `[Input]` or `[Output]` on an `[LmpSignature]` type that does not supply a `Description`.

```csharp
// BAD — no Description on the Output attribute
[LmpSignature(Instructions = "Classify severity.")]
public partial class TriageTicket
{
    [Input(Description = "Raw ticket text")]
    public required string TicketText { get; init; }

    [Output]  // ⚠ LMP001
    public required string Severity { get; init; }
}
```

**Message:** `Field 'Severity' on signature 'TriageTicket' is missing a Description`

#### How to Fix

```csharp
// GOOD
[Output(Description = "Severity: Low, Medium, High, Critical")]
public required string Severity { get; init; }
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingFieldDescriptionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.MissingFieldDescription);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext ctx)
    {
        var property = (IPropertySymbol)ctx.Symbol;
        var containingType = property.ContainingType;

        if (!containingType.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "LmpSignatureAttribute"))
            return;

        var fieldAttr = property.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "InputAttribute" or "OutputAttribute");
        if (fieldAttr is null)
            return;

        var descArg = fieldAttr.NamedArguments
            .FirstOrDefault(kvp => kvp.Key == "Description");

        if (descArg.Key is null ||
            descArg.Value.Value is not string s ||
            string.IsNullOrWhiteSpace(s))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors.MissingFieldDescription,
                property.Locations[0],
                property.Name,
                containingType.Name));
        }
    }
}
```

#### Unit Test

```csharp
using Verify =
    Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
        MissingFieldDescriptionAnalyzer,
        Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

[Fact]
public async Task OutputWithoutDescription_ReportsLMP001()
{
    const string source = """
        using LMP;

        [LmpSignature(Instructions = "Classify.")]
        public partial class Ticket
        {
            [Input(Description = "text")]
            public required string Text { get; init; }

            [Output]
            public required string {|#0:Severity|} { get; init; }
        }
        """;

    var expected = Verify.Diagnostic("LMP001")
        .WithLocation(0)
        .WithArguments("Severity", "Ticket");

    await Verify.VerifyAnalyzerAsync(source, expected);
}
```

---

### LMP002 — Missing or Empty Signature Instructions

| Property | Value |
|---|---|
| **ID** | `LMP002` |
| **Title** | Missing or empty signature instructions |
| **Severity** | Warning |
| **Category** | `LMP.Authoring` |

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor MissingSignatureInstructions = new(
    id: "LMP002",
    title: "Missing or empty signature instructions",
    messageFormat: "Signature '{0}' has missing or empty Instructions",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP002");
```

#### What Triggers It

An `[LmpSignature]` attribute whose `Instructions` is omitted, null, or whitespace-only.

```csharp
// BAD — empty Instructions
[LmpSignature(Instructions = "")]  // ⚠ LMP002
public partial class EmptyInstructions
{
    [Input(Description = "input")] public required string In { get; init; }
    [Output(Description = "output")] public required string Out { get; init; }
}
```

**Message:** `Signature 'EmptyInstructions' has missing or empty Instructions`

#### How to Fix

```csharp
// GOOD
[LmpSignature(Instructions = "You are a classification assistant. Classify the input.")]
public partial class EmptyInstructions { /* ... */ }
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingSignatureInstructionsAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.MissingSignatureInstructions);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;

        var sigAttr = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "LmpSignatureAttribute");
        if (sigAttr is null) return;

        var instructions = sigAttr.NamedArguments
            .FirstOrDefault(kvp => kvp.Key == "Instructions");

        if (instructions.Key is null ||
            instructions.Value.Value is not string s ||
            string.IsNullOrWhiteSpace(s))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors.MissingSignatureInstructions,
                sigAttr.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                    ?? type.Locations[0],
                type.Name));
        }
    }
}
```

---

### LMP003 — Duplicate Step Name in Program

| Property | Value |
|---|---|
| **ID** | `LMP003` |
| **Title** | Duplicate step name in program |
| **Severity** | Error |
| **Category** | `LMP.Structure` |

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor DuplicateStepName = new(
    id: "LMP003",
    title: "Duplicate step name in program",
    messageFormat: "Step name '{0}' is used more than once in program '{1}'",
    category: "LMP.Structure",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP003");
```

#### What Triggers It

Two or more `Step.*` calls inside a single `Build()` method share the same `name:` argument.

```csharp
// BAD — "triage" used twice
[LmpProgram("ticket-triage")]
public partial class TriageProgram : LmpProgram<TicketInput, TriageResult>
{
    public override ProgramGraph Build()
    {
        var a = Step.Predict<TriageTicket>(name: "triage", /* ... */);
        var b = Step.Predict<TriageTicket>(name: "triage", /* ... */);  // ❌ LMP003
        return Graph.StartWith(a).Then(b).Build();
    }
}
```

**Message:** `Step name 'triage' is used more than once in program 'TriageProgram'`

#### How to Fix

```csharp
// GOOD — unique step names
var a = Step.Predict<TriageTicket>(name: "triage-initial", /* ... */);
var b = Step.Predict<TriageTicket>(name: "triage-refined", /* ... */);
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateStepNameAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.DuplicateStepName);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBuildMethod,
            SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeBuildMethod(SyntaxNodeAnalysisContext ctx)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        if (method.Identifier.Text != "Build") return;

        var containingType = ctx.SemanticModel
            .GetDeclaredSymbol(method)?.ContainingType;
        if (containingType is null) return;

        bool isProgram = containingType.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "LmpProgramAttribute");
        if (!isProgram) return;

        // Collect all Step.* invocations and their name: arguments
        var invocations = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>();

        var nameArgs = new List<(string Name, Location Location)>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma)
                continue;
            if (ma.Expression is not IdentifierNameSyntax id ||
                id.Identifier.Text != "Step")
                continue;

            var nameArg = invocation.ArgumentList.Arguments
                .FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "name");
            if (nameArg?.Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is string nameValue)
            {
                nameArgs.Add((nameValue, literal.GetLocation()));
            }
        }

        var duplicates = nameArgs.GroupBy(n => n.Name)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            foreach (var (name, location) in group.Skip(1))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.DuplicateStepName,
                    location,
                    name,
                    containingType.Name));
            }
        }
    }
}
```

---

### LMP004 — Non-Deterministic Step Name

| Property | Value |
|---|---|
| **ID** | `LMP004` |
| **Title** | Non-deterministic step name |
| **Severity** | Error |
| **Category** | `LMP.Structure` |

Step names must be deterministic compile-time constants. Compiled variants, traces, and optimizer metadata require stable step identities (spec §9).

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor NonDeterministicStepName = new(
    id: "LMP004",
    title: "Non-deterministic step name",
    messageFormat: "Step name must be a compile-time constant; '{0}' is not deterministic",
    category: "LMP.Structure",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP004");
```

#### What Triggers It

A `name:` argument on a `Step.*` call that is not a string literal or `const` reference — for example, a method call or interpolated string.

```csharp
// BAD — Guid.NewGuid() is non-deterministic
var s = Step.Predict<TriageTicket>(
    name: Guid.NewGuid().ToString(),  // ❌ LMP004
    /* ... */);
```

**Message:** `Step name must be a compile-time constant; 'Guid.NewGuid().ToString()' is not deterministic`

#### How to Fix

```csharp
// GOOD — string literal constant
var s = Step.Predict<TriageTicket>(name: "triage", /* ... */);
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonDeterministicStepNameAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.NonDeterministicStepName);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation,
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return;
        if (ma.Expression is not IdentifierNameSyntax id ||
            id.Identifier.Text != "Step")
            return;

        var nameArg = invocation.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "name");
        if (nameArg is null) return;

        var expr = nameArg.Expression;

        // Allow string literals
        if (expr is LiteralExpressionSyntax) return;

        // Allow const field / local references
        var symbol = ctx.SemanticModel.GetSymbolInfo(expr).Symbol;
        if (symbol is IFieldSymbol { IsConst: true }) return;
        if (symbol is ILocalSymbol { IsConst: true }) return;

        ctx.ReportDiagnostic(Diagnostic.Create(
            Descriptors.NonDeterministicStepName,
            expr.GetLocation(),
            expr.ToString()));
    }
}
```

---

### LMP005 — Unsupported Output Type

| Property | Value |
|---|---|
| **ID** | `LMP005` |
| **Title** | Unsupported output type |
| **Severity** | Error |
| **Category** | `LMP.Authoring` |

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor UnsupportedOutputType = new(
    id: "LMP005",
    title: "Unsupported output type",
    messageFormat: "Output field '{0}' uses unsupported type '{1}'; use a concrete type instead",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP005");
```

#### What Triggers It

An `[Output]` property whose type is `dynamic`, `object`, or another type the framework cannot structurally serialize.

```csharp
// BAD — object is not structurally serializable
[LmpSignature(Instructions = "Summarise.")]
public partial class Summary
{
    [Input(Description = "text")] public required string Text { get; init; }

    [Output(Description = "result")]
    public required object Result { get; init; }  // ❌ LMP005
}
```

**Message:** `Output field 'Result' uses unsupported type 'object'; use a concrete type instead`

#### How to Fix

```csharp
// GOOD — concrete type
[Output(Description = "result")]
public required string Result { get; init; }
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsupportedOutputTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly HashSet<string> Blocked = new()
    {
        "System.Object",
        "System.Dynamic.ExpandoObject",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.UnsupportedOutputType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext ctx)
    {
        var property = (IPropertySymbol)ctx.Symbol;

        bool hasOutput = property.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "OutputAttribute");
        if (!hasOutput) return;

        var typeName = property.Type.ToDisplayString();

        // Check for dynamic (appears as object with DynamicAttribute)
        bool isDynamic = property.Type.TypeKind == TypeKind.Dynamic;

        if (isDynamic || Blocked.Contains(typeName))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnsupportedOutputType,
                property.Locations[0],
                property.Name,
                isDynamic ? "dynamic" : typeName));
        }
    }
}
```

---

### LMP006 — Invalid Graph Cycle or Unsupported Self-Reference

| Property | Value |
|---|---|
| **ID** | `LMP006` |
| **Title** | Invalid graph cycle |
| **Severity** | Error |
| **Category** | `LMP.Structure` |

The MVP program graph must be acyclic (spec §8). Cycles are detected when a step references itself or creates a circular dependency via `after:` / `ctx.OutputOf(...)`.

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor InvalidGraphCycle = new(
    id: "LMP006",
    title: "Invalid graph cycle",
    messageFormat: "Step '{0}' creates a cycle in program '{1}'; MVP graphs must be acyclic",
    category: "LMP.Structure",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP006");
```

#### What Triggers It

A step variable is passed to `ctx.OutputOf(...)` or `after:` on a step that is defined *before* or *at the same level* in a way that creates a back-edge. The simplest case is self-reference.

```csharp
// BAD — self-reference
var triage = Step.Predict<TriageTicket>(
    name: "triage",
    bind: (input, ctx) => new TriageTicket
    {
        Text = ctx.OutputOf(triage).DraftReply  // ❌ LMP006 — 'triage' references itself
    });
```

**Message:** `Step 'triage' creates a cycle in program 'TriageProgram'; MVP graphs must be acyclic`

#### How to Fix

Remove the self-reference. If you need iterative refinement, use the `Step.Repair` pattern:

```csharp
// GOOD — separate repair step
var triage = Step.Predict<TriageTicket>(name: "triage", /* ... */);
var repair = Step.Repair<TriageTicket>(
    name: "repair-triage",
    usingFeedbackFrom: new[] { groundedness });
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGraphCycleAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.InvalidGraphCycle);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBuildMethod,
            SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeBuildMethod(SyntaxNodeAnalysisContext ctx)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        if (method.Identifier.Text != "Build") return;

        var containingType = ctx.SemanticModel
            .GetDeclaredSymbol(method)?.ContainingType;
        if (containingType is null) return;
        if (!containingType.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "LmpProgramAttribute"))
            return;

        // Build a map: variable name → declaration statement
        var declarations = method.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is InvocationExpressionSyntax)
            .ToDictionary(v => v.Identifier.Text, v => v);

        // For each declaration, find OutputOf(...) or after: references
        foreach (var (varName, declarator) in declarations)
        {
            var initializer = declarator.Initializer!.Value;
            var outputOfCalls = initializer.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name.Identifier.Text == "OutputOf");

            foreach (var call in outputOfCalls)
            {
                var arg = call.ArgumentList.Arguments.FirstOrDefault();
                if (arg?.Expression is IdentifierNameSyntax refId &&
                    refId.Identifier.Text == varName)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.InvalidGraphCycle,
                        arg.GetLocation(),
                        varName,
                        containingType.Name));
                }
            }
        }
    }
}
```

---

### LMP007 — Expression Tree Used Where Attribute Would Suffice

| Property | Value |
|---|---|
| **ID** | `LMP007` |
| **Title** | Expression tree used where attribute binding would suffice |
| **Severity** | Info |
| **Category** | `LMP.Authoring` |

An expression-tree binding (Tier 4) is used for a simple property mapping that could be expressed with `[BindFrom]` (Tier 2) or resolved automatically by convention (Tier 1). Expression-tree bindings incur `.Compile()` cost at runtime, while attribute and convention bindings are generated as zero-overhead code at build time.

#### Descriptor

```csharp
public static readonly DiagnosticDescriptor ExpressionTreeBindingHint = new(
    id: "LMP007",
    title: "Expression tree used where attribute binding would suffice",
    messageFormat: "Binding for property '{0}' on step '{1}' uses a runtime expression tree; consider [BindFrom(\"{2}\")]",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true,
    helpLinkUri: "https://lmp.dev/diagnostics/LMP007");
```

#### What Triggers It

A `bind:` lambda on a `Step.Predict<T>()` call contains a simple property mapping that the generator can statically analyze but did not intercept (Tier 3), and that maps to a single upstream output property — meaning it could be replaced with a `[BindFrom]` attribute or resolved by convention.

```csharp
// INFO — LMP007: simple mapping could use [BindFrom("retrieve-kb.Documents")]
var triage = Step.Predict<TriageTicket>(
    name: "triage",
    bind: (input, ctx) => new TriageTicket
    {
        KnowledgeSnippets = ctx.OutputOf(retrieveKb).Documents  // ℹ️ LMP007
    });
```

**Message:** `Binding for property 'KnowledgeSnippets' on step 'triage' uses a runtime expression tree; consider [BindFrom("retrieve-kb.Documents")]`

#### How to Fix

Apply `[BindFrom]` to the property on the signature, or rename the property to match the upstream output for convention binding:

```csharp
// Option 1: Use [BindFrom] (Tier 2)
[Input(Description = "Relevant knowledge base snippets")]
[BindFrom("retrieve-kb.Documents")]
public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

// Option 2: Rename to match upstream output (Tier 1 convention)
[Input(Description = "Relevant knowledge base snippets")]
public required IReadOnlyList<string> Documents { get; init; }
```

#### Analyzer Implementation

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExpressionTreeBindingHintAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.ExpressionTreeBindingHint);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBindLambda,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void AnalyzeBindLambda(SyntaxNodeAnalysisContext ctx)
    {
        // Check if this lambda is the `bind:` argument of a Step.Predict<T>() call.
        // If so, analyze the lambda body for simple property mappings.
        // For each simple mapping, check if a [BindFrom] or convention match exists.
        // If the mapping is trivially replaceable, report LMP007.
    }
}
```

---

## 3. Code Fix Reference

### Fix for LMP001 — Add Missing Description

**Lightbulb text:** `Add Description to field`

```csharp
[ExportCodeFixProvider(LanguageNames.CSharp)]
public sealed class AddFieldDescriptionCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create("LMP001");

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);
        if (node is not PropertyDeclarationSyntax property) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add Description to field",
                createChangedDocument: ct =>
                    AddDescription(context.Document, property, ct),
                equivalenceKey: "LMP001_AddDescription"),
            diagnostic);
    }

    private static async Task<Document> AddDescription(
        Document document, PropertyDeclarationSyntax property, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null) return document;

        // Find [Input] or [Output] attribute
        var attrList = property.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Input" or "Output"
                or "InputAttribute" or "OutputAttribute");
        if (attrList is null) return document;

        // Build new attribute argument: Description = "TODO: describe this field"
        // Note: The "TODO:" prefix is intentional — the code fix inserts a placeholder
        // that the developer must replace with a real description. This follows the
        // same pattern as Visual Studio's "throw new NotImplementedException()" stub.
        var descArg = SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals("Description"),
            null,
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal("TODO: describe this field")));

        var newArgs = attrList.ArgumentList is null
            ? SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(descArg))
            : attrList.ArgumentList.AddArguments(descArg);

        var newAttr = attrList.WithArgumentList(newArgs);
        var newRoot = root.ReplaceNode(attrList, newAttr);
        return document.WithSyntaxRoot(newRoot);
    }
}
```

**Before:**
```csharp
[Output]
public required string Severity { get; init; }
```

**After:**
```csharp
[Output(Description = "TODO: describe this field")]
public required string Severity { get; init; }
```

---

### Fix for LMP002 — Add Placeholder Instructions

**Lightbulb text:** `Add Instructions to signature`

```csharp
[ExportCodeFixProvider(LanguageNames.CSharp)]
public sealed class AddSignatureInstructionsCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create("LMP002");

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);

        // Walk up to find the attribute syntax
        var attr = node?.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attr is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add Instructions to signature",
                createChangedDocument: ct =>
                    AddInstructions(context.Document, attr, ct),
                equivalenceKey: "LMP002_AddInstructions"),
            diagnostic);
    }

    private static async Task<Document> AddInstructions(
        Document document, AttributeSyntax attr, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null) return document;

        var instrArg = SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals("Instructions"),
            null,
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal("TODO: describe what this signature does")));

        var newArgs = attr.ArgumentList is null
            ? SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(instrArg))
            : attr.ArgumentList.AddArguments(instrArg);

        var newAttr = attr.WithArgumentList(newArgs);
        var newRoot = root.ReplaceNode(attr, newAttr);
        return document.WithSyntaxRoot(newRoot);
    }
}
```

---

### Fix for LMP004 — Extract Constant Step Name

**Lightbulb text:** `Extract step name to const`

```csharp
[ExportCodeFixProvider(LanguageNames.CSharp)]
public sealed class ExtractConstStepNameCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create("LMP004");

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);
        if (node is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Extract step name to const",
                createChangedDocument: ct =>
                    ExtractConst(context.Document, node, ct),
                equivalenceKey: "LMP004_ExtractConst"),
            diagnostic);
    }

    private static async Task<Document> ExtractConst(
        Document document, SyntaxNode expr, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null) return document;

        // Replace the expression with a const reference named StepName
        var constField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(
                    SyntaxFactory.Token(SyntaxKind.StringKeyword)))
            .AddVariables(
                SyntaxFactory.VariableDeclarator("StepName")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal("TODO-step-name"))))))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ConstKeyword));

        var classDecl = expr.Ancestors().OfType<ClassDeclarationSyntax>().First();
        var newClass = classDecl.AddMembers(constField);

        var newExpr = SyntaxFactory.IdentifierName("StepName");
        newClass = newClass.ReplaceNode(
            newClass.DescendantNodes().First(n => n.IsEquivalentTo(expr)),
            newExpr);

        var newRoot = root.ReplaceNode(classDecl, newClass);
        return document.WithSyntaxRoot(newRoot);
    }
}
```

---

## 4. Analyzer Architecture

### Analyzers and Source Generators: Shared Information

Analyzers and source generators both consume the Roslyn `Compilation` and `SemanticModel`, but they serve different purposes and do **not** share mutable state:

```
C# Source → Roslyn Compilation
                ├── Analyzers  → Diagnostics (IDE squiggles, build warnings)
                └── Source Generators → Generated .g.cs files
```

Both discover the same `[LmpSignature]` and `[LmpProgram]` types via `INamedTypeSymbol` attributes. The source generator uses what the analyzer validates. This means:
- The analyzer should report problems *before* the generator runs.
- The generator can assume validated shapes when emitting code.

### DiagnosticAnalyzer Lifecycle

1. Roslyn calls `Initialize` once. Register callbacks here — never store mutable state on the analyzer instance.
2. `context.RegisterSymbolAction` fires for each matching symbol (property, type, method).
3. `context.RegisterSyntaxNodeAction` fires for each matching syntax node kind.
4. Analysis callbacks run concurrently. Use only the provided `context` parameter.

### SyntaxNode Analysis vs SemanticModel Analysis

| Approach | When to use | Examples |
|---|---|---|
| `RegisterSyntaxNodeAction` | Pattern is purely syntactic — string literals, method structure, naming | LMP003 (duplicate names), LMP004 (non-const expression), LMP006 (self-ref) |
| `RegisterSymbolAction` | You need type information, attribute metadata, inheritance chain | LMP001 (field description), LMP002 (instructions), LMP005 (type check) |

**Rule of thumb:** If you need `.GetAttributes()` or `.Type`, use symbol analysis. If you need to walk `DescendantNodes()` inside a method body, use syntax analysis.

---

## 5. Testing Diagnostics

### Required Packages

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing" Version="1.1.2" />
<PackageReference Include="Microsoft.CodeAnalysis.CSharp.CodeFix.Testing" Version="1.1.2" />
<PackageReference Include="Microsoft.CodeAnalysis.Testing.Verifiers.Xunit" Version="1.1.2" />
```

### Complete Analyzer Test — LMP001

```csharp
using Microsoft.CodeAnalysis.Testing;
using AnalyzerVerifier =
    Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
        LMP.Roslyn.Analyzers.MissingFieldDescriptionAnalyzer,
        DefaultVerifier>;

public class MissingFieldDescriptionTests
{
    [Fact]
    public async Task OutputField_WithDescription_NoDiagnostic()
    {
        const string source = """
            using LMP;

            [LmpSignature(Instructions = "Classify.")]
            public partial class Ticket
            {
                [Input(Description = "text")]
                public required string Text { get; init; }

                [Output(Description = "severity label")]
                public required string Severity { get; init; }
            }
            """;

        await AnalyzerVerifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task OutputField_NoDescription_ReportsLMP001()
    {
        const string source = """
            using LMP;

            [LmpSignature(Instructions = "Classify.")]
            public partial class Ticket
            {
                [Input(Description = "text")]
                public required string Text { get; init; }

                [Output]
                public required string {|#0:Severity|} { get; init; }
            }
            """;

        await AnalyzerVerifier.VerifyAnalyzerAsync(source,
            AnalyzerVerifier.Diagnostic("LMP001")
                .WithLocation(0)
                .WithArguments("Severity", "Ticket"));
    }
}
```

### Complete Code Fix Test — LMP001

```csharp
using CodeFixVerifier =
    Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
        LMP.Roslyn.Analyzers.MissingFieldDescriptionAnalyzer,
        LMP.Roslyn.CodeFixes.AddFieldDescriptionCodeFix,
        DefaultVerifier>;

public class AddFieldDescriptionCodeFixTests
{
    [Fact]
    public async Task AddsDescriptionPlaceholder()
    {
        const string before = """
            using LMP;

            [LmpSignature(Instructions = "Classify.")]
            public partial class Ticket
            {
                [Output]
                public required string {|#0:Severity|} { get; init; }
            }
            """;

        const string after = """
            using LMP;

            [LmpSignature(Instructions = "Classify.")]
            public partial class Ticket
            {
                [Output(Description = "TODO: describe this field")]
                public required string Severity { get; init; }
            }
            """;

        await CodeFixVerifier.VerifyCodeFixAsync(before, 
            CodeFixVerifier.Diagnostic("LMP001")
                .WithLocation(0)
                .WithArguments("Severity", "Ticket"),
            after);
    }
}
```

---

## 6. Adding New Diagnostics (Guide for Contributors)

Follow these four steps for every new diagnostic:

### Step 1 — Create the Descriptor

Add a static field to `Descriptors.cs`:

```csharp
// File: src/LMP.Roslyn/Descriptors.cs
public static class Descriptors
{
    public static readonly DiagnosticDescriptor NewRule = new(
        id: "LMP0XX",
        title: "Short title",
        messageFormat: "Explain '{0}' problem clearly",
        category: "LMP.Authoring",          // or "LMP.Structure"
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://lmp.dev/diagnostics/LMP0XX");
}
```

### Step 2 — Implement the Analyzer

Create a class in `src/LMP.Roslyn/Analyzers/`:

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NewRuleAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.NewRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        // Choose RegisterSymbolAction or RegisterSyntaxNodeAction
        context.RegisterSymbolAction(Analyze, SymbolKind.Property);
    }

    private static void Analyze(SymbolAnalysisContext ctx) { /* ... */ }
}
```

### Step 3 — Add Tests

Create a test class in `tests/LMP.Roslyn.Tests/Analyzers/`:

```csharp
public class NewRuleAnalyzerTests
{
    [Fact]
    public async Task ValidCode_NoDiagnostic() { /* ... */ }

    [Fact]
    public async Task InvalidCode_ReportsNewRule() { /* ... */ }
}
```

Use the `{|#0:token|}` markup syntax to mark expected diagnostic locations.

### Step 4 — Register (Automatic)

Roslyn discovers analyzers automatically via the `[DiagnosticAnalyzer]` attribute when the assembly is referenced as an analyzer. No manual registration is needed. Ensure the project file includes:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.9.2" />
  </ItemGroup>
</Project>
```

Consumers reference it as:

```xml
<ItemGroup>
  <ProjectReference Include="..\LMP.Roslyn\LMP.Roslyn.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

---

*This spec is derived from `spec.org` §5A and §13. If any conflict arises, `spec.org` wins.*
