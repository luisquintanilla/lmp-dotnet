# Artifact Format Specification

> **Derived from:** spec.org § 11 (Artifact Model), § 5 (Convergence 4), § 6A (Artifact Layer), § 10A (Compiled Artifact semantics)
>
> **Status:** Normative — all implementations must conform.

---

## 1. What Is a Compiled Artifact

A compiled artifact is a file that captures everything needed to run an optimized LM program: which model to use, what temperature, which few-shot examples, what instructions — all selected by the compiler after testing hundreds of variants.

Concretely, the compiler:

1. Explores a combinatorial space of tunable parameters (model choice, temperature, instructions, few-shot examples, retriever settings).
2. Executes each candidate configuration against a validation dataset, collecting quality metrics, latency, and cost.
3. Rejects candidates that violate hard constraints (e.g., `policy_pass_rate == 1.0`).
4. Selects the best valid candidate by weighted objective score.
5. Serializes the winning configuration — the **compiled artifact** — to a JSON file on disk.

At runtime the artifact is loaded, its parameters are applied to the program's steps, and the program executes deterministically using the compiler-selected settings.

### Why Artifacts Matter

| Concern | How the artifact addresses it |
|---|---|
| **Reproducibility** | Pinning every tunable parameter means re-running the program produces the same behavior. |
| **Versioning** | Each artifact carries a `compiledVersion` and `baseProgramHash`, making it trivial to diff configurations across releases. |
| **Deployment** | The artifact is the deployable unit. CI/CD pipelines promote artifacts through staging → production exactly like container images. |
| **Audit** | `validationMetrics`, `provenance`, and `approved` fields provide an auditable record of *why* this configuration was selected. |

---

## 2. Artifact Schema (Complete JSON)

The canonical JSON schema for a compiled artifact:

```json
{
  "program": "support-triage",
  "compiledVersion": "0.1.0",
  "variantId": "triage-v7",
  "baseProgramHash": "sha256:abc123def456...",
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
  "approved": true,
  "provenance": {
    "compiledAt": "2025-07-15T14:32:00Z",
    "compilerVersion": "1.0.0",
    "trialCount": 48,
    "optimizerBackend": "GridSearch",
    "contentHash": "xxhash128:9f3a..."
  },
  "bindingMetadata": {
    "triage": {
      "TicketText": { "kind": "Convention", "tier": 1, "source": "input.TicketText" },
      "AccountTier": { "kind": "Convention", "tier": 1, "source": "input.AccountTier" },
      "KnowledgeSnippets": { "kind": "Attribute", "tier": 2, "source": "steps.retrieve-kb.Documents" },
      "PolicySnippets": { "kind": "Interceptor", "tier": 3, "source": "steps.retrieve-policy.Documents" }
    }
  }
}
```

### Field Reference

| Field | Type | Required | Purpose |
|---|---|---|---|
| `program` | `string` | ✅ | Stable identity of the authored program (e.g., `"support-triage"`). Must match the program's `ProgramDescriptor.Id`. |
| `compiledVersion` | `string` | ✅ | Semantic version of this artifact. Increment on each recompilation. |
| `variantId` | `string` | ✅ | Unique identifier for the selected variant within this compile run. |
| `baseProgramHash` | `string` | ✅ | SHA-256 hash of the source program IR at compile time. Ties the artifact to a specific code version. |
| `selectedParameters` | `object` | ✅ | Key–value map of step-scoped parameter assignments. Keys use `stepName.parameterName` dot notation. Values are strings, numbers, booleans, or string arrays. |
| `validationMetrics` | `object` | ✅ | Metric name → numeric value from validation evaluation. Used for audit and regression detection. |
| `approved` | `bool` | ✅ | `true` if the artifact passed all hard constraints and was selected as best valid. `false` if emitted for diagnostic purposes only. |
| `provenance` | `object` | Optional | Compilation metadata: timestamp, compiler version, trial count, optimizer backend, content hash. |
| `bindingMetadata` | `object` | Optional | Per-step binding metadata. Keys are step names; values are objects mapping property names to `{ "kind": "<Convention|Attribute|Interceptor|ExpressionTree>", "tier": <1|2|3|4>, "source": "<binding expression>" }`. The `tier` field (1–4) indicates the resolution tier from the binding model — Tier 1 (convention) through Tier 4 (expression tree fallback). Used for diagnostics, debugging, and identifying upgrade opportunities from lower tiers to higher ones. |

---

## 3. C# Type Definitions

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// The deployable output of LM program compilation. Captures every tunable
/// parameter selected by the compiler, along with validation metrics and
/// provenance metadata.
/// </summary>
public sealed record CompiledArtifact
{
    public required string Program { get; init; }
    public required string CompiledVersion { get; init; }
    public required string VariantId { get; init; }
    public required string BaseProgramHash { get; init; }

    /// <summary>
    /// Step-scoped parameter assignments. Keys use "stepName.parameterName" notation.
    /// Values are <see cref="JsonElement"/> to support string, number, bool, and array types.
    /// </summary>
    public required ImmutableDictionary<string, JsonElement> SelectedParameters { get; init; }

    public required ImmutableDictionary<string, double> ValidationMetrics { get; init; }
    public required bool Approved { get; init; }
    public ArtifactProvenance? Provenance { get; init; }
}

public sealed record ArtifactProvenance
{
    public DateTimeOffset CompiledAt { get; init; }
    public string? CompilerVersion { get; init; }
    public int TrialCount { get; init; }
    public string? OptimizerBackend { get; init; }
    public string? ContentHash { get; init; }
}
```

### AOT-Safe Serialization

LMP targets Native AOT. All JSON serialization must go through a source-generated `JsonSerializerContext` — never through reflection-based `JsonSerializer.Serialize<T>()`.

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompiledArtifact))]
[JsonSerializable(typeof(ArtifactProvenance))]
public partial class ArtifactSerializerContext : JsonSerializerContext;
```

> **Security Note:** Never deserialize artifacts using `JsonSerializer.Deserialize<T>(json)` without a `JsonSerializerContext`. Reflection-based deserialization can instantiate arbitrary types. Always use `ArtifactSerializerContext.Default.CompiledArtifact` as the type info parameter.

### Polymorphic Step Parameters

When `selectedParameters` values carry typed discriminators (future extension for complex parameter objects), use `[JsonDerivedType]`:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(InstructionVariantParam), "instruction")]
[JsonDerivedType(typeof(FewShotParam), "fewShot")]
[JsonDerivedType(typeof(ModelParam), "model")]
public abstract record StepParameter;

public sealed record InstructionVariantParam(string VariantId) : StepParameter;
public sealed record FewShotParam(ImmutableArray<string> ExampleIds) : StepParameter;
public sealed record ModelParam(string ModelId, double Temperature) : StepParameter;
```

For MVP, `selectedParameters` uses plain `JsonElement` values. The polymorphic types above are reserved for future versions that need richer parameter structures.

---

## 4. Save/Load API

### Interface

```csharp
namespace LMP;

/// <summary>
/// Loads and saves compiled artifacts. Implementations handle format,
/// storage location, and integrity verification.
/// </summary>
public interface ICompiledArtifactLoader
{
    Task<CompiledArtifact> LoadAsync(string path, CancellationToken ct = default);
    Task SaveAsync(CompiledArtifact artifact, string path, CancellationToken ct = default);
}
```

### Implementation: FileSystemArtifactLoader

```csharp
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LMP.Runtime;

public sealed class FileSystemArtifactLoader : ICompiledArtifactLoader
{
    private static readonly JsonSerializerOptions s_options =
        ArtifactSerializerContext.Default.Options;

    /// <summary>
    /// Save workflow:
    ///   1. Serialize artifact to JSON.
    ///   2. Compute XxHash128 content hash for fast fingerprinting.
    ///   3. Stamp the hash into provenance.
    ///   4. Re-serialize with the stamped hash.
    ///   5. Write atomically (temp file → rename) to prevent partial writes.
    /// </summary>
    public async Task SaveAsync(
        CompiledArtifact artifact,
        string path,
        CancellationToken ct = default)
    {
        // Step 1: Serialize without content hash to compute hash of the payload.
        var withoutHash = artifact with
        {
            Provenance = (artifact.Provenance ?? new ArtifactProvenance()) with
            {
                ContentHash = null
            }
        };

        byte[] preliminary = JsonSerializer.SerializeToUtf8Bytes(
            withoutHash,
            ArtifactSerializerContext.Default.CompiledArtifact);

        // Step 2: Compute XxHash128 fingerprint.
        var xxHash = new XxHash128();
        xxHash.Append(preliminary);
        byte[] hashBytes = xxHash.GetCurrentHash();
        string hashHex = $"xxhash128:{Convert.ToHexStringLower(hashBytes)}";

        // Step 3: Stamp hash into provenance.
        var stamped = artifact with
        {
            Provenance = (artifact.Provenance ?? new ArtifactProvenance()) with
            {
                ContentHash = hashHex,
                CompiledAt = artifact.Provenance?.CompiledAt ?? DateTimeOffset.UtcNow
            }
        };

        // Step 4: Final serialization.
        byte[] final = JsonSerializer.SerializeToUtf8Bytes(
            stamped,
            ArtifactSerializerContext.Default.CompiledArtifact);

        // Step 5: Atomic write.
        string tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, final, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Load workflow:
    ///   1. Read file bytes.
    ///   2. Deserialize.
    ///   3. Verify content hash if present.
    ///   4. Return validated artifact.
    /// </summary>
    public async Task<CompiledArtifact> LoadAsync(
        string path,
        CancellationToken ct = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);

        var artifact = JsonSerializer.Deserialize(
            bytes,
            ArtifactSerializerContext.Default.CompiledArtifact)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize artifact from {path}");

        // Verify integrity if content hash is present.
        if (artifact.Provenance?.ContentHash is { } expectedHash)
        {
            VerifyContentHash(artifact, expectedHash);
        }

        return artifact;
    }

    private static void VerifyContentHash(
        CompiledArtifact artifact,
        string expectedHash)
    {
        // Recompute: serialize without hash, then hash.
        var withoutHash = artifact with
        {
            Provenance = artifact.Provenance! with { ContentHash = null }
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            withoutHash,
            ArtifactSerializerContext.Default.CompiledArtifact);

        var xxHash = new XxHash128();
        xxHash.Append(payload);
        string actual = $"xxhash128:{Convert.ToHexStringLower(xxHash.GetCurrentHash())}";

        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifact integrity check failed. Expected {expectedHash}, got {actual}. " +
                "The file may have been modified after compilation.");
        }
    }
}
```

> **Security Note:** Always validate `path` inputs. Reject paths containing `..` traversal sequences. In production, restrict artifact loading to a configured allow-listed directory.

> **.NET 10 Enhancement:** When artifacts evolve to a multi-file archive format (e.g., `.lmpa` bundle with separate JSON + evaluation data), use `ZipFile.OpenAsync()` (.NET 10) for fully async archive I/O. This avoids blocking the thread pool during artifact packaging in CI/CD pipelines.

---

## 5. Hot-Swap Architecture

Hot-swap lets a running service switch from artifact v1 to v2 without restarting. This is critical for production systems that serve traffic 24/7.

### Design

The hot-swap mechanism relies on three .NET primitives from Convergence 4 in the spec:

| Primitive | Role |
|---|---|
| `AssemblyLoadContext` (collectible) | Isolates each artifact version in its own context so the old version's types can be garbage-collected. |
| `IOptionsMonitor<T>` + `IChangeToken` | Watches the artifact file on disk and triggers reload on change. |
| Atomic reference swap | `Interlocked.Exchange` swaps the active artifact pointer so in-flight requests finish on v1 while new requests use v2. |

### Implementation

```csharp
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LMP.Runtime;

/// <summary>
/// Configuration type that points to the artifact file on disk.
/// Bound via IOptions in DI with validation and ConfigurationBinder source generation.
/// </summary>
public sealed class ArtifactOptions
{
    public const string SectionName = "LmpArtifact";

    [Required]
    public required string ArtifactPath { get; set; }

    public bool AutoReload { get; set; } = true;
}

// DI registration with fail-fast validation:
// services.AddOptions<ArtifactOptions>()
//     .BindConfiguration(ArtifactOptions.SectionName)
//     .ValidateDataAnnotations()
//     .ValidateOnStart();
//
// Enable AOT-safe configuration binding in .csproj:
// <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>

/// <summary>
/// Watches an artifact file and hot-swaps the active <see cref="CompiledArtifact"/>
/// when the file changes. Register as a singleton in DI.
/// </summary>
public sealed class HotSwapArtifactProvider : IDisposable
{
    private readonly ICompiledArtifactLoader _loader;
    private readonly IOptionsMonitor<ArtifactOptions> _optionsMonitor;
    private readonly IDisposable? _changeSubscription;

    private volatile CompiledArtifact? _current;
    private volatile CollectibleArtifactContext? _currentContext;
    private readonly SemaphoreSlim _swapLock = new(1, 1);

    public HotSwapArtifactProvider(
        ICompiledArtifactLoader loader,
        IOptionsMonitor<ArtifactOptions> optionsMonitor)
    {
        _loader = loader;
        _optionsMonitor = optionsMonitor;

        // Initial load.
        _current = LoadAndIsolate(optionsMonitor.CurrentValue.ArtifactPath)
            .GetAwaiter().GetResult();

        // Watch for changes.
        _changeSubscription = optionsMonitor.OnChange(opts =>
        {
            _ = SwapAsync(opts.ArtifactPath);
        });
    }

    /// <summary>Returns the currently active artifact. Never null after construction.</summary>
    public CompiledArtifact Current =>
        _current ?? throw new InvalidOperationException("No artifact loaded.");

    private async Task SwapAsync(string newPath)
    {
        await _swapLock.WaitAsync();
        try
        {
            var oldContext = _currentContext;

            // Load new artifact in a fresh collectible context.
            var newArtifact = await LoadAndIsolate(newPath);

            // Atomic swap: new requests immediately see the new artifact.
            Interlocked.Exchange(ref _current, newArtifact);

            // Unload old context. The GC will collect it once all references
            // from in-flight requests are released.
            oldContext?.Unload();
        }
        finally
        {
            _swapLock.Release();
        }
    }

    private async Task<CompiledArtifact> LoadAndIsolate(string path)
    {
        var ctx = new CollectibleArtifactContext();
        _currentContext = ctx;

        // The loader reads and deserializes within this context.
        return await _loader.LoadAsync(path);
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
        _swapLock.Dispose();
    }
}

/// <summary>
/// A collectible AssemblyLoadContext that allows unloading old artifact
/// versions and reclaiming their memory.
/// </summary>
internal sealed class CollectibleArtifactContext : AssemblyLoadContext
{
    public CollectibleArtifactContext()
        : base(isCollectible: true) { }
}
```

### Memory Management

When `oldContext.Unload()` is called:

1. The CLR marks the context for unloading.
2. Once all live references to types loaded in that context are released (i.e., in-flight requests complete), the GC collects them.
3. **No manual memory management is required.** The collectible `AssemblyLoadContext` is a first-class CLR primitive designed for this scenario.

> **Security Note:** File-system watchers (`IOptionsMonitor`) should only monitor trusted directories. An attacker who can write to the artifact directory can inject malicious configurations. Restrict write access via OS-level file permissions.

---

## 6. Provenance and Integrity

Artifacts carry two levels of integrity verification:

### XxHash128 — Fast Fingerprinting

- **Algorithm:** `System.IO.Hashing.XxHash128` (non-cryptographic, extremely fast).
- **Purpose:** Detect accidental corruption or unintended edits. Used as the `contentHash` in `provenance`.
- **Format:** `xxhash128:<32-char lowercase hex>`

Use XxHash128 for:
- Cache keys
- Quick equality checks ("has this artifact changed?")
- CI/CD pipeline fingerprinting

### SHA-256 — Cryptographic Verification

- **Algorithm:** `System.Security.Cryptography.SHA256`
- **Purpose:** Tamper detection in high-assurance environments. Used as the `baseProgramHash`.
- **Format:** `sha256:<64-char lowercase hex>`

Use SHA-256 for:
- `baseProgramHash` — ties the artifact to the exact source program IR that was compiled.
- Deployment gate checks — verify that the artifact on disk matches the artifact that was approved in CI.

```csharp
public static class ArtifactHashing
{
    /// <summary>
    /// Computes the SHA-256 hash of a program IR, used as baseProgramHash.
    /// </summary>
    public static string ComputeProgramHash(byte[] programIrBytes)
    {
        byte[] hash = SHA256.HashData(programIrBytes);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// Computes XxHash128 of raw artifact JSON for fast fingerprinting.
    /// </summary>
    public static string ComputeContentHash(byte[] artifactJson)
    {
        var hasher = new XxHash128();
        hasher.Append(artifactJson);
        return $"xxhash128:{Convert.ToHexStringLower(hasher.GetCurrentHash())}";
    }
}
```

### baseProgramHash

The `baseProgramHash` field ties an artifact to the specific version of the authored source code. If the program's IR changes (e.g., a step is added, a signature is modified), the hash changes, and the old artifact is **incompatible** — the runtime must reject it.

### compiledVersion

Follows [Semantic Versioning](https://semver.org/):
- **Patch:** Recompiled with same source, same search space (e.g., new training data).
- **Minor:** Search space expanded (new tunable dimension added).
- **Major:** Breaking source change (step renamed, signature restructured).

---

## 7. Schema Evolution

Artifact files will evolve over time. The format must handle this gracefully.

### Forward Compatibility (Adding New Fields)

New fields are added with sensible defaults. Older loaders ignore them.

```csharp
// v1: no "provenance" field.
// v2: "provenance" field added.
// A v1 loader reads a v2 file and simply ignores "provenance" — JsonSerializer
// skips unknown properties by default.
```

### Backward Compatibility (Ignoring Unknown Fields)

The default `System.Text.Json` behavior is to skip unknown JSON properties during deserialization. **Do not** set `JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow` on artifact deserialization — this would break backward compatibility.

### Version-Based Migration

When the schema changes in a breaking way, use the `compiledVersion` field to branch migration logic:

```csharp
public static CompiledArtifact MigrateIfNeeded(CompiledArtifact artifact)
{
    var version = Version.Parse(artifact.CompiledVersion);

    if (version < new Version(2, 0, 0))
    {
        // v1 → v2 migration: e.g., flatten nested parameters.
        return artifact with
        {
            CompiledVersion = "2.0.0",
            // ... apply migration transforms ...
        };
    }

    return artifact;
}
```

### Rules

1. **Never remove a required field** without a major version bump.
2. **New optional fields** get default values (null, empty collection, or a sensible constant).
3. **The `compiledVersion` field is always present** — it is the migration pivot.
4. **Loader implementations must tolerate unknown JSON properties** in all versions.

---

## 8. Complete Example

### Step 1: Compile and Save

```csharp
// After the compiler selects the best valid candidate:
var artifact = new CompiledArtifact
{
    Program = "support-triage",
    CompiledVersion = "0.1.0",
    VariantId = "triage-v7",
    BaseProgramHash = ArtifactHashing.ComputeProgramHash(programIrBytes),
    SelectedParameters = ImmutableDictionary.CreateRange(new[]
    {
        Param("triage.instructionsVariant", "inst-3"),
        Param("triage.model", "gpt-4.1-mini"),
        Param("triage.temperature", 0.1),
        Param("triage.fewShotExampleIds", new[] { "ex-12", "ex-44", "ex-78", "ex-121" }),
        Param("retrieve-kb.topK", 6),
        Param("retrieve-policy.topK", 3),
    }),
    ValidationMetrics = ImmutableDictionary.CreateRange(new Dictionary<string, double>
    {
        ["routing_accuracy"] = 0.89,
        ["severity_accuracy"] = 0.83,
        ["groundedness"] = 0.96,
        ["policy_pass_rate"] = 1.0,
        ["p95_latency_ms"] = 2210,
        ["avg_cost_usd"] = 0.021,
    }),
    Approved = true,
    Provenance = new ArtifactProvenance
    {
        CompiledAt = DateTimeOffset.UtcNow,
        CompilerVersion = "1.0.0",
        TrialCount = 48,
        OptimizerBackend = "GridSearch",
    }
};

var loader = new FileSystemArtifactLoader();
await loader.SaveAsync(artifact, "artifacts/support-triage-v0.1.0.json");

// Helper to create JsonElement values from CLR types.
static KeyValuePair<string, JsonElement> Param(string key, object value) =>
    KeyValuePair.Create(key, JsonSerializer.SerializeToElement(value));
```

### Step 2: Load and Apply to Runtime

```csharp
var loader = new FileSystemArtifactLoader();
var artifact = await loader.LoadAsync("artifacts/support-triage-v0.1.0.json");

Console.WriteLine($"Loaded artifact: {artifact.Program} v{artifact.CompiledVersion}");
Console.WriteLine($"  Variant:    {artifact.VariantId}");
Console.WriteLine($"  Model:      {artifact.SelectedParameters["triage.model"]}");
Console.WriteLine($"  Accuracy:   {artifact.ValidationMetrics["routing_accuracy"]:P0}");
Console.WriteLine($"  Approved:   {artifact.Approved}");

// Apply to runtime: the runtime reads selectedParameters and configures
// each step accordingly.
var runtime = new LmpProgramRuntime(artifact);
var result = await runtime.RunAsync(new SupportTicket
{
    Subject = "Cannot access billing portal",
    Body = "I keep getting a 403 error when trying to view invoices...",
    CustomerTier = "Enterprise"
});
```

### Step 3: Hot-Swap from v1 to v2

```csharp
// In DI registration (Startup / Program.cs):
builder.Services.Configure<ArtifactOptions>(opts =>
    opts.ArtifactPath = "artifacts/support-triage-latest.json");

builder.Services.AddSingleton<ICompiledArtifactLoader, FileSystemArtifactLoader>();
builder.Services.AddSingleton<HotSwapArtifactProvider>();

// ...

// The service is running with v0.1.0.
// An operator drops a new artifact file:
//   artifacts/support-triage-latest.json  (now v0.2.0)
//
// IOptionsMonitor detects the file change.
// HotSwapArtifactProvider:
//   1. Loads v0.2.0 in a fresh CollectibleArtifactContext.
//   2. Verifies the content hash.
//   3. Atomically swaps _current from v0.1.0 → v0.2.0.
//   4. Unloads the v0.1.0 context.
//
// In-flight requests on v0.1.0 complete normally.
// New requests immediately use v0.2.0.
// No restart. No downtime. No request failures.
```

### Resulting JSON on Disk

```json
{
  "program": "support-triage",
  "compiledVersion": "0.1.0",
  "variantId": "triage-v7",
  "baseProgramHash": "sha256:a1b2c3d4e5f6...",
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
  "approved": true,
  "provenance": {
    "compiledAt": "2025-07-15T14:32:00+00:00",
    "compilerVersion": "1.0.0",
    "trialCount": 48,
    "optimizerBackend": "GridSearch",
    "contentHash": "xxhash128:9f3a1b2c3d4e5f6a7b8c9d0e1f2a3b4c"
  }
}
```

---

## 9. NuGet Artifact Distribution (Post-MVP)

### 9.1 Why NuGet?

Compiled LMP artifacts (optimized prompts, selected models, few-shot examples) are **deployable units** — just like ML.NET ONNX models or EF Core compiled models. NuGet already solves every distribution problem:

| Need | NuGet Solution |
|------|----------------|
| **Versioning** | SemVer 2.0 — `1.0.0`, `2.0.0-beta.1` |
| **Distribution** | nuget.org, Azure Artifacts, GitHub Packages |
| **Authentication** | PAT tokens, service principals |
| **Provenance** | SourceLink, repository metadata, commit SHA |
| **Dependency management** | Automatic transitive resolution |
| **Rollback** | Pin to any previous version |

**Precedent:** ML.NET distributes pre-trained ONNX models via NuGet `contentFiles/`.
Source: [ML.NET ONNX Model Distribution](https://learn.microsoft.com/azure/machine-learning/how-to-use-automl-onnx-model-dotnet)

### 9.2 Artifact NuGet Package Structure

```
MyCompany.Lmp.SupportTriage.1.0.0.nupkg
├── lib/net10.0/
│   └── SupportTriage.dll                       # Compiled types (signatures, program class)
├── contentFiles/any/any/artifacts/
│   └── support-triage-v1.0.0.json              # Compiled artifact
├── build/
│   └── MyCompany.Lmp.SupportTriage.targets     # Auto-registration targets
└── buildTransitive/
    └── MyCompany.Lmp.SupportTriage.targets     # Transitive auto-registration
```

### 9.3 Auto-Registration .targets File

The package ships a `.targets` file that automatically copies the artifact to the output directory and sets an MSBuild property for discovery:

```xml
<Project>
  <PropertyGroup>
    <LmpArtifact_SupportTriage>$(MSBuildThisFileDirectory)..\contentFiles\any\any\artifacts\support-triage-v1.0.0.json</LmpArtifact_SupportTriage>
  </PropertyGroup>

  <ItemGroup>
    <None Include="$(LmpArtifact_SupportTriage)"
          Link="artifacts\support-triage-v1.0.0.json"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

### 9.4 Consumer Experience

```xml
<!-- .csproj — add one line -->
<PackageReference Include="MyCompany.Lmp.SupportTriage" Version="1.0.0" />
```

```csharp
// Program.cs — artifact is auto-available in output directory
builder.Services.AddLmpProgram<SupportTriageProgram>(opts =>
    opts.ArtifactPath = "artifacts/support-triage-v1.0.0.json");
```

### 9.5 CLI Workflow

```bash
# 1. Compile (runs optimizer trials — costs $$$)
dotnet lmp compile --program SupportTriage --data eval-data.json

# 2. Package artifact as NuGet
dotnet lmp pack \
    --artifact artifacts/support-triage-v1.0.0.json \
    --id MyCompany.Lmp.SupportTriage \
    --version 1.0.0 \
    --output ./nupkgs

# 3. Push to private feed
dotnet nuget push ./nupkgs/MyCompany.Lmp.SupportTriage.1.0.0.nupkg \
    --source MyPrivateFeed

# 4. Consuming team adds reference — gets optimized program
dotnet add package MyCompany.Lmp.SupportTriage --version 1.0.0
```

### 9.6 Enterprise Distribution Feeds

| Feed | Use Case |
|------|----------|
| **Azure Artifacts** | Enterprise private packages. `https://pkgs.dev.azure.com/{org}/_packaging/{feed}/nuget/v3/index.json` |
| **GitHub Packages** | OSS + private org packages. `https://nuget.pkg.github.com/{owner}/index.json` |
| **Local feed** | Air-gapped environments. File system directory as NuGet source. |

---

*End of Artifact Format Specification*
