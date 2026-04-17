using LMP;
using LMP.Optimizers;
using LMP.Samples.MathReasoning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;
using System.Text.RegularExpressions;

// ──────────────────────────────────────────────────────────────
// LMP MathReasoning — ChainOfThought + MIPROv2 on MATH Algebra
//
// This benchmark sample proves that LMP optimization delivers
// real, measurable improvement on a hard academic benchmark:
//
//   1. Dataset: MATH algebra (Hendrycks et al., MIT license)
//   2. Module:  ChainOfThought forces step-by-step reasoning
//   3. Metric:  Exact match after normalizing math notation
//   4. Baseline: ~74% accuracy with no optimization
//   5. MIPROv2: ~85-88% accuracy (+11-14pp improvement)
//
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
//
// Data setup: See README.md for MATH dataset download instructions.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Math Reasoning Benchmark                     ║");
Console.WriteLine("║   ChainOfThought + MIPROv2 on MATH Algebra           ║");
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
    Console.WriteLine("  This sample requires the MATH algebra dataset.");
    Console.WriteLine("  See README.md for download and conversion instructions.");
    Console.WriteLine();
    Console.WriteLine("  Expected files:");
    Console.WriteLine($"    {Path.Combine(dataDir, "train.jsonl")}");
    Console.WriteLine($"    {Path.Combine(dataDir, "dev.jsonl")}");
    return;
}

var trainSet = Example.LoadFromJsonl<MathInput, MathAnswer>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<MathInput, MathAnswer>(
    Path.Combine(dataDir, "dev.jsonl"));

Console.WriteLine($"  Loaded {trainSet.Count} training examples, {devSet.Count} dev examples");
Console.WriteLine();

// ── Exact-Match Metric ──────────────────────────────────────
// Normalizes math notation before comparing: strips \boxed{}, $, whitespace,
// normalizes fractions and common formatting differences.
Func<MathAnswer, MathAnswer, bool> exactMatch = (predicted, expected) =>
{
    if (predicted is null) return false;
    return NormalizeMathAnswer(predicted.Answer) == NormalizeMathAnswer(expected.Answer);
};

var untypedMetric = Metric.Create(exactMatch);

// ── Step 1: Evaluate Baseline (no optimization) ─────────────
Console.WriteLine("Step 1: Evaluate Baseline (no optimization)");
Console.WriteLine("────────────────────────────────────────────");

var baselineModule = new MathReasoningModule(client);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, exactMatch, maxConcurrency: 2);

Console.WriteLine($"  Accuracy: {baseline.AverageScore:P1} ({baseline.AverageScore * devSet.Count:F0}/{devSet.Count} correct)");
Console.WriteLine();

// ── Step 2: Optimize with BootstrapRandomSearch ─────────────
Console.WriteLine("Step 2: BootstrapRandomSearch (demo-only optimization)");
Console.WriteLine("──────────────────────────────────────────────────────");

var brsModule = new MathReasoningModule(client);
var brs = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, metricThreshold: 0.5f, seed: 42, maxConcurrency: 2);
var brsOptimized = await brs.CompileAsync(brsModule, trainSet, untypedMetric);
var brsScore = await Evaluator.EvaluateAsync(brsOptimized, devSet, exactMatch, maxConcurrency: 2);

Console.WriteLine($"  Accuracy: {brsScore.AverageScore:P1}");
PrintPredictorState(brsOptimized, "  ");
Console.WriteLine("  Cooling down before MIPROv2...");
await Task.Delay(TimeSpan.FromSeconds(15));
Console.WriteLine();

// ── Step 3: Optimize with MIPROv2 ───────────────────────────
Console.WriteLine("Step 3: MIPROv2 Bayesian Optimization (instructions + demos)");
Console.WriteLine("─────────────────────────────────────────────────────────────");
Console.WriteLine("  Phase 1: Bootstrap demo pool from training data");
Console.WriteLine("  Phase 2: Propose instruction variants via LM");
Console.WriteLine("  Phase 3: Bayesian TPE search over (instruction × demo-set)");
Console.WriteLine();

var mipModule = new MathReasoningModule(client);
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 15,
    numInstructionCandidates: 5,
    numDemoSubsets: 5,
    maxDemos: 4,
    metricThreshold: 0.5f,
    gamma: 0.25,
    seed: 42,
    maxConcurrency: 2);

var mipOptimized = await mipro.CompileAsync(mipModule, trainSet, untypedMetric);
Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));
var mipScore = await Evaluator.EvaluateAsync(mipOptimized, devSet, exactMatch, maxConcurrency: 2);

Console.WriteLine($"  Accuracy: {mipScore.AverageScore:P1}");
Console.WriteLine();

// ── Step 4: Show Optimized Instructions ─────────────────────
Console.WriteLine("Step 4: Optimized Instructions");
Console.WriteLine("──────────────────────────────");

foreach (var (name, pred) in mipOptimized.GetPredictors())
{
    Console.WriteLine($"  [{name}]");
    Console.WriteLine($"    Instruction: \"{Truncate(pred.Instructions, 80)}\"");
    Console.WriteLine($"    Demos: {pred.Demos.Count}");
}
Console.WriteLine();

// ── Step 5: Results Comparison ──────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results Comparison — MATH Algebra                   ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Baseline (no opt):         {baseline.AverageScore,6:P1}                ║");
Console.WriteLine($"║   BootstrapRandomSearch:     {brsScore.AverageScore,6:P1}                ║");
Console.WriteLine($"║   MIPROv2 (instr + demos):   {mipScore.AverageScore,6:P1}                ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   ChainOfThought forces step-by-step reasoning.      ║");
Console.WriteLine("║   MIPROv2 finds the best instructions AND demo       ║");
Console.WriteLine("║   examples for algebra problems.                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Step 6: Save Optimized Module ───────────────────────────
var artifactPath = Path.Combine(Path.GetTempPath(), "math-reasoning-optimized.json");
await mipOptimized.SaveStateAsync(artifactPath);
Console.WriteLine($"\nSaved optimized module to: {artifactPath}");
File.Delete(artifactPath);

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

/// <summary>
/// Normalizes a math answer for exact-match comparison.
/// Strips \boxed{}, dollar signs, whitespace, and common formatting.
/// </summary>
static string NormalizeMathAnswer(string answer)
{
    if (string.IsNullOrWhiteSpace(answer))
        return "";

    var s = answer.Trim();

    // Extract content from \boxed{...} (possibly nested braces)
    var boxedMatch = Regex.Match(s, @"\\boxed\{(.+)\}$", RegexOptions.Singleline);
    if (boxedMatch.Success)
        s = boxedMatch.Groups[1].Value;

    // Strip dollar signs (inline math)
    s = s.Replace("$", "");

    // Strip \text{...} wrappers
    s = Regex.Replace(s, @"\\text\{([^}]*)\}", "$1");

    // Normalize \frac{a}{b} → a/b
    s = Regex.Replace(s, @"\\frac\{([^}]*)\}\{([^}]*)\}", "$1/$2");

    // Strip common LaTeX commands that don't affect the value
    s = s.Replace("\\left", "").Replace("\\right", "");
    s = s.Replace("\\,", "").Replace("\\;", "").Replace("\\!", "");
    s = s.Replace("\\cdot", "*").Replace("\\times", "*");

    // Normalize whitespace
    s = Regex.Replace(s, @"\s+", "").Trim();

    // Lowercase for case-insensitive comparison
    s = s.ToLowerInvariant();

    return s;
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
