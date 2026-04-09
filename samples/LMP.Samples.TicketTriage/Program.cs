using LMP;
using LMP.Optimizers;
using LMP.Samples.TicketTriage;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP Ticket Triage — End-to-End Sample
//
// This sample demonstrates the full LMP workflow:
//   1. Define typed input/output records
//   2. Create predictors and compose them into a module
//   3. Evaluate baseline accuracy on a dev set
//   4. Optimize with BootstrapFewShot
//   5. Save and load optimized parameters
//
// To run with a real LLM provider, replace the IChatClient below
// with your configured OpenAI, Azure OpenAI, or Ollama client.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Ticket Triage Demo                  ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// ── Configure your IChatClient ──────────────────────────────
// Replace this with your actual IChatClient provider:
//
//   using Microsoft.Extensions.AI;
//   IChatClient client = new OpenAIChatClient("gpt-4o-mini");
//
// For this demo, we use a mock client that returns deterministic
// responses, so you can run it without an API key.
IChatClient client = new MockChatClient();

// ── Step 1: Single prediction ───────────────────────────────
Console.WriteLine("Step 1: Single Prediction");
Console.WriteLine("─────────────────────────");

var classifier = new Predictor<TicketInput, ClassifyTicket>(client);
var result = await classifier.PredictAsync(
    new TicketInput("I was charged twice on my last invoice"));

Console.WriteLine($"  Category: {result.Category}");
Console.WriteLine($"  Urgency:  {result.Urgency}");
Console.WriteLine();

// ── Step 2: Chain of Thought ────────────────────────────────
Console.WriteLine("Step 2: Chain of Thought");
Console.WriteLine("────────────────────────");

var cotClassifier = new ChainOfThought<TicketInput, ClassifyTicket>(client);
var cotResult = await cotClassifier.PredictAsync(
    new TicketInput("I was charged twice on my last invoice"));

Console.WriteLine($"  Category: {cotResult.Category}");
Console.WriteLine($"  Urgency:  {cotResult.Urgency}");
Console.WriteLine();

// ── Step 3: Module composition ──────────────────────────────
Console.WriteLine("Step 3: Module Composition (SupportTriageModule)");
Console.WriteLine("────────────────────────────────────────────────");

var module = new SupportTriageModule(client);
var reply = await module.ForwardAsync(
    new TicketInput("Production API returning 503 for all customers"));

Console.WriteLine($"  Reply: {reply.ReplyText}");
Console.WriteLine();

// ── Step 4: Evaluate baseline ───────────────────────────────
Console.WriteLine("Step 4: Evaluate Baseline");
Console.WriteLine("─────────────────────────");

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));

Func<Example, object, float> metric = (example, output) =>
{
    var label = (DraftReply)example.GetLabel();
    var prediction = (DraftReply)output;

    // Simple metric: does the reply mention the expected category keyword?
    var keywords = ExtractKeywords(label.ReplyText);
    var matchCount = keywords.Count(kw =>
        prediction.ReplyText.Contains(kw, StringComparison.OrdinalIgnoreCase));

    return keywords.Length > 0 ? (float)matchCount / keywords.Length : 0f;
};

var baseline = await Evaluator.EvaluateAsync(module, devSet, metric);

Console.WriteLine($"  Examples:      {baseline.Count}");
Console.WriteLine($"  Average score: {baseline.AverageScore:P1}");
Console.WriteLine($"  Min score:     {baseline.MinScore:P1}");
Console.WriteLine($"  Max score:     {baseline.MaxScore:P1}");
Console.WriteLine();

// ── Step 5: Optimize with BootstrapFewShot ──────────────────
Console.WriteLine("Step 5: Optimize with BootstrapFewShot");
Console.WriteLine("──────────────────────────────────────");

var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));

var optimizer = new BootstrapFewShot(maxDemos: 3, metricThreshold: 0.3f);
var optimized = await optimizer.CompileAsync(module, trainSet, metric);

var optimizedScore = await Evaluator.EvaluateAsync(optimized, devSet, metric);

Console.WriteLine($"  Optimized average score: {optimizedScore.AverageScore:P1}");
Console.WriteLine($"  Demos in classify: {optimized.GetPredictors().First(p => p.Name == "classify").Predictor.Demos.Count}");
Console.WriteLine($"  Demos in draft:    {optimized.GetPredictors().First(p => p.Name == "draft").Predictor.Demos.Count}");
Console.WriteLine();

// ── Step 6: Save and Load ───────────────────────────────────
Console.WriteLine("Step 6: Save & Load");
Console.WriteLine("────────────────────");

var artifactPath = Path.Combine(Path.GetTempPath(), "triage-optimized.json");
await optimized.SaveAsync(artifactPath);
Console.WriteLine($"  Saved to: {artifactPath}");

var loaded = new SupportTriageModule(client);
await loaded.LoadAsync(artifactPath);

var loadedScore = await Evaluator.EvaluateAsync(loaded, devSet, metric);
Console.WriteLine($"  Loaded module score: {loadedScore.AverageScore:P1}");
Console.WriteLine();

// Clean up
File.Delete(artifactPath);

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                             ║");
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

/// <summary>
/// A deterministic mock chat client that returns canned responses
/// so the demo runs without an API key. Replace with a real
/// <see cref="IChatClient"/> for production use.
/// </summary>
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

        // Detect call type from message content
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
            json = $$$"""{"reasoning":"Analyzing the ticket to determine category and urgency level.","result":{"category":"{{{category}}}","urgency":{{{urgency}}}}}""";
        }
        else
        {
            var (category, urgency) = ClassifyFromText(userText);
            json = $$$"""{"category":"{{{category}}}","urgency":{{{urgency}}}}""";
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not supported in mock.");

    private static (string Category, int Urgency) ClassifyFromText(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("charg") || lower.Contains("invoice") || lower.Contains("bill")
            || lower.Contains("payment") || lower.Contains("refund") || lower.Contains("price"))
            return ("billing", lower.Contains("critical") || lower.Contains("production") ? 5 : 3);

        if (lower.Contains("vpn") || lower.Contains("api") || lower.Contains("error")
            || lower.Contains("crash") || lower.Contains("503") || lower.Contains("slow")
            || lower.Contains("connect") || lower.Contains("bug") || lower.Contains("install"))
            return ("technical", lower.Contains("all customer") || lower.Contains("production") ? 5 : 4);

        if (lower.Contains("account") || lower.Contains("email") || lower.Contains("password")
            || lower.Contains("login") || lower.Contains("reset") || lower.Contains("profile"))
            return ("account", 2);

        return ("general", 1);
    }

    private static string DraftFromContext(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("billing"))
            return "Thank you for reaching out about your billing concern. We have reviewed your account and will process a correction. Please allow 3-5 business days for the adjustment to appear.";

        if (lower.Contains("technical"))
            return "Thank you for reporting this technical issue. Our engineering team is investigating and we will provide an update shortly. Please check our status page for real-time updates.";

        if (lower.Contains("account"))
            return "Thank you for contacting us about your account. We can help you with that change. For security, please verify your identity through the link we have sent to your registered email.";

        return "Thank you for contacting support. We have received your request and a team member will follow up with you within 24 hours.";
    }
}
