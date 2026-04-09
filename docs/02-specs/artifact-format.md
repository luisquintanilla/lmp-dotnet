# Artifact Format Specification

> **Status:** Normative — all implementations must conform.
>
> **Principle:** Match DSPy's `module.save()` / `module.load()` — flat JSON, nothing more.

---

## 1. What Is an Artifact

An artifact is a JSON file that captures the learned state of an optimized LM program: instructions, few-shot demos, and config per predictor. The optimizer fills these; `SaveAsync` writes them; `LoadAsync` restores them.

This mirrors DSPy exactly. `module.save("file.json")` dumps `{predictor_name: {demos, signature, lm_config}}` per predictor. LMP does the same thing with source-generated JSON serialization.

### Why Artifacts Matter

| Concern | How the artifact addresses it |
|---|---|
| **Reproducibility** | Pinning instructions, demos, and config per predictor means re-running produces the same behavior. |
| **Git-friendly** | Plain JSON — human-readable, easily diffable, works with any VCS. |
| **Deployment** | The artifact is the deployable unit. CI/CD promotes artifacts through staging → production. |
| **AOT-safe** | Source-generated `JsonSerializerContext` — no reflection, trimming-safe, Native AOT compatible. |

---

## 2. JSON Schema

A single flat JSON file. One entry per predictor in the module.

```json
{
  "version": "1.0",
  "module": "SupportTriage",
  "predictors": {
    "Classify": {
      "instructions": "Classify a support ticket by category and urgency",
      "demos": [
        {
          "input": { "ticketText": "I was charged twice for my subscription" },
          "output": { "category": "billing", "urgency": 3 }
        },
        {
          "input": { "ticketText": "Can't log in after password reset" },
          "output": { "category": "account", "urgency": 4 }
        }
      ],
      "config": { "temperature": 0.7, "model": "gpt-4o-mini" }
    },
    "DraftReply": {
      "instructions": "Draft a helpful reply for a classified support ticket",
      "demos": [
        {
          "input": { "category": "billing", "urgency": 3 },
          "output": { "reply": "I'm sorry about the double charge. I've initiated a refund..." }
        }
      ],
      "config": { "temperature": 0.9, "model": "gpt-4o-mini" }
    }
  }
}
```

### Field Reference

| Field | Type | Required | Purpose |
|---|---|---|---|
| `version` | `string` | ✅ | Schema version. `"1.0"` for this spec. |
| `module` | `string` | ✅ | Name of the `LmpModule` subclass (e.g., `"SupportTriage"`). |
| `predictors` | `object` | ✅ | Map of predictor name → predictor state. Keys match predictor field/property names in the module. |
| `predictors.*.instructions` | `string` | ✅ | The instruction text for this predictor (from `[LmpSignature]`, possibly rewritten by an optimizer). |
| `predictors.*.demos` | `array` | ✅ | Few-shot examples. Each entry has `input` and `output` objects whose keys match the predictor's `TInput` / `TOutput` property names. May be empty `[]`. |
| `predictors.*.config` | `object` | Optional | Predictor-level LM configuration (temperature, model, max tokens, etc.). Null or absent means "use defaults." |

---

## 3. C# Types

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// Serializable state of a saved LM program.
/// One entry per predictor — instructions, demos, and config.
/// </summary>
public sealed record ModuleState
{
    public required string Version { get; init; }
    public required string Module { get; init; }
    public required Dictionary<string, PredictorState> Predictors { get; init; }
}

public sealed record PredictorState
{
    public required string Instructions { get; init; }
    public required List<DemoEntry> Demos { get; init; }
    public Dictionary<string, JsonElement>? Config { get; init; }
}

public sealed record DemoEntry
{
    public required Dictionary<string, JsonElement> Input { get; init; }
    public required Dictionary<string, JsonElement> Output { get; init; }
}
```

### Source-Generated Serialization

All JSON goes through a source-generated `JsonSerializerContext` — zero reflection, AOT-safe, trimming-safe.

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModuleState))]
public partial class ModuleStateSerializerContext : JsonSerializerContext;
```

> **Rule:** Never call `JsonSerializer.Serialize<T>(value)` or `JsonSerializer.Deserialize<T>(json)` without a `JsonSerializerContext`. Always use `ModuleStateSerializerContext.Default.ModuleState`.

---

## 4. SaveAsync / LoadAsync

Methods live directly on `LmpModule`. No separate loader interface needed.

```csharp
public abstract class LmpModule
{
    // Source generator emits GetPredictors() — returns all Predictor fields.
    public abstract IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors();

    /// <summary>
    /// Serializes all predictor state (instructions, demos, config) to a JSON file.
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = GetType().Name,
            Predictors = GetPredictors().ToDictionary(
                p => p.Name,
                p => p.Predictor.GetState())
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            state,
            ModuleStateSerializerContext.Default.ModuleState);

        // Atomic write: temp file → rename.
        string tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads predictor state from a JSON file and populates demos/instructions/config.
    /// </summary>
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);

        var state = JsonSerializer.Deserialize(
            bytes,
            ModuleStateSerializerContext.Default.ModuleState)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize module state from {path}");

        foreach (var (name, predictor) in GetPredictors())
        {
            if (state.Predictors.TryGetValue(name, out var predictorState))
            {
                predictor.LoadState(predictorState);
            }
        }
    }
}
```

### IPredictor State Interface

Each `Predictor<TIn, TOut>` implements these to bridge between typed state and the serializable `PredictorState`:

```csharp
public interface IPredictor
{
    PredictorState GetState();
    void LoadState(PredictorState state);
}
```

The source generator emits typed serialization for `GetState()` / `LoadState()` so demos are round-tripped through the correct `TInput` / `TOutput` types.

---

## 5. Complete Example

### Optimize, Save, Load

```csharp
// 1. Define the module.
var module = new SupportTriageModule(chatClient);

// 2. Optimize — fills predictor demos/instructions.
var optimizer = new BootstrapFewShot(metric, maxDemos: 4);
var optimized = await optimizer.OptimizeAsync(module, trainSet);

// 3. Save.
await optimized.SaveAsync("optimized.json");

// 4. Later — load into a fresh module.
var deployed = new SupportTriageModule(chatClient);
await deployed.LoadAsync("optimized.json");

// 5. Use it.
var result = await deployed.ForwardAsync(new TicketInput("I was charged twice"));
```

### Resulting JSON on Disk

```json
{
  "version": "1.0",
  "module": "SupportTriageModule",
  "predictors": {
    "Classify": {
      "instructions": "Classify a support ticket by category and urgency",
      "demos": [
        {
          "input": { "ticketText": "I was charged twice for my subscription" },
          "output": { "category": "billing", "urgency": 3 }
        },
        {
          "input": { "ticketText": "Can't log in after password reset" },
          "output": { "category": "account", "urgency": 4 }
        },
        {
          "input": { "ticketText": "API returning 500 errors since update" },
          "output": { "category": "technical", "urgency": 5 }
        }
      ],
      "config": { "temperature": 0.1, "model": "gpt-4o-mini" }
    },
    "DraftReply": {
      "instructions": "Draft a helpful reply for a classified support ticket",
      "demos": [
        {
          "input": { "category": "billing", "urgency": 3 },
          "output": { "reply": "I'm sorry about the double charge. I've initiated a refund..." }
        }
      ],
      "config": { "temperature": 0.9, "model": "gpt-4o-mini" }
    }
  }
}
```

---

## 6. Schema Evolution

### Forward Compatibility

New fields are added with sensible defaults. Older loaders ignore unknown properties — this is `System.Text.Json`'s default behavior. **Do not** set `UnmappedMemberHandling = Disallow`.

### Backward Compatibility

The `version` field is the migration pivot:

```csharp
if (state.Version == "1.0")
{
    // current format — no migration needed
}
```

### Rules

1. **Never remove a required field** without a major version bump.
2. **New optional fields** get default values (null or empty).
3. **The `version` field is always present** — it drives migration.
4. **Loaders must tolerate unknown JSON properties** in all versions.

---

## 7. Design Decisions

### What We Dropped (and Why)

| Old concept | Why it was dropped |
|---|---|
| `CompiledArtifact` with `selectedParameters` map | Over-abstraction. Predictors own their state directly — instructions, demos, config. No indirection layer. |
| `variantId` / `baseProgramHash` | Premature. Useful for enterprise provenance, not for MVP save/load. |
| `validationMetrics` / `approved` | Evaluation results belong in eval output, not in the saved module state. |
| XxHash128 content hashing | Nice-to-have integrity check, not essential for a JSON file you `git diff`. |
| SHA-256 program hash | Same — provenance tracking is a post-MVP concern. |
| `AssemblyLoadContext` hot-swap | Massive over-engineering for JSON config. Reloading JSON is trivial — just re-call `LoadAsync()`. |
| `ICompiledArtifactLoader` interface | Unnecessary indirection. `SaveAsync` / `LoadAsync` live on `LmpModule` directly. |
| ZIP / `.lmpa` archive format | There's one file. It's JSON. No archive needed. |
| Polymorphic `StepParameter` types | Demos and config are `JsonElement` dictionaries — flexible without type hierarchies. |
| `bindingMetadata` | Internal binding plumbing doesn't belong in a saved artifact. |
| NuGet package structure | Premature distribution concern. |

### Why This Matches DSPy

DSPy's `module.save("file.json")` produces:

```python
{
  "predict.instructions": "...",
  "predict.demos": [...],
  "predict.lm": { "model": "gpt-4o-mini", "temperature": 0.7 }
}
```

LMP's `module.SaveAsync("file.json")` produces the equivalent in a structured, typed format. Same concept, .NET-native serialization.

---

## 8. Post-MVP Extensions

These are real concerns — just not MVP concerns. Mention for completeness:

| Extension | What It Would Add |
|---|---|
| **NuGet packaging** | Wrap artifact JSON in a `.nupkg` for distribution via Azure Artifacts / GitHub Packages. Follow ML.NET's `contentFiles/` pattern. |
| **Version hashing** | Add optional `provenance` section with content hash, compiler version, timestamp. XxHash128 for fast fingerprinting. |
| **Hot reload** | `IOptionsMonitor<T>` watches the JSON file, triggers `LoadAsync()` on change. No `AssemblyLoadContext` needed — it's just JSON. |
| **Validation metrics sidecar** | Save eval results alongside the artifact (e.g., `optimized.metrics.json`) without polluting the module state file. |

---

*End of Artifact Format Specification*
