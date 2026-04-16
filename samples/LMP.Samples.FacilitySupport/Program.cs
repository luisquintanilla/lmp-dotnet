using LMP;
using LMP.Optimizers;
using LMP.Samples.FacilitySupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;

// ──────────────────────────────────────────────────────────────
// LMP FacilitySupport — GEPA Evolutionary Optimization
//
// This benchmark shows GEPA's evolutionary optimization on a
// real enterprise multi-task problem from Meta:
//
//   1. Dataset: FacilitySupportAnalyzer (Meta, public)
//   2. Module:  3 parallel predictors (urgency, sentiment, categories)
//   3. Metric:  Combined accuracy (arithmetic mean of 3 sub-tasks)
//   4. Baseline: ~75% combined accuracy
//   5. GEPA: ~85-87% (+10-12pp)
//
// GEPA's superpower: it reflects on FAILURES. When a sub-task
// gets it wrong, the reflection LLM diagnoses WHY and proposes
// a targeted instruction mutation. Each predictor evolves its
// own instructions independently.
//
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
//
// Data setup: See README.md for dataset download instructions.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Facility Support Benchmark                   ║");
Console.WriteLine("║   GEPA Evolutionary Optimization (3 sub-tasks)        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Azure OpenAI Setup ──────────────────────────────────────
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Endpoint in user secrets: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR.openai.azure.com/\"");
string deployment = config["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException(
        "Set AzureOpenAI:Deployment in user secrets: dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4o-mini\"");

IChatClient client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

Console.WriteLine($"  Using: {endpoint}");
Console.WriteLine($"  Deployment: {deployment}");
Console.WriteLine();

// ── Load Dataset ────────────────────────────────────────────
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

if (!Directory.Exists(dataDir) ||
    !File.Exists(Path.Combine(dataDir, "train.jsonl")) ||
    !File.Exists(Path.Combine(dataDir, "dev.jsonl")))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  ERROR: Dataset not found!");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("  This sample requires the FacilitySupportAnalyzer dataset.");
    Console.WriteLine("  See README.md for download and conversion instructions.");
    Console.WriteLine();
    Console.WriteLine("  Expected files:");
    Console.WriteLine($"    {Path.Combine(dataDir, "train.jsonl")}");
    Console.WriteLine($"    {Path.Combine(dataDir, "dev.jsonl")}");
    return;
}

var trainSet = Example.LoadFromJsonl<SupportInput, AnalysisResult>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<SupportInput, AnalysisResult>(
    Path.Combine(dataDir, "dev.jsonl"));

Console.WriteLine($"  Loaded {trainSet.Count} training examples, {devSet.Count} dev examples");
Console.WriteLine();

// ── Combined Multi-Task Metric ──────────────────────────────
// Score = average of 3 sub-task accuracies (each 0 or 1)
Func<AnalysisResult, AnalysisResult, float> combinedMetric = (predicted, expected) =>
{
    float urgencyScore = predicted.Urgency == expected.Urgency ? 1f : 0f;
    float sentimentScore = predicted.Sentiment == expected.Sentiment ? 1f : 0f;
    float categoryScore = predicted.PrimaryCategory == expected.PrimaryCategory ? 1f : 0f;
    return (urgencyScore + sentimentScore + categoryScore) / 3f;
};

var untypedMetric = Metric.Create(combinedMetric);

// ── Step 1: Evaluate Baseline ───────────────────────────────
Console.WriteLine("Step 1: Baseline Evaluation");
Console.WriteLine("───────────────────────────");

var baselineModule = new FacilitySupportModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, combinedMetric);

Console.WriteLine($"  Combined Score: {baseline.AverageScore:P1}");
PrintSubTaskScores(baseline, devSet);
Console.WriteLine();

// ── Step 2: Show Original Instructions ──────────────────────
Console.WriteLine("Step 2: Original Instructions (before GEPA)");
Console.WriteLine("─────────────────────────────────────────────");

var gepaModule = new FacilitySupportModule(client);
foreach (var (name, pred) in gepaModule.GetPredictors())
{
    Console.WriteLine($"  [{name}] \"{pred.Instructions}\"");
}
Console.WriteLine();

// ── Step 3: GEPA Optimization ───────────────────────────────
Console.WriteLine("Step 3: GEPA Evolutionary Optimization");
Console.WriteLine("──────────────────────────────────────");
Console.WriteLine("  GEPA loop:");
Console.WriteLine("    1. Evaluate candidate on mini-batch (5 examples)");
Console.WriteLine("    2. Identify failures (score < threshold)");
Console.WriteLine("    3. Reflection LLM diagnoses WHY each sub-task failed");
Console.WriteLine("    4. Proposes targeted instruction mutation per predictor");
Console.WriteLine("    5. Mutated candidate enters Pareto frontier");
Console.WriteLine("    6. Merge (crossover) Pareto-optimal parents every 5 iters");
Console.WriteLine();

var gepaProgress = new Progress<GEPAProgressReport>(r =>
{
    string status = r.IterationType == GEPAIterationType.Merge
        ? "MERGE"
        : r.Passed == true ? "PASS " : "skip ";
    Console.WriteLine($"  iter {r.Iteration,2}/{r.TotalIterations} [{status}]  frontier={r.FrontierSize,2}  best={r.BestScore:P1}");
});

var gepa = new GEPA(
    reflectionClient: client,
    maxIterations: 30,
    miniBatchSize: 5,
    mergeEvery: 5,
    seed: 42,
    progress: gepaProgress);

var gepaOptimized = await gepa.CompileAsync(gepaModule, trainSet, untypedMetric);
var gepaScore = await Evaluator.EvaluateAsync(gepaOptimized, devSet, combinedMetric);

Console.WriteLine();
Console.WriteLine($"  Combined Score: {gepaScore.AverageScore:P1}");
PrintSubTaskScores(gepaScore, devSet);
Console.WriteLine();

// ── Step 4: Show Evolved Instructions ───────────────────────
Console.WriteLine("Step 4: Evolved Instructions (after GEPA)");
Console.WriteLine("───────────────────────────────────────────");

foreach (var (name, pred) in gepaOptimized.GetPredictors())
{
    Console.WriteLine($"  [{name}]");
    Console.WriteLine($"    Instruction ({pred.Instructions.Length} chars): \"{pred.Instructions}\"");
    Console.WriteLine($"    Demos: {pred.Demos.Count}");
}
Console.WriteLine();

// ── Step 5: Results Comparison ──────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results — FacilitySupportAnalyzer                   ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline (no opt):     {baseline.AverageScore,6:P1}                    ║");
Console.WriteLine($"║   GEPA (evolutionary):   {gepaScore.AverageScore,6:P1}                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   GEPA reflects on failures and evolves targeted     ║");
Console.WriteLine("║   instructions per sub-task. Each predictor gets     ║");
Console.WriteLine("║   its own optimized instruction.                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Save ────────────────────────────────────────────────────
var artifactPath = Path.Combine(Path.GetTempPath(), "facility-support-optimized.json");
await gepaOptimized.SaveStateAsync(artifactPath);
Console.WriteLine($"\nSaved optimized module to: {artifactPath}");
File.Delete(artifactPath);

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

/// <summary>
/// Prints per-sub-task accuracy breakdown from the combined metric results.
/// </summary>
static void PrintSubTaskScores(EvaluationResult result, IReadOnlyList<Example> devSet)
{
    int urgencyCorrect = 0, sentimentCorrect = 0, categoryCorrect = 0, validCount = 0;
    foreach (var r in result.PerExample)
    {
        if (r.Output is not AnalysisResult predicted) continue;
        validCount++;
        var expected = (AnalysisResult)r.Example.GetLabel();
        if (predicted.Urgency == expected.Urgency) urgencyCorrect++;
        if (predicted.Sentiment == expected.Sentiment) sentimentCorrect++;
        if (predicted.PrimaryCategory == expected.PrimaryCategory) categoryCorrect++;
    }
    int total = result.PerExample.Count;
    int failed = total - validCount;
    Console.WriteLine($"    Urgency:    {urgencyCorrect}/{total} ({(float)urgencyCorrect / total:P1})");
    Console.WriteLine($"    Sentiment:  {sentimentCorrect}/{total} ({(float)sentimentCorrect / total:P1})");
    Console.WriteLine($"    Category:   {categoryCorrect}/{total} ({(float)categoryCorrect / total:P1})");
    if (failed > 0) Console.WriteLine($"    ({failed} examples failed — LLM returned invalid output)");
}
