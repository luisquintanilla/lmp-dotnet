using System.Diagnostics;
using LMP;
using LMP.Optimizers;
using LMP.Samples.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

// ──────────────────────────────────────────────────────────────
// LMP Middleware Pipeline Demo
//
// Demonstrates M.E.AI middleware with LMP modules:
//   1. DistributedCache — cache identical LLM prompts
//   2. OpenTelemetry    — trace every LLM call with latency
//   3. ILogger          — structured logging for requests
//
// Shows the timing difference between uncached and cached runs.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Middleware Pipeline Demo             ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
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
// The pipeline wraps any IChatClient (here a mock, but swap in
// Azure OpenAI, OpenAI, or Ollama for production):
//
//   Inner client → Cache → OpenTelemetry → Logging → Outer
//
// Order matters:
//   - Cache is innermost: cache hits skip OTel + logging
//   - OTel traces only actual LLM calls (cache misses)
//   - Logging is outermost: logs all requests (cached or not)

IChatClient innerClient = new MockChatClient();

IChatClient client = new ChatClientBuilder(innerClient)
    .UseDistributedCache(cache)
    .UseOpenTelemetry(sourceName: sourceName, configure: c =>
        c.EnableSensitiveData = true)
    .UseLogging(loggerFactory)
    .Build();

Console.WriteLine("Pipeline: MockChatClient → Cache → OpenTelemetry → Logging");
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
Console.WriteLine($"  Duration: {warmResult.Count}ms (warm — cache hits, should be faster)");
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
Console.WriteLine("║   • Swap MockChatClient for Azure OpenAI     ║");
Console.WriteLine("║     and the pipeline works identically        ║");
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

// ── Mock Chat Client ────────────────────────────────────────

file sealed class MockChatClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var systemText = messageList.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var userText = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        bool isCoT = systemText.Contains("step by step", StringComparison.OrdinalIgnoreCase);
        bool isDraft = userText.Contains("Category", StringComparison.Ordinal)
                    || userText.Contains("category", StringComparison.Ordinal)
                       && (userText.Contains("Urgency", StringComparison.Ordinal)
                           || userText.Contains("urgency", StringComparison.Ordinal));

        string json;
        if (isDraft && !isCoT)
        {
            var replyText = DraftFromContext(userText);
            json = $$$"""{"replyText":"{{{replyText}}}"}""";
        }
        else if (isCoT)
        {
            var (category, urgency) = ClassifyFromText(userText);
            json = $$$"""{"reasoning":"Analyzing ticket.","result":{"category":"{{{category}}}","urgency":{{{urgency}}}}}""";
        }
        else
        {
            var (category, urgency) = ClassifyFromText(userText);
            json = $$$"""{"category":"{{{category}}}","urgency":{{{urgency}}}}""";
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    private static (string Category, int Urgency) ClassifyFromText(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("charg") || lower.Contains("invoice") || lower.Contains("bill")
            || lower.Contains("payment") || lower.Contains("refund"))
            return ("billing", lower.Contains("production") ? 5 : 3);
        if (lower.Contains("vpn") || lower.Contains("api") || lower.Contains("error")
            || lower.Contains("crash") || lower.Contains("503") || lower.Contains("slow"))
            return ("technical", lower.Contains("production") ? 5 : 4);
        if (lower.Contains("account") || lower.Contains("email") || lower.Contains("password")
            || lower.Contains("login"))
            return ("account", 2);
        return ("general", 1);
    }

    private static string DraftFromContext(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("billing"))
            return "Thank you for reaching out about your billing concern. We have reviewed your account and will process a correction. Please allow 3-5 business days for the adjustment to appear.";
        if (lower.Contains("technical"))
            return "Thank you for reporting this technical issue. Our engineering team is investigating and we will provide an update shortly.";
        if (lower.Contains("account"))
            return "Thank you for contacting us about your account. For security, please verify your identity through the link we have sent to your registered email.";
        return "Thank you for contacting support. A team member will follow up within 24 hours.";
    }
}
