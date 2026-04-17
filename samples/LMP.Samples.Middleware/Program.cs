using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using LMP.Samples.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

// ──────────────────────────────────────────────────────────────
// LMP Middleware Pipeline Demo (Azure OpenAI)
//
// Demonstrates M.E.AI middleware wrapping Azure OpenAI:
//   1. DistributedCache — cache identical LLM prompts
//   2. OpenTelemetry    — trace every LLM call with latency
//   3. ILogger          — structured logging for requests
//
// Shows the timing difference between uncached and cached runs.
//
// Configure via user secrets:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Middleware Pipeline Demo             ║");
Console.WriteLine("║          (Azure OpenAI)                      ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// ── Configure Azure OpenAI via user secrets + managed identity ──
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Endpoint in user secrets: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR_RESOURCE.openai.azure.com/\"");
string deployment = config["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Deployment in user secrets: dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4.1-nano\"");

Console.WriteLine($"  Using: {deployment} @ {endpoint}");
Console.WriteLine();

// ── Set up OpenTelemetry ────────────────────────────────────
string sourceName = "LMP.Middleware.Demo";

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(sourceName)
    .AddConsoleExporter()
    .Build();

// ── Set up logging ──────────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// ── Set up distributed cache ────────────────────────────────
var cache = new MemoryDistributedCache(
    Options.Create(new MemoryDistributedCacheOptions()));

// ── Build the middleware pipeline ───────────────────────────
//
// The pipeline wraps the Azure OpenAI client with middleware:
//
//   Azure OpenAI → Cache → OpenTelemetry → Logging → Outer
//
// Order matters:
//   - Cache is innermost: cache hits skip OTel + logging overhead
//   - OTel traces only actual LLM calls (cache misses)
//   - Logging is outermost: logs all requests (cached or not)

IChatClient innerClient = new AzureOpenAIClient(
        new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

IChatClient client = new ChatClientBuilder(innerClient)
    .UseDistributedCache(cache)
    .UseOpenTelemetry(sourceName: sourceName, configure: c =>
        c.EnableSensitiveData = true)
    .UseLogging(loggerFactory)
    .Build();

Console.WriteLine("Pipeline: Azure OpenAI → Cache → OpenTelemetry → Logging");
Console.WriteLine();

// ── Step 1: Evaluate with cold cache ────────────────────────
Console.WriteLine("Step 1: Evaluate (Cold Cache — all calls hit LLM)");
Console.WriteLine("──────────────────────────────────────────────────");

var module = new SupportTriageModule(client);
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));

Func<DraftReply, DraftReply, float> metric = (prediction, label) =>
{
    if (prediction is null) return 0f;
    var keywords = ExtractKeywords(label.ReplyText);
    var matchCount = keywords.Count(kw =>
        prediction.ReplyText.Contains(kw, StringComparison.OrdinalIgnoreCase));
    return keywords.Length > 0 ? (float)matchCount / keywords.Length : 0f;
};

var sw = Stopwatch.StartNew();
var coldResult = await Evaluator.EvaluateAsync(module, devSet, metric);
sw.Stop();

Console.WriteLine($"  Score:    {coldResult.AverageScore:P1}");
Console.WriteLine($"  Duration: {sw.ElapsedMilliseconds}ms (cold — all {coldResult.Count} examples hit LLM)");
Console.WriteLine();

// ── Step 2: Evaluate with warm cache ────────────────────────
Console.WriteLine("Step 2: Evaluate (Warm Cache — identical prompts are cached)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

// Create a fresh module (no state), same cache
var module2 = new SupportTriageModule(client);

sw.Restart();
var warmResult = await Evaluator.EvaluateAsync(module2, devSet, metric);
sw.Stop();

Console.WriteLine($"  Score:    {warmResult.AverageScore:P1}");
Console.WriteLine($"  Duration: {sw.ElapsedMilliseconds}ms (warm — cache hits, should be faster)");
Console.WriteLine($"  Speedup:  Cache eliminates redundant LLM calls");
Console.WriteLine();

// ── Step 3: Optimize with caching ───────────────────────────
Console.WriteLine("Step 3: Optimize (BootstrapFewShot with cached client)");
Console.WriteLine("──────────────────────────────────────────────────────");

var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));

sw.Restart();
var untypedMetric = Metric.Create(metric);
var optimizer = new BootstrapFewShot(maxDemos: 3, metricThreshold: 0.3f);
var optimized = await optimizer.CompileAsync(module, trainSet, untypedMetric);
sw.Stop();

var optimizedScore = await Evaluator.EvaluateAsync(optimized, devSet, metric);

Console.WriteLine($"  Baseline:  {coldResult.AverageScore:P1}");
Console.WriteLine($"  Optimized: {optimizedScore.AverageScore:P1}");
Console.WriteLine($"  Duration:  {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"  (Many training examples reuse cached prompts from Step 1)");
Console.WriteLine();

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   Key Takeaways                              ║");
Console.WriteLine("║                                              ║");
Console.WriteLine("║   • Cache: identical prompts → instant       ║");
Console.WriteLine("║   • OTel:  traces show latency per call      ║");
Console.WriteLine("║   • Logs:  structured request/response logs   ║");
Console.WriteLine("║   • Pipeline wraps Azure OpenAI seamlessly   ║");
Console.WriteLine("║   • Optimization reuses cached LLM responses ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

static string[] ExtractKeywords(string text)
{
    var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will",
        "would", "could", "should", "may", "might", "shall", "can",
        "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "as", "into", "through", "during", "before", "after", "we",
        "your", "you", "our", "this", "that", "it", "and", "or", "i"
    };

    return text
        .Split([' ', ',', '.', '!', '?', ';', ':', '-', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 2 && !stopWords.Contains(w))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
