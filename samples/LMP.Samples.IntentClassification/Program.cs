using LMP;
using LMP.Optimizers;
using LMP.Samples.IntentClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;

// ──────────────────────────────────────────────────────────────
// LMP IntentClassification — BootstrapRandomSearch on Banking77
//
// This benchmark proves that few-shot demo selection delivers
// massive improvement on high-cardinality classification:
//
//   1. Dataset: Banking77 (PolyAI, CC-BY-4.0) — 77 intent classes
//   2. Module:  Simple Predictor (no chain-of-thought needed)
//   3. Metric:  Exact match on intent label
//   4. Baseline: ~55% accuracy zero-shot (77 classes is HARD)
//   5. Optimized: ~75-80% (+20-25pp with the right demos)
//
// The key insight: with 77 possible labels, the model needs to
// SEE examples of similar queries to pick the right fine-grained
// intent. BootstrapRandomSearch finds the best demo set.
//
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
//
// Data setup: See README.md for Banking77 dataset download.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Intent Classification Benchmark              ║");
Console.WriteLine("║   BootstrapRandomSearch on Banking77 (77 classes)     ║");
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
    Console.WriteLine("  This sample requires the Banking77 dataset.");
    Console.WriteLine("  See README.md for download and conversion instructions.");
    Console.WriteLine();
    Console.WriteLine("  Expected files:");
    Console.WriteLine($"    {Path.Combine(dataDir, "train.jsonl")}");
    Console.WriteLine($"    {Path.Combine(dataDir, "dev.jsonl")}");
    return;
}

var trainSet = Example.LoadFromJsonl<ClassifyInput, ClassifyOutput>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<ClassifyInput, ClassifyOutput>(
    Path.Combine(dataDir, "dev.jsonl"));

Console.WriteLine($"  Loaded {trainSet.Count} training examples, {devSet.Count} dev examples");

// Show label distribution
var labelCounts = trainSet
    .GroupBy(e => e.Label.Intent)
    .OrderByDescending(g => g.Count())
    .Take(5);
Console.WriteLine($"  Top 5 intents in training set:");
foreach (var g in labelCounts)
    Console.WriteLine($"    {g.Key}: {g.Count()} examples");
Console.WriteLine($"  ... and {trainSet.GroupBy(e => e.Label.Intent).Count()} unique intents total");
Console.WriteLine();

// ── Exact-Match Metric ──────────────────────────────────────
// Normalize label comparison: trim, lowercase, replace underscores with spaces
Func<ClassifyOutput, ClassifyOutput, bool> exactMatch = (predicted, expected) =>
    NormalizeLabel(predicted.Intent) == NormalizeLabel(expected.Intent);

var untypedMetric = Metric.Create(exactMatch);

// ── Step 1: Evaluate Baseline (no optimization) ─────────────
Console.WriteLine("Step 1: Evaluate Baseline (zero-shot, no demos)");
Console.WriteLine("────────────────────────────────────────────────");

var baselineModule = new IntentClassificationModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, exactMatch);

Console.WriteLine($"  Accuracy: {baseline.AverageScore:P1} ({baseline.AverageScore * devSet.Count:F0}/{devSet.Count} correct)");
Console.WriteLine();

// ── Step 2: Optimize with BootstrapRandomSearch ─────────────
Console.WriteLine("Step 2: BootstrapRandomSearch (finds best demo set)");
Console.WriteLine("───────────────────────────────────────────────────");
Console.WriteLine("  Bootstrapping: run training examples → collect successful traces");
Console.WriteLine("  Random search: try different demo subsets → pick the best");
Console.WriteLine();

var optModule = new IntentClassificationModule(client);
var brs = new BootstrapRandomSearch(numTrials: 10, maxDemos: 6, metricThreshold: 0.5f, seed: 42);
var optimized = await brs.CompileAsync(optModule, trainSet, untypedMetric);
var optScore = await Evaluator.EvaluateAsync(optimized, devSet, exactMatch);

Console.WriteLine($"  Accuracy: {optScore.AverageScore:P1} ({optScore.AverageScore * devSet.Count:F0}/{devSet.Count} correct)");
PrintPredictorState(optimized, "  ");
Console.WriteLine();

// ── Step 3: Also try MIPROv2 ────────────────────────────────
Console.WriteLine("Step 3: MIPROv2 (instructions + demos)");
Console.WriteLine("───────────────────────────────────────");

var mipModule = new IntentClassificationModule(client);
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 12,
    numInstructionCandidates: 4,
    numDemoSubsets: 4,
    maxDemos: 6,
    metricThreshold: 0.5f,
    gamma: 0.25,
    seed: 42);

var mipOptimized = await mipro.CompileAsync(mipModule, trainSet, untypedMetric);
var mipScore = await Evaluator.EvaluateAsync(mipOptimized, devSet, exactMatch);

Console.WriteLine($"  Accuracy: {mipScore.AverageScore:P1}");
Console.WriteLine();

// ── Step 4: Error Analysis ──────────────────────────────────
Console.WriteLine("Step 4: Error Analysis (misclassified examples)");
Console.WriteLine("────────────────────────────────────────────────");

var errors = mipScore.PerExample
    .Where(r => r.Score < 1.0f)
    .Take(5);

foreach (var err in errors)
{
    var example = (Example<ClassifyInput, ClassifyOutput>)err.Example;
    var predicted = (ClassifyOutput)err.Output;
    Console.WriteLine($"  Query:     \"{Truncate(example.Input.Query, 60)}\"");
    Console.WriteLine($"  Expected:  {example.Label.Intent}");
    Console.WriteLine($"  Predicted: {predicted.Intent}");
    Console.WriteLine();
}

// ── Step 5: Results Comparison ──────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results Comparison — Banking77 (77 intents)         ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline (zero-shot):      {baseline.AverageScore,6:P1}                ║");
Console.WriteLine($"║   BootstrapRandomSearch:     {optScore.AverageScore,6:P1}                ║");
Console.WriteLine($"║   MIPROv2 (instr + demos):   {mipScore.AverageScore,6:P1}                ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   77 classes is too many for zero-shot.              ║");
Console.WriteLine("║   Few-shot demos show the model WHAT each intent     ║");
Console.WriteLine("║   looks like — that's the power of optimization.     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Save ────────────────────────────────────────────────────
var artifactPath = Path.Combine(Path.GetTempPath(), "intent-classification-optimized.json");
await mipOptimized.SaveStateAsync(artifactPath);
Console.WriteLine($"\nSaved optimized module to: {artifactPath}");
File.Delete(artifactPath);

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

static string NormalizeLabel(string label)
{
    if (string.IsNullOrWhiteSpace(label))
        return "";
    return label.Trim().ToLowerInvariant().Replace(' ', '_');
}

static void PrintPredictorState(LmpModule module, string indent)
{
    foreach (var (name, pred) in module.GetPredictors())
    {
        Console.WriteLine($"{indent}[{name}] Demos: {pred.Demos.Count}, Instruction: \"{Truncate(pred.Instructions, 60)}\"");
    }
}

static string Truncate(string s, int maxLen) =>
    s.Length <= maxLen ? s : s[..maxLen] + "...";
