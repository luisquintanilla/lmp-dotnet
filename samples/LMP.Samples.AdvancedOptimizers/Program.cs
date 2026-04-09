using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using LMP.Samples.AdvancedOptimizers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// ──────────────────────────────────────────────────────────────
// LMP Advanced Optimizers — ISampler, SmacSampler, TraceAnalyzer
//
// This sample demonstrates:
//   1. Optimize with MIPROv2 (default TPE sampler) — collect trial history
//   2. Analyze trial history with TraceAnalyzer — posteriors, interactions
//   3. Re-optimize with SmacSampler (SMAC/RF) — different search strategy
//   4. Warm-start a new optimization from prior posteriors
//   5. Compare all strategies side-by-side
//
// Uses Azure OpenAI with DefaultAzureCredential (managed identity).
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Advanced Optimizers Demo (Azure OpenAI)      ║");
Console.WriteLine("║   ISampler · SmacSampler · TraceAnalyzer              ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Azure OpenAI Setup ──────────────────────────────────────
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Endpoint in user secrets: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR_RESOURCE.openai.azure.com/\"");
string deployment = config["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Deployment in user secrets: dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4.1-nano\"");

IChatClient client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

Console.WriteLine($"  Using: {deployment} @ {endpoint}");
Console.WriteLine();

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
        prediction.ReplyText.StartsWith("We sincerely", StringComparison.OrdinalIgnoreCase) ||
        prediction.ReplyText.StartsWith("We apologize", StringComparison.OrdinalIgnoreCase))
        score += 0.2f;

    return score;
};

var untypedMetric = Metric.Create(metric);

// ═══════════════════════════════════════════════════════════
// Step 1: Baseline Evaluation
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 1: Baseline Evaluation (no optimization)");
Console.WriteLine("──────────────────────────────────────────────");

var baselineModule = new SupportTriageModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, metric);
Console.WriteLine($"  Score: {baseline.AverageScore:P1}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 2: MIPROv2 with Default TPE Sampler + History Export
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 2: MIPROv2 + TPE Sampler (default)");
Console.WriteLine("─────────────────────────────────────────");
Console.WriteLine("  Bayesian TPE search over (instruction × demo-set)...");

var tpeModule = new SupportTriageModule(client);
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 4,
    metricThreshold: 0.3f,
    seed: 42);

var tpeOptimized = await mipro.CompileAsync(tpeModule, trainSet, untypedMetric);
var tpeScore = await Evaluator.EvaluateAsync(tpeOptimized, devSet, metric);
Console.WriteLine($"  Score: {tpeScore.AverageScore:P1}");

// Retrieve trial history for analysis
var tpeHistory = mipro.LastTrialHistory;
Console.WriteLine($"  Trials collected: {tpeHistory?.Count ?? 0}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 3: Analyze Trials with TraceAnalyzer
// ═══════════════════════════════════════════════════════════
if (tpeHistory is { Count: > 0 })
{
    Console.WriteLine("Step 3: TraceAnalyzer — Post-Optimization Analysis");
    Console.WriteLine("───────────────────────────────────────────────────");

    // Build cardinalities (same search space as MIPROv2)
    var cardinalities = new Dictionary<string, int>
    {
        ["classify_instr"] = 4,
        ["classify_demos"] = 4,
        ["draft_instr"] = 4,
        ["draft_demos"] = 4
    };

    // 3a: Parameter Posteriors — confidence per parameter value
    Console.WriteLine("  3a) Parameter Posteriors (mean score ± stderr per choice):");
    var posteriors = TraceAnalyzer.ComputePosteriors(tpeHistory, cardinalities);
    foreach (var (param, values) in posteriors)
    {
        Console.WriteLine($"    [{param}]");
        foreach (var (valueIdx, post) in values.OrderByDescending(kv => kv.Value.Mean))
        {
            Console.WriteLine($"      Choice {valueIdx}: {post.Mean:F3} ± {post.StandardError:F3}  (n={post.Count})");
        }
    }
    Console.WriteLine();

    // 3b: Interaction Detection — ANOVA residual analysis
    Console.WriteLine("  3b) Parameter Interactions (ANOVA residual analysis):");
    var interactions = TraceAnalyzer.DetectInteractions(tpeHistory);
    foreach (var ((p1, p2), strength) in interactions.OrderByDescending(kv => kv.Value).Take(5))
    {
        var label = strength > 0.01 ? "⚡ Strong" : strength > 0.001 ? "~ Moderate" : "· Weak";
        Console.WriteLine($"    {label}: {p1} × {p2} = {strength:F4}");
    }
    Console.WriteLine();
}

// ═══════════════════════════════════════════════════════════
// Step 4: MIPROv2 with SmacSampler (SMAC/Random Forest)
// ═══════════════════════════════════════════════════════════
Console.WriteLine("Step 4: MIPROv2 + SmacSampler (SMAC/RF)");
Console.WriteLine("─────────────────────────────────────────");
Console.WriteLine("  Random Forest surrogate + Expected Improvement...");

var smacModule = new SupportTriageModule(client);
var miproSmac = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 4,
    metricThreshold: 0.3f,
    seed: 42,
    samplerFactory: cards => new SmacSampler(cards, numTrees: 10, seed: 42));

var smacOptimized = await miproSmac.CompileAsync(smacModule, trainSet, untypedMetric);
var smacScore = await Evaluator.EvaluateAsync(smacOptimized, devSet, metric);
Console.WriteLine($"  Score: {smacScore.AverageScore:P1}");

var smacHistory = miproSmac.LastTrialHistory;
Console.WriteLine($"  Trials collected: {smacHistory?.Count ?? 0}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════
// Step 5: Warm-Start — Transfer Knowledge to New Optimization
// ═══════════════════════════════════════════════════════════
if (tpeHistory is { Count: > 0 })
{
    Console.WriteLine("Step 5: Warm-Start Transfer Learning");
    Console.WriteLine("─────────────────────────────────────");
    Console.WriteLine("  Transferring TPE posteriors → new SmacSampler...");

    var cardinalities = new Dictionary<string, int>
    {
        ["classify_instr"] = 4,
        ["classify_demos"] = 4,
        ["draft_instr"] = 4,
        ["draft_demos"] = 4
    };

    // Create a new SmacSampler and warm-start it with TPE posteriors
    var warmSampler = new SmacSampler(cardinalities, numTrees: 10, seed: 123);
    var posteriors = TraceAnalyzer.ComputePosteriors(tpeHistory, cardinalities);
    TraceAnalyzer.WarmStart(warmSampler, posteriors, numSyntheticTrials: 5);

    Console.WriteLine($"  Warm-started sampler now has {warmSampler.TrialCount} synthetic trials");
    Console.WriteLine("  These guide early exploration toward promising regions.");

    // Run a new MIPROv2 with the warm-started sampler
    var warmModule = new SupportTriageModule(client);
    var miproWarm = new MIPROv2(
        proposalClient: client,
        numTrials: 10,
        numInstructionCandidates: 4,
        numDemoSubsets: 4,
        maxDemos: 4,
        metricThreshold: 0.3f,
        seed: 123,
        samplerFactory: _ => warmSampler);

    var warmOptimized = await miproWarm.CompileAsync(warmModule, trainSet, untypedMetric);
    var warmScore = await Evaluator.EvaluateAsync(warmOptimized, devSet, metric);
    Console.WriteLine($"  Score: {warmScore.AverageScore:P1}");
    Console.WriteLine();

    // ═══════════════════════════════════════════════════════════
    // Step 6: Results Comparison
    // ═══════════════════════════════════════════════════════════
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║   Results Comparison                                 ║");
    Console.WriteLine("╠══════════════════════════════════════════════════════╣");
    Console.WriteLine($"║   Baseline (no opt):           {baseline.AverageScore,6:P1}              ║");
    Console.WriteLine($"║   MIPROv2 + TPE:               {tpeScore.AverageScore,6:P1}              ║");
    Console.WriteLine($"║   MIPROv2 + SmacSampler:       {smacScore.AverageScore,6:P1}              ║");
    Console.WriteLine($"║   MIPROv2 + Warm-Start SMAC:   {warmScore.AverageScore,6:P1}              ║");
    Console.WriteLine("╠══════════════════════════════════════════════════════╣");
    Console.WriteLine("║                                                      ║");
    Console.WriteLine("║   ISampler lets you swap search strategies:           ║");
    Console.WriteLine("║   · TPE — fast, good for independent params           ║");
    Console.WriteLine("║   · SMAC — RF surrogate, better for interactions      ║");
    Console.WriteLine("║   TraceAnalyzer enables cross-run transfer learning.  ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
}

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

