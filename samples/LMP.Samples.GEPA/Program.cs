using LMP;
using LMP.Optimizers;
using LMP.Samples.GEPA;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP GEPA — Evolutionary Reflection-Driven Optimization
//
// This sample demonstrates the GEPA optimizer:
//   1. Evaluate baseline (no optimization)
//   2. Capture original instructions
//   3. Run GEPA: evolve instructions via LLM reflection on failures
//   4. Compare instructions before/after — GEPA rewrites them
//
// GEPA is fundamentally different from MIPROv2:
//   • MIPROv2 (Bayesian): "Config scored 0.45. Try a different config."
//   • GEPA (Evolutionary): "Config scored 0.45. The classify predictor
//     output 'urgent' for a routine ticket. Fix: be more conservative."
//
// With a mock client, the reflection loop returns canned instructions.
// With a real LLM (see Azure sample), GEPA produces targeted fixes.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — GEPA Evolutionary Optimizer                  ║");
Console.WriteLine("║   Reflection-Driven Instruction Evolution             ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

IChatClient taskClient = new MockTaskClient();
IChatClient reflectionClient = new MockReflectionClient();

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
Console.WriteLine("Step 1: Baseline Evaluation");
Console.WriteLine("───────────────────────────");

var baselineModule = new SupportTriageModule(taskClient);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, metric);
Console.WriteLine($"  Score: {baseline.AverageScore:P1}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 2: Original Instructions
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 2: Original Instructions (before GEPA)");
Console.WriteLine("─────────────────────────────────────────────");

var gepaModule = new SupportTriageModule(taskClient);
foreach (var (name, pred) in gepaModule.GetPredictors())
{
    Console.WriteLine($"  [{name}] \"{Truncate(pred.Instructions, 80)}\"");
}
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 3: GEPA Optimization
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 3: GEPA Evolutionary Optimization");
Console.WriteLine("──────────────────────────────────────");
Console.WriteLine("  GEPA loop:");
Console.WriteLine("    1. Evaluate candidate on mini-batch");
Console.WriteLine("    2. Identify failures (score < 0.8)");
Console.WriteLine("    3. Send failure traces to reflection LLM");
Console.WriteLine("    4. LLM diagnoses WHY it failed → proposes new instruction");
Console.WriteLine("    5. Mutated candidate enters Pareto frontier");
Console.WriteLine("    6. Periodically merge (crossover) Pareto-optimal parents");
Console.WriteLine();

var gepa = new LMP.Optimizers.GEPA(
    reflectionClient: reflectionClient,
    maxIterations: 20,
    miniBatchSize: 5,
    mergeEvery: 5,
    seed: 42);

var gepaOptimized = await gepa.CompileAsync(gepaModule, trainSet, untypedMetric);
var gepaScore = await Evaluator.EvaluateAsync(gepaOptimized, devSet, metric);
Console.WriteLine($"  Score: {gepaScore.AverageScore:P1}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 4: Evolved Instructions
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 4: Evolved Instructions (after GEPA)");
Console.WriteLine("──────────────────────────────────────────");

foreach (var (name, pred) in gepaOptimized.GetPredictors())
{
    Console.WriteLine($"  [{name}] \"{Truncate(pred.Instructions, 80)}\"");
}
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 5: Compare with MIPROv2
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 5: MIPROv2 for Comparison (Bayesian — not evolutionary)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

var mipModule = new SupportTriageModule(taskClient);
var mipro = new MIPROv2(
    proposalClient: taskClient,
    numTrials: 10,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 4,
    metricThreshold: 0.3f,
    seed: 42);
var mipOptimized = await mipro.CompileAsync(mipModule, trainSet, untypedMetric);
var mipScore = await Evaluator.EvaluateAsync(mipOptimized, devSet, metric);
Console.WriteLine($"  Score: {mipScore.AverageScore:P1}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 6: Results Comparison
// ═══════════════════════════════════════════════════════════
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results Comparison                                 ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline:              {baseline.AverageScore,6:P1}                    ║");
Console.WriteLine($"║   GEPA (evolutionary):   {gepaScore.AverageScore,6:P1}                    ║");
Console.WriteLine($"║   MIPROv2 (Bayesian):    {mipScore.AverageScore,6:P1}                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   GEPA evolves INSTRUCTIONS via LLM reflection:      ║");
Console.WriteLine("║   · Captures execution traces (why it failed)        ║");
Console.WriteLine("║   · LLM diagnoses failures → proposes fixes          ║");
Console.WriteLine("║   · Pareto frontier tracks non-dominated candidates  ║");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   MIPROv2 searches over INSTRUCTION + DEMO combos:   ║");
Console.WriteLine("║   · TPE/SMAC guides search based on scores           ║");
Console.WriteLine("║   · No failure diagnosis — just score patterns        ║");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   Best strategy: GEPA for instructions, then         ║");
Console.WriteLine("║   MIPROv2 for instruction+demo joint optimization.   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

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

// ── Mock Task Client ────────────────────────────────────────

file sealed class MockTaskClient : IChatClient
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

        // MIPROv2 instruction proposal
        if (systemText.Contains("expert prompt engineer", StringComparison.OrdinalIgnoreCase))
        {
            var variant = Interlocked.Increment(ref _instructionVariant);
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    $"Analyze the support ticket carefully and produce accurate results. Variant {variant}.")));
        }

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

// ── Mock Reflection Client ──────────────────────────────────
// Simulates the reflection LLM that diagnoses failures and proposes
// improved instructions. With a real LLM, this produces targeted fixes.

file sealed class MockReflectionClient : IChatClient
{
    private int _callCount;

    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var idx = Interlocked.Increment(ref _callCount);
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // Simulate increasingly refined instructions based on failure analysis
        string instruction;
        if (userText.Contains("classify", StringComparison.OrdinalIgnoreCase))
        {
            instruction = idx switch
            {
                <= 3 => "Carefully read the support ticket. Identify the primary category (billing, technical, account, security, or general) based on keywords. Rate urgency 1-5 where 5 means service outage or data loss.",
                <= 6 => "You are a senior support triage agent. Classify tickets by category and urgency. For urgency: 1=informational, 2=minor, 3=standard, 4=high-impact, 5=critical outage. Look for severity indicators.",
                _ => "Expert ticket classifier. Categories: billing (charges, invoices, refunds), technical (errors, crashes, connectivity), account (login, profile, email), security (breaches, unauthorized access), general (other). Urgency: match to business impact."
            };
        }
        else
        {
            instruction = idx switch
            {
                <= 3 => "Draft a professional, empathetic reply. Acknowledge the issue, state next steps clearly, and set timeline expectations. Start with a thank you.",
                <= 6 => "Write a customer support reply that: 1) acknowledges their frustration, 2) confirms the issue category, 3) explains what we are doing about it, 4) provides a timeline. Keep it concise.",
                _ => "Compose a warm, actionable support response. Reference the specific issue. Include concrete next steps and expected resolution time. End with reassurance."
            };
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, instruction)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
