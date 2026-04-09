using LMP;
using LMP.Optimizers;
using LMP.Samples.MIPROv2;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;

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
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — MIPROv2 Bayesian Optimization Demo          ║");
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
        "Set AzureOpenAI:Deployment in user secrets: dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4o-mini\"");

// Both the module client and MIPROv2's proposal client use the same Azure OpenAI deployment.
// For cost optimization, you could use a cheaper deployment for proposalClient (instruction generation).
IChatClient client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

Console.WriteLine($"  Using: {endpoint}");
Console.WriteLine($"  Deployment: {deployment}");
Console.WriteLine();
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

// MIPROv2 uses a proposal client for instruction generation.
// Here we use the same Azure OpenAI client, but you could use a
// cheaper deployment (e.g., gpt-4.1-nano) for the proposal client.
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

// ── Mock Chat Client removed — using Azure OpenAI ───────────
