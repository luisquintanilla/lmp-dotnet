using LMP;
using LMP.Optimizers;
using LMP.Samples.MIPROv2;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP MIPROv2 — Bayesian Instruction + Demo Optimization
//
// This sample demonstrates the most advanced LMP optimizer:
//   1. Evaluate baseline (no optimization)
//   2. Optimize with BootstrapRandomSearch (demo-only, baseline)
//   3. Optimize with MIPROv2 (instructions + demos via Bayesian TPE)
//   4. Compare results and show instruction improvements
//
// MIPROv2 is unique: it optimizes BOTH instructions AND demos.
// It uses a proposal LM to generate instruction variants, then
// a Bayesian (TPE) search to find the best combination.
//
// To run with a real LLM, replace MockChatClient below.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — MIPROv2 Bayesian Optimization Demo          ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

IChatClient client = new MockChatClient();
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));

// Structured rubric metric (same as TicketTriage sample)
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
        prediction.ReplyText.StartsWith("We sincerely", StringComparison.OrdinalIgnoreCase) ||
        prediction.ReplyText.StartsWith("We apologize", StringComparison.OrdinalIgnoreCase))
        score += 0.2f;

    return score;
};

var untypedMetric = Metric.Create(metric);

// ── Step 1: Evaluate Baseline (no optimization) ─────────────
Console.WriteLine("Step 1: Evaluate Baseline (no optimization)");
Console.WriteLine("────────────────────────────────────────────");

var baselineModule = new SupportTriageModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, metric);

Console.WriteLine($"  Score: {baseline.AverageScore:P1} (avg), {baseline.MinScore:P1} (min), {baseline.MaxScore:P1} (max)");
Console.WriteLine();

// ── Step 2: BootstrapRandomSearch (demo-only optimization) ──
Console.WriteLine("Step 2: BootstrapRandomSearch (optimizes demos only)");
Console.WriteLine("────────────────────────────────────────────────────");

var brsModule = new SupportTriageModule(client);
var brs = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, metricThreshold: 0.3f, seed: 42);
var brsOptimized = await brs.CompileAsync(brsModule, trainSet, untypedMetric);
var brsScore = await Evaluator.EvaluateAsync(brsOptimized, devSet, metric);

Console.WriteLine($"  Score: {brsScore.AverageScore:P1}");
PrintPredictorState(brsOptimized, "  ");
Console.WriteLine();

// ── Step 3: Capture original instructions ───────────────────
Console.WriteLine("Step 3: Original Instructions (before MIPROv2)");
Console.WriteLine("───────────────────────────────────────────────");

var mipModule = new SupportTriageModule(client);
foreach (var (name, pred) in mipModule.GetPredictors())
{
    Console.WriteLine($"  [{name}] \"{pred.Instructions}\"");
}
Console.WriteLine();

// ── Step 4: MIPROv2 (instruction + demo optimization) ───────
Console.WriteLine("Step 4: MIPROv2 Bayesian Optimization");
Console.WriteLine("──────────────────────────────────────");
Console.WriteLine("  Phase 1: Bootstrap demo pool from training data");
Console.WriteLine("  Phase 2: Propose instruction variants via LM");
Console.WriteLine("  Phase 3: Bayesian TPE search over (instruction × demo-set)");
Console.WriteLine();

// MIPROv2 uses a proposal client (here the same mock, but can be
// a cheaper model like gpt-4o-mini for instruction generation)
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 4,
    metricThreshold: 0.3f,
    gamma: 0.25,
    seed: 42);

var mipOptimized = await mipro.CompileAsync(mipModule, trainSet, untypedMetric);
var mipScore = await Evaluator.EvaluateAsync(mipOptimized, devSet, metric);

Console.WriteLine($"  Score: {mipScore.AverageScore:P1}");
Console.WriteLine();

// ── Step 5: Compare Instructions After Optimization ─────────
Console.WriteLine("Step 5: Optimized Instructions (after MIPROv2)");
Console.WriteLine("───────────────────────────────────────────────");

foreach (var (name, pred) in mipOptimized.GetPredictors())
{
    Console.WriteLine($"  [{name}] \"{pred.Instructions}\"");
    Console.WriteLine($"          Demos: {pred.Demos.Count}");
}
Console.WriteLine();

// ── Step 6: Results Comparison ──────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results Comparison                                 ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline (no opt):         {baseline.AverageScore,6:P1}                ║");
Console.WriteLine($"║   BootstrapRandomSearch:     {brsScore.AverageScore,6:P1}                ║");
Console.WriteLine($"║   MIPROv2 (instr + demos):   {mipScore.AverageScore,6:P1}                ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   MIPROv2 optimizes BOTH instructions AND demos.     ║");
Console.WriteLine("║   BootstrapRandomSearch only optimizes demos.        ║");
Console.WriteLine("║   With a real LLM, instruction optimization can      ║");
Console.WriteLine("║   yield significant quality improvements.            ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Step 7: Save optimized module ───────────────────────────
var artifactPath = Path.Combine(Path.GetTempPath(), "mipro-optimized.json");
await mipOptimized.SaveAsync(artifactPath);
Console.WriteLine($"\nSaved optimized module to: {artifactPath}");
File.Delete(artifactPath);

// ── Helpers ─────────────────────────────────────────────────

static void PrintPredictorState(LmpModule module, string indent)
{
    foreach (var (name, pred) in module.GetPredictors())
    {
        Console.WriteLine($"{indent}[{name}] Demos: {pred.Demos.Count}, Instruction: \"{Truncate(pred.Instructions, 60)}\"");
    }
}

static string Truncate(string s, int maxLen) =>
    s.Length <= maxLen ? s : s[..maxLen] + "...";

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
// Handles both module predictions AND MIPROv2 instruction generation.

file sealed class MockChatClient : IChatClient
{
    private int _instructionVariant;

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

        // MIPROv2 instruction generation: proposal client asks for instruction variants
        if (systemText.Contains("expert prompt engineer", StringComparison.OrdinalIgnoreCase))
        {
            var variant = Interlocked.Increment(ref _instructionVariant);
            string instruction = GenerateInstructionVariant(userText, variant);
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, instruction)));
        }

        // Regular module prediction
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

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    private static string GenerateInstructionVariant(string userText, int variant)
    {
        // Generate realistic instruction variants based on the predictor name
        if (userText.Contains("classify", StringComparison.OrdinalIgnoreCase))
        {
            return variant switch
            {
                1 => "Analyze the support ticket and determine its category (billing, technical, account, security, feature-request) and urgency level from 1 to 5.",
                2 => "Read the customer's ticket carefully. Categorize it into one of: billing, technical, account, security, or feature-request. Rate urgency 1 (low) to 5 (critical).",
                3 => "You are a support triage specialist. Given a ticket, output the category and urgency score. Focus on accuracy.",
                _ => "Examine the incoming support request. Identify the primary concern category and assign an urgency rating reflecting business impact."
            };
        }

        if (userText.Contains("draft", StringComparison.OrdinalIgnoreCase))
        {
            return variant switch
            {
                1 => "Compose a professional, empathetic customer support reply addressing the classified ticket. Be specific about next steps.",
                2 => "Write a helpful response to the customer based on their ticket classification. Include expected resolution timeline and maintain a warm, professional tone.",
                3 => "Draft a concise, actionable reply for the support agent to send. Reference the ticket category and provide clear guidance.",
                _ => "Generate a customer-facing response that acknowledges the issue, explains the resolution process, and sets expectations for follow-up."
            };
        }

        return $"Process the input and produce the expected output. Variant {variant}.";
    }

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
