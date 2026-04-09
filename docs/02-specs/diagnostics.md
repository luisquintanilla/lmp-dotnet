# Diagnostics Specification

> Roslyn analyzers shipped in the `LMP.Analyzers` package.
> Scope: `[LmpSignature]` output-type validation only.

---

## Overview

LMP ships a small set of Roslyn analyzers that catch common mistakes at build time.
They run in Visual Studio, VS Code, and `dotnet build` — giving IDE squiggles and code fixes.

All diagnostics use category **`LMP.Authoring`** and live in the `LMP.Analyzers` namespace.

---

## Summary Table

| ID | Severity | Title | Fires when |
|---|---|---|---|
| LMP001 | Warning | Missing property description | Output type property has no `[Description]` attribute |
| LMP002 | Error | Non-serializable output type | Output type property cannot be serialized by `System.Text.Json` |
| LMP003 | Error | `[LmpSignature]` on non-partial record | Attribute applied to a type that is not a `partial record` |

---

## LMP001 — Missing Property Description

| Property | Value |
|---|---|
| **ID** | `LMP001` |
| **Severity** | Warning |
| **Category** | `LMP.Authoring` |
| **Message** | `Property '{0}' on output type '{1}' is missing a [Description] attribute` |

### When It Fires

Any public property on an `[LmpSignature]` output type that does not have a
`[System.ComponentModel.Description]` attribute.

Field descriptions are used by the source-generated `PromptBuilder` to construct
the LM prompt. Missing descriptions produce worse prompts.

```csharp
[LmpSignature("Classify a support ticket")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account")]
    public required string Category { get; init; }

    public required int Urgency { get; init; }  // ⚠ LMP001
}
```

### Code Fix

Add a `[Description]` attribute with a TODO placeholder:

```csharp
[Description("TODO: add description")]
public required int Urgency { get; init; }  // fixed
```

### Descriptor

```csharp
public static readonly DiagnosticDescriptor MissingDescription = new(
    id: "LMP001",
    title: "Missing property description",
    messageFormat: "Property '{0}' on output type '{1}' is missing a [Description] attribute",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp001");
```

---

## LMP002 — Non-Serializable Output Type

| Property | Value |
|---|---|
| **ID** | `LMP002` |
| **Severity** | Error |
| **Category** | `LMP.Authoring` |
| **Message** | `Property '{0}' on output type '{1}' is not serializable by System.Text.Json` |

### When It Fires

A property on an `[LmpSignature]` output type uses a type that `System.Text.Json`
cannot round-trip (no public parameterless constructor, no settable properties, or
an unsupported type like `Delegate`, `IntPtr`, `Span<T>`, etc.).

`GetResponseAsync<TOutput>()` deserializes the LM's JSON response into `TOutput`.
A non-serializable property will throw at runtime.

```csharp
[LmpSignature("Extract entities")]
public partial record Entities
{
    [Description("Matched entities")]
    public required Action<string> Callback { get; init; }  // ❌ LMP002
}
```

### Code Fix

Replace the property type with a serializable alternative. No automatic code fix —
the correct replacement depends on intent.

### Descriptor

```csharp
public static readonly DiagnosticDescriptor NonSerializableOutput = new(
    id: "LMP002",
    title: "Non-serializable output type",
    messageFormat: "Property '{0}' on output type '{1}' is not serializable by System.Text.Json",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp002");
```

---

## LMP003 — [LmpSignature] on Non-Partial Record

| Property | Value |
|---|---|
| **ID** | `LMP003` |
| **Severity** | Error |
| **Category** | `LMP.Authoring` |
| **Message** | `[LmpSignature] requires a partial record but '{0}' is {1}` |

### When It Fires

`[LmpSignature]` is applied to a type that is not declared as `partial record`.
The source generator needs the `partial` keyword to emit companion code
(`PromptBuilder`, `JsonTypeInfo`, predictor discovery).

```csharp
[LmpSignature("Summarize text")]        // ❌ LMP003 — not partial
public record Summary
{
    [Description("One-line summary")]
    public required string Text { get; init; }
}

[LmpSignature("Summarize text")]        // ❌ LMP003 — class, not record
public partial class Summary2
{
    [Description("One-line summary")]
    public required string Text { get; init; }
}
```

### Code Fix

Add `partial` and/or change to `record`:

```csharp
[LmpSignature("Summarize text")]
public partial record Summary           // fixed
{
    [Description("One-line summary")]
    public required string Text { get; init; }
}
```

### Descriptor

```csharp
public static readonly DiagnosticDescriptor NonPartialRecord = new(
    id: "LMP003",
    title: "[LmpSignature] on non-partial record",
    messageFormat: "[LmpSignature] requires a partial record but '{0}' is {1}",
    category: "LMP.Authoring",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://github.com/lmp-dotnet/lmp/blob/main/docs/diagnostics.md#lmp003");
```

---

## Suppression

All diagnostics can be suppressed per standard Roslyn mechanisms:

```csharp
#pragma warning disable LMP001
public required int Urgency { get; init; }
#pragma warning restore LMP001
```

Or via `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.LMP001.severity = none
```
