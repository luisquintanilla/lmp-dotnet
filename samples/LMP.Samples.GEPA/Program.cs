using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using LMP.Samples.GEPA;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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
// Uses Azure OpenAI with DefaultAzureCredential (managed identity).
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
//   (Optional) dotnet user-secrets set "AzureOpenAI:ReflectionDeployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — GEPA Evolutionary Optimizer (Azure OpenAI)   ║");
Console.WriteLine("║   Reflection-Driven Instruction Evolution             ║");
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

// Use same deployment for both task and reflection by default.
// Set AzureOpenAI:ReflectionDeployment for a separate (cheaper) model.
string reflectionDeployment = config["AzureOpenAI:ReflectionDeployment"] ?? deployment;

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
IChatClient taskClient = azureClient.GetChatClient(deployment).AsIChatClient();
IChatClient reflectionClient = azureClient.GetChatClient(reflectionDeployment).AsIChatClient();

Console.WriteLine($"  Task model:       {deployment} @ {endpoint}");
Console.WriteLine($"  Reflection model: {reflectionDeployment} @ {endpoint}");
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

Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));

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

