# Middleware Pipeline

| | |
|---|---|
| **Technique** | Microsoft.Extensions.AI Middleware Pipeline |
| **Difficulty** | Intermediate |

## What You'll Learn

- How to wrap an LLM client with **caching**, **distributed tracing**, and **structured logging** using the `Microsoft.Extensions.AI` middleware pipeline
- How `ChatClientBuilder` composes middleware in a specific order — and why that order matters
- How caching accelerates both evaluation and optimization by eliminating redundant LLM calls
- How to read OpenTelemetry traces and ILogger output to diagnose LLM behavior in production

## The Problem

You've built an LMP module that works. Now you need to run it in production. Questions immediately arise:

- **How long does each LLM call take?** Without tracing, you can't tell whether a slow response is the model or your network.
- **How much are you spending?** Identical prompts during evaluation and optimization hit the LLM repeatedly — wasting tokens and money.
- **What did the model actually see?** When something goes wrong, you need structured logs of the exact request and response — not just "it returned the wrong answer."

The `Microsoft.Extensions.AI` middleware pipeline solves all three by wrapping `IChatClient` with composable layers — no changes to your module code.

## How It Works

This sample builds a three-layer middleware pipeline around an Azure OpenAI client:

```
Request → Logging → OpenTelemetry → Cache → Azure OpenAI
                                         ↓
Response ← Logging ← OpenTelemetry ← Cache ← Azure OpenAI
```

Each layer is an `IChatClient` decorator added via `ChatClientBuilder`:

| Layer | What It Does | Why It's There |
|---|---|---|
| **DistributedCache** | Caches LLM responses keyed by prompt content | Identical prompts during eval/optimization return instantly |
| **OpenTelemetry** | Emits spans with model, token count, and latency | Traces every actual LLM call (cache misses only) |
| **ILogger** | Logs request/response at `Information` level | Structured logs for every request, cached or not |

**Order matters.** Cache is innermost so cache hits skip the tracing and logging overhead entirely. OpenTelemetry wraps only actual LLM calls. Logging is outermost so it captures everything — including cache hits.

The sample then runs the same evaluation twice to show the timing difference, and finishes with a `BootstrapFewShot` optimization pass that benefits from the warm cache.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.201+)
- An Azure OpenAI resource with a deployed model (e.g., `gpt-4.1-nano`)
- Azure credentials configured for `DefaultAzureCredential` (Azure CLI login, managed identity, etc.)

## Run It

**1. Configure your Azure OpenAI endpoint via user secrets:**

```bash
cd samples/LMP.Samples.Middleware

dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR_DEPLOYMENT_NAME"
```

> Replace `YOUR_RESOURCE` with your Azure OpenAI resource name and `YOUR_DEPLOYMENT_NAME` with the model deployment name (e.g., `gpt-4.1-nano`).

**2. Log in to Azure (if using Azure CLI credentials):**

```bash
az login
```

**3. Run the sample:**

```bash
dotnet run --project samples/LMP.Samples.Middleware
```

## Code Walkthrough

### 1. Setting Up the Middleware Components

The sample creates three independent infrastructure pieces before wiring them together:

```csharp
// OpenTelemetry tracer — exports spans to the console
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(sourceName)
    .AddConsoleExporter()
    .Build();

// Structured logging via ILoggerFactory
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// In-memory distributed cache (swap for Redis in production)
var cache = new MemoryDistributedCache(
    Options.Create(new MemoryDistributedCacheOptions()));
```

Each of these is a standard `Microsoft.Extensions.*` component — nothing LMP-specific. In a real app, these would come from dependency injection.

### 2. Building the Pipeline with ChatClientBuilder

This is the key pattern. `ChatClientBuilder` chains middleware around the raw Azure OpenAI client:

```csharp
IChatClient innerClient = new AzureOpenAIClient(
        new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

IChatClient client = new ChatClientBuilder(innerClient)
    .UseDistributedCache(cache)       // Innermost: cache hits skip everything below
    .UseOpenTelemetry(sourceName: sourceName,
        configure: c => c.EnableSensitiveData = true)
    .UseLogging(loggerFactory)         // Outermost: logs all requests
    .Build();
```

The resulting `client` is still just an `IChatClient` — your module code doesn't change at all. The `SupportTriageModule` receives this wrapped client and uses it exactly like a plain client.

### 3. The SupportTriageModule

The module is a two-step pipeline that classifies a support ticket then drafts a reply:

```csharp
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public override async Task<DraftReply> ForwardAsync(TicketInput input, ...)
    {
        var classification = await _classify.PredictAsync(input, trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"));

        return await _draft.PredictAsync(classification, trace: Trace);
    }
}
```

Notice that the module has **no awareness of caching, tracing, or logging**. The middleware pipeline handles all of that transparently because it's layered on the `IChatClient` interface.

### 4. Cold Cache vs. Warm Cache

The sample runs the same evaluation twice on the same dev set:

```csharp
// Step 1: Cold cache — every call hits the LLM
var coldResult = await Evaluator.EvaluateAsync(module, devSet, metric);

// Step 2: Warm cache — identical prompts return from cache
var module2 = new SupportTriageModule(client);  // Fresh module, same cache
var warmResult = await Evaluator.EvaluateAsync(module2, devSet, metric);
```

The second run produces the same scores but completes dramatically faster because every prompt was already cached from the first run.

### 5. Optimization with a Warm Cache

The final step runs `BootstrapFewShot` optimization, which internally generates many LLM calls:

```csharp
var optimizer = new BootstrapFewShot(maxDemos: 3, metricThreshold: 0.3f);
var optimized = await optimizer.CompileAsync(module, trainSet, untypedMetric);
```

Because some training examples share prompts with the dev set evaluation, the optimizer benefits from cache hits — reducing both latency and token cost.

## Expected Output

```
╔══════════════════════════════════════════════╗
║   LMP — Middleware Pipeline Demo             ║
║          (Azure OpenAI)                      ║
╚══════════════════════════════════════════════╝

  Using: YOUR_DEPLOYMENT_NAME @ https://YOUR_RESOURCE.openai.azure.com/

Pipeline: Azure OpenAI → Cache → OpenTelemetry → Logging

Step 1: Evaluate (Cold Cache — all calls hit LLM)
──────────────────────────────────────────────────
  Score:    XX.X%
  Duration: XXXXms (cold — all 25 examples hit LLM)

Step 2: Evaluate (Warm Cache — identical prompts are cached)
─────────────────────────────────────────────────────────────
  Score:    XX.X%
  Duration: XXms (warm — cache hits, should be faster)
  Speedup:  Cache eliminates redundant LLM calls

Step 3: Optimize (BootstrapFewShot with cached client)
──────────────────────────────────────────────────────
  Baseline:  XX.X%
  Optimized: XX.X%
  Duration:  XXXXms
  (Many training examples reuse cached prompts from Step 1)
```

You'll also see interleaved **OpenTelemetry spans** (with activity names, durations, and token counts) and **ILogger lines** (with structured request/response data) for each LLM call.

## Key Takeaways

1. **Middleware is transparent.** Your `LmpModule` code doesn't change — caching, tracing, and logging are added at the `IChatClient` level.
2. **Pipeline order matters.** Inner middleware runs first; cache should be innermost so cache hits skip tracing/logging overhead.
3. **Caching pays for itself fast.** Evaluation and optimization repeat prompts. A warm cache turns multi-second LLM calls into sub-millisecond cache lookups.
4. **OpenTelemetry traces only real work.** Because cache is innermost, OTel spans only appear for actual LLM calls — giving you accurate latency data without noise from cache hits.
5. **Swap implementations without code changes.** Replace `MemoryDistributedCache` with Redis, replace the console exporter with Jaeger, replace console logging with Serilog — the module code stays identical.

## Next Steps

| Sample | What It Adds |
|---|---|
| [**Evaluation**](../LMP.Samples.Evaluation/) | Deep-dive into `Evaluator` and `Microsoft.Extensions.AI.Evaluation` quality metrics |
| [**AdvancedOptimizers**](../LMP.Samples.AdvancedOptimizers/) | Compare `BootstrapRandomSearch`, `SmacSampler`, and `TraceAnalyzer` strategies |
