# Build-Time Auto-Optimization

**Technique:** Build-Time Auto-Optimization &nbsp;|&nbsp; **Difficulty:** Advanced

> Let MSBuild optimize your LM modules automatically — no manual prompt-tuning,
> no runtime overhead, and fully reproducible in CI/CD.

---

## What You'll Learn

| Concept | Where to look |
|---|---|
| `[AutoOptimize]` attribute | `QAModule.cs` |
| Source-generated `partial void ApplyOptimizedState()` | Source gen output (build `obj/` dir) |
| Generated `.g.cs` optimization artifacts | `Generated/QAModule.Optimized.g.cs` |
| MSBuild integration (`LMP.Build.targets`) | `.csproj` + build output |
| Convention-based `CreateClient()` and `Score()` discovery | `QAModule.cs` |

---

## The Problem

Manual prompt optimization doesn't scale:

1. **Developer runs optimizer locally** → saves a JSON state file → checks it in.
2. **Training data changes** → someone has to remember to re-optimize.
3. **CI/CD pipelines** can't reproduce the optimization step.
4. **Runtime `LoadState()` calls** are easy to forget and add latency on startup.

What if `dotnet build` could do all of this for you?

---

## How It Works

The auto-optimize pipeline has four phases — only the first requires your code:

```
┌─────────────────────────────────────────────────────────────────────────┐
│ 1. AUTHOR TIME  — you write code                                       │
│                                                                         │
│    [AutoOptimize(TrainSet = "data/train.jsonl", DevSet = "data/dev.jsonl")]│
│    public partial class QAModule : LmpModule<QAInput, QAOutput> { ... }│
│                                                                         │
│ 2. SOURCE GEN   — Roslyn source generator runs during every build      │
│                                                                         │
│    Emits: partial void ApplyOptimizedState();   // declaration          │
│    Emits: call site in GetPredictors()          // guarded by a flag   │
│    If no .g.cs implementation exists → compiler removes both (C# rule) │
│                                                                         │
│ 3. OPTIMIZE     — triggered by MSBuild property                        │
│                                                                         │
│    dotnet build -p:LmpAutoOptimize=true                                │
│      → LMP.Build.targets runs BeforeTargets="CoreCompile"             │
│      → CLI loads your module via reflection                            │
│      → Discovers CreateClient() for the IChatClient                    │
│      → Discovers Score() for the evaluation metric                     │
│      → Runs BootstrapRandomSearch against train/dev JSONL              │
│      → Writes Generated/QAModule.Optimized.g.cs with winning state    │
│                                                                         │
│ 4. COMPILE      — the SAME build (or any future build)                 │
│                                                                         │
│    Compiler sees the .g.cs in Generated/ → it provides the body for   │
│    partial void ApplyOptimizedState() → optimized demos/instructions   │
│    are baked in as C# literals → zero runtime file I/O                 │
└─────────────────────────────────────────────────────────────────────────┘
```

After optimization, every subsequent `dotnet build` compiles the `.g.cs` artifact
and reports the optimization metadata via an `LMP1000` build warning.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| **.NET 10 SDK** | `dotnet --version` ≥ 10.0 |
| **Azure OpenAI resource** | Any chat-completion deployment (e.g. `gpt-4o-mini`) |
| **Azure identity** | `DefaultAzureCredential` — `az login` is the easiest path |

### One-time setup (user secrets)

```bash
cd samples/LMP.Samples.AutoOptimize

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
```

> **Tip:** You can override the model at build time with
> `dotnet build -p:LmpModel=gpt-4o` or by setting the `LMP_MODEL`
> environment variable.

---

## Run It

There are two paths — depending on whether you want to *use* an existing
optimization or *generate* a new one.

### Path A: Run with existing `.g.cs` (default)

The repo ships with a pre-generated `Generated/QAModule.Optimized.g.cs`.
Just run:

```bash
dotnet run --project samples/LMP.Samples.AutoOptimize
```

You'll see the optimized predictor state (instructions + demos) loaded
automatically, followed by answers to sample questions.

### Path B: Re-optimize from scratch

```bash
dotnet build samples/LMP.Samples.AutoOptimize -p:LmpAutoOptimize=true
```

This triggers the full pipeline:

1. MSBuild invokes the LMP CLI **before** compilation.
2. The CLI discovers `QAModule` (via `[AutoOptimize]`), loads training data,
   and runs the optimizer against your Azure OpenAI deployment.
3. The winning state is written to `Generated/QAModule.Optimized.g.cs`.
4. The **same build** then compiles the new `.g.cs` into the assembly.

After optimization completes, run the app normally:

```bash
dotnet run --project samples/LMP.Samples.AutoOptimize
```

### Additional MSBuild properties

| Property | Default | Description |
|---|---|---|
| `LmpAutoOptimize` | `false` | Set to `true` to trigger optimization |
| `LmpAutoOptimizeForce` | `false` | Re-optimize even if data hasn't changed |
| `LmpOptimizer` | `random` | Strategy: `bootstrap`, `random`, `mipro`, or `gepa` |
| `LmpNumTrials` | `8` | Number of optimization trials/iterations |
| `LmpMaxDemos` | `4` | Max few-shot demos per predictor |
| `LmpAutoOptimizeBudget` | *(attribute value)* | Time budget in seconds (overrides `BudgetSeconds` in attribute) |
| `LmpModel` | *(user secret)* | Override the model deployment name |
| `LmpAutoOptimizeVerbose` | `false` | Show detailed optimizer output |

**Example — MIPROv2 with 20 trials and 5-minute budget:**

```bash
dotnet build -p:LmpAutoOptimize=true -p:LmpOptimizer=mipro -p:LmpNumTrials=20 -p:LmpAutoOptimizeBudget=300
```

### Baseline guard

The CLI measures a **baseline score** before optimization (running the unoptimized module
on the training set). After optimization, it evaluates the optimized module on the **same
dataset**. The `.g.cs` artifact is only written if the optimized score **strictly improves**
over baseline. If not, you'll see:

```
Optimization did not improve over baseline.
  Baseline: 0.7801  |  Optimized: 0.7735
  Artifact NOT written (no improvement)
```

This prevents committing regressions. The `LMP1000` build diagnostic also shows the
baseline when available: `Score: 0.82 (Baseline: 0.65)`.

---

## Code Walkthrough

### 1. The `[AutoOptimize]` attribute

```csharp
// QAModule.cs
[AutoOptimize(TrainSet = "data/train.jsonl", DevSet = "data/dev.jsonl")]
public partial class QAModule : LmpModule<QAInput, QAOutput>
```

This attribute serves **two audiences**:

- **Source generator** — emits the `partial void ApplyOptimizedState()`
  declaration and a once-guarded call site inside `GetPredictors()`.
- **CLI tool** — reads `TrainSet`, `DevSet`, and `BudgetSeconds` to
  configure the optimization run.

The class **must** be `partial` so the source generator can extend it.

### 2. Convention: `CreateClient()`

```csharp
// QAModule.cs
public static IChatClient CreateClient()
{
    var config = new ConfigurationBuilder()
        .AddUserSecrets<QAModule>()
        .Build();

    string endpoint = config["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException(
            "Run: dotnet user-secrets set AzureOpenAI:Endpoint https://YOUR_RESOURCE.openai.azure.com/");

    string deployment = Environment.GetEnvironmentVariable("LMP_MODEL")
        ?? config["AzureOpenAI:Deployment"] ?? "gpt-4o-mini";

    return new AzureOpenAIClient(
            new Uri(endpoint), new DefaultAzureCredential())
        .GetChatClient(deployment)
        .AsIChatClient();
}
```

The CLI discovers this **static factory** via reflection at optimization time —
similar to how EF Core discovers `IDesignTimeDbContextFactory<T>`. You own the
client construction; any provider (Azure OpenAI, Ollama, Anthropic, etc.) works.

The same factory is called at runtime in `Program.cs`, keeping things DRY.

### 3. Convention: `Score()`

```csharp
// QAModule.cs
public static float Score(QAOutput predicted, QAOutput expected)
{
    // Word-overlap metric: fraction of expected words found in predicted
    ...
}
```

The CLI discovers this **static scoring function** via reflection. It must:

- Be `static` on the module class
- Accept `(TOutput predicted, TOutput expected)` and return `float`

This is **optional** — if absent, the optimizer falls back to keyword overlap
on string properties.

### 4. Input / Output types

```csharp
// Types.cs
public record QAInput(
    [property: Description("The question to answer")]
    string Question);

[LmpSignature("Answer the given question")]
public partial record QAOutput
{
    [Description("The answer to the question")]
    public required string Answer { get; init; }
}
```

- `[Description]` attributes become field descriptions in the LLM prompt.
- `[LmpSignature]` sets the task description for the predictor.
- `QAOutput` is `partial` so the source generator can extend it with
  JSON serialization support.

### 5. The generated `.g.cs` anatomy

After optimization, `Generated/QAModule.Optimized.g.cs` looks like:

```csharp
// <auto-generated>
// LMP Auto-Optimize | Score: 0.6346 | Strategy: BootstrapRandomSearch | Demos: 1
// Dataset: sha256:475a4d91b6f006ff
// Generated: 2026-04-10T20:13:34.9242581Z
// </auto-generated>

#nullable enable

namespace LMP.Samples.AutoOptimize;

partial class QAModule
{
    partial void ApplyOptimizedState()
    {
        _qa.AddDemo(
            input: new QAInput("Who discovered penicillin?"),
            output: new QAOutput { Answer = "Penicillin was discovered by Alexander Fleming in 1928." });
    }
}
```

Key details:

- **Header comments** — metadata (score, strategy, demo count, dataset hash,
  timestamp) parsed by MSBuild for the `LMP1000` build warning.
- **`partial void` body** — provides the implementation that the source
  generator declared. Calls `AddDemo()` and/or sets `Instructions` on
  predictors.
- **Pure C# literals** — no JSON files, no runtime deserialization. The
  optimized state is compiled directly into IL.
- **Dataset hash** — enables staleness detection. Re-running optimization
  with unchanged data is a no-op (unless `--force` is used).

---

## Build Output

On every build (even without `-p:LmpAutoOptimize=true`), the `_LmpReportOptimization`
target reads `.g.cs` headers and emits a build warning:

```
warning LMP1000: QAModule optimized — Score: 0.6346 | Strategy: BootstrapRandomSearch | Demos: 1 | 2026-04-10
```

If **no** `.g.cs` files exist yet, you'll see:

```
warning LMP1001: No optimized modules found. Run 'dotnet lmp auto-optimize --project ...' to optimize.
```

> **Tip:** Suppress the warning with `<NoWarn>LMP1000</NoWarn>` in your
> `.csproj` if you don't want it in CI logs.

---

## Key Takeaways

1. **`[AutoOptimize]` is declarative** — mark a module and provide dataset
   paths; the pipeline handles the rest.
2. **Source gen + partial void = zero cost when absent** — if no `.g.cs`
   exists, the compiler removes the hook entirely.
3. **Optimization is a build step, not a runtime step** — the winning state
   is compiled into IL as C# literals. No file I/O, no JSON, no latency.
4. **Convention over configuration** — `CreateClient()` and `Score()` are
   discovered by reflection, keeping the attribute surface small.
5. **MSBuild-native** — works with `dotnet build`, CI/CD, and
   `dotnet publish` out of the box. The `.g.cs` files are checked into
   source control like EF Core migrations.
6. **Staleness detection** — dataset hashes prevent redundant re-optimization.

---

## Next Steps

- **Add a second module** — create another `[AutoOptimize]` class in the
  same project. Each gets its own `.g.cs` artifact.
- **Custom optimizer budget** — try
  `dotnet build -p:LmpAutoOptimize=true -p:LmpAutoOptimizeBudget=300`
  for a longer search.
- **CI/CD integration** — add `-p:LmpAutoOptimize=true` to your CI build
  step. Commit the resulting `.g.cs` changes back via a PR.
- **Explore other samples** — see the sibling samples for runtime
  optimization, manual state management, and multi-module composition.
