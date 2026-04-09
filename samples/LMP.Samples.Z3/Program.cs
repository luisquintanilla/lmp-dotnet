using LMP;
using LMP.Extensions.Z3;
using LMP.Optimizers;
using LMP.Samples.Z3;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP Z3 — Constraint-Based Demo Selection
//
// This sample demonstrates Z3ConstrainedDemoSelector:
//   1. Optimize with BootstrapRandomSearch (random demo selection)
//   2. Optimize with Z3 (constrained demo selection)
//   3. Compare: random picks demos blindly, Z3 enforces category
//      coverage while maximizing quality
//
// Z3 guarantees structural properties that random search cannot:
//   • At least one demo per ticket category
//   • Exactly maxDemos selected (no more, no fewer)
//   • Highest quality demos within constraints
//   • Optional: minimize total token usage
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Z3 Constraint-Based Demo Selection           ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

IChatClient client = new MockChatClient();
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));

Func<DraftReply, DraftReply, float> metric = (prediction, label) =>
{
    float score = 0f;
    var categoryPhrases = new[] { "billing", "technical", "account", "security", "feature" };
    var expectedCategory = categoryPhrases.FirstOrDefault(c =>
        label.ReplyText.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? "";
    if (!string.IsNullOrEmpty(expectedCategory) &&
        prediction.ReplyText.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase))
        score += 0.4f;

    var keywords = ExtractKeywords(label.ReplyText);
    if (keywords.Length > 0)
    {
        var matchCount = keywords.Count(kw =>
            prediction.ReplyText.Contains(kw, StringComparison.OrdinalIgnoreCase));
        score += 0.4f * matchCount / keywords.Length;
    }

    if (prediction.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase) ||
        prediction.ReplyText.StartsWith("We sincerely", StringComparison.OrdinalIgnoreCase))
        score += 0.2f;

    return score;
};

var untypedMetric = Metric.Create(metric);

// ═══════════════════════════════════════════════════════════
// Step 1: Baseline
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 1: Baseline (no optimization)");
Console.WriteLine("───────────────────────────────────");

var baselineModule = new SupportTriageModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, metric);
Console.WriteLine($"  Score: {baseline.AverageScore:P1}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 2: BootstrapRandomSearch (random demo selection)
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 2: BootstrapRandomSearch (random demo selection)");
Console.WriteLine("─────────────────────────────────────────────────────");

var brsModule = new SupportTriageModule(client);
var brs = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, metricThreshold: 0.3f, seed: 42);
var brsOptimized = await brs.CompileAsync(brsModule, trainSet, untypedMetric);
var brsScore = await Evaluator.EvaluateAsync(brsOptimized, devSet, metric);

Console.WriteLine($"  Score: {brsScore.AverageScore:P1}");
Console.WriteLine("  Demos selected (random — no coverage guarantees):");
foreach (var (name, pred) in brsOptimized.GetPredictors())
{
    Console.WriteLine($"    [{name}] {pred.Demos.Count} demos");
}
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 3: Z3 Constrained Demo Selection
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 3: Z3ConstrainedDemoSelector");
Console.WriteLine("──────────────────────────────────");
Console.WriteLine("  Constraints:");
Console.WriteLine("    • Exactly 4 demos per predictor");
Console.WriteLine("    • At least 1 demo per ticket category");
Console.WriteLine("    • Maximize total quality score");
Console.WriteLine("    • Minimize token usage (secondary)");
Console.WriteLine();

// Category extractor: pull the category from the module's classification output
// In real usage, you'd extract from the demo's input/output structure
Func<object, string> categoryExtractor = input =>
{
    if (input is TicketInput ticket)
    {
        var text = ticket.TicketText.ToLowerInvariant();
        if (text.Contains("charg") || text.Contains("invoice") || text.Contains("bill")
            || text.Contains("payment") || text.Contains("refund"))
            return "billing";
        if (text.Contains("vpn") || text.Contains("api") || text.Contains("error")
            || text.Contains("crash") || text.Contains("503") || text.Contains("slow"))
            return "technical";
        if (text.Contains("account") || text.Contains("email") || text.Contains("password")
            || text.Contains("login"))
            return "account";
        if (text.Contains("secur") || text.Contains("breach") || text.Contains("unauthorized"))
            return "security";
        return "general";
    }
    return "unknown";
};

// Token counter: estimate tokens from input text length
Func<object, int> tokenCounter = input =>
{
    if (input is TicketInput ticket)
        return ticket.TicketText.Length / 4; // rough chars-to-tokens
    return 10;
};

var z3Module = new SupportTriageModule(client);
var z3Selector = new Z3ConstrainedDemoSelector(
    categoryExtractor: categoryExtractor,
    tokenCounter: tokenCounter,
    maxDemos: 4,
    metricThreshold: 0.3f);

var z3Optimized = await z3Selector.CompileAsync(z3Module, trainSet, untypedMetric);
var z3Score = await Evaluator.EvaluateAsync(z3Optimized, devSet, metric);

Console.WriteLine($"  Score: {z3Score.AverageScore:P1}");
Console.WriteLine("  Demos selected (Z3 — category coverage guaranteed):");
foreach (var (name, pred) in z3Optimized.GetPredictors())
{
    Console.WriteLine($"    [{name}] {pred.Demos.Count} demos");
}
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 4: Results Comparison
// ═══════════════════════════════════════════════════════════
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results Comparison                                 ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline (no opt):           {baseline.AverageScore,6:P1}              ║");
Console.WriteLine($"║   BootstrapRandomSearch:       {brsScore.AverageScore,6:P1}              ║");
Console.WriteLine($"║   Z3 Constrained:              {z3Score.AverageScore,6:P1}              ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   Z3 guarantees structural properties:               ║");
Console.WriteLine("║   · Category coverage (≥1 demo per category)         ║");
Console.WriteLine("║   · Exact cardinality (exactly maxDemos selected)    ║");
Console.WriteLine("║   · Quality maximization within constraints          ║");
Console.WriteLine("║   · Token minimization (secondary objective)         ║");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   Random search has no such guarantees — it may      ║");
Console.WriteLine("║   select all demos from the same category.           ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

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
            json = $$$"""{"reasoning":"Analyzing the ticket.","result":{"category":"{{{category}}}","urgency":{{{urgency}}}}}""";
        }
        else
        {
            var (category, urgency) = ClassifyFromText(userText);
            json = $$$"""{"category":"{{{category}}}","urgency":{{{urgency}}}}""";
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, json)));
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
            return ("billing", 3);
        if (lower.Contains("vpn") || lower.Contains("api") || lower.Contains("error")
            || lower.Contains("crash") || lower.Contains("503") || lower.Contains("slow"))
            return ("technical", 4);
        if (lower.Contains("account") || lower.Contains("email") || lower.Contains("password")
            || lower.Contains("login"))
            return ("account", 2);
        return ("general", 1);
    }

    private static string DraftFromContext(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("billing"))
            return "Thank you for reaching out about your billing concern. We have reviewed your account and will process a correction.";
        if (lower.Contains("technical"))
            return "Thank you for reporting this technical issue. Our engineering team is investigating.";
        if (lower.Contains("account"))
            return "Thank you for contacting us about your account. We can help you with that change.";
        return "Thank you for contacting support. We have received your request.";
    }
}
