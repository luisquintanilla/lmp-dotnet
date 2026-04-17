using LMP;
using LMP.Optimizers;
using LMP.Samples.AdvancedRag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;

// ──────────────────────────────────────────────────────────────
// LMP AdvancedRag — Multi-Hop RAG with 4 Optimizable Predictors
//
// This benchmark demonstrates the full power of LMP on the #1
// enterprise AI pattern: Retrieval-Augmented Generation.
//
//   1. Dataset: RAG-QA Arena Tech (CC-BY-SA-4.0)
//   2. Pipeline: expand → retrieve → rerank → CRAG validate → answer
//   3. Multi-hop: if CRAG says "ambiguous", generates follow-up query
//   4. 4 LMP predictors: expand, rerank, crag_validate, answer (CoT)
//   5. MIPROv2 optimizes all 4 predictors jointly
//
// Architecture mirrors the validated LMP+MEDI integration spec.
// Each predictor can be wrapped as a MEDI processor when the
// Microsoft.Extensions.DataRetrieval package is available.
//
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
//
// Data setup: See README.md for RAG-QA Arena download instructions.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Advanced RAG Benchmark                       ║");
Console.WriteLine("║   Multi-Hop Pipeline with 4 Optimizable Predictors   ║");
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
    !File.Exists(Path.Combine(dataDir, "dev.jsonl")) ||
    !File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  ERROR: Dataset not found!");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("  This sample requires the RAG-QA Arena Tech dataset + corpus.");
    Console.WriteLine("  See README.md for download and conversion instructions.");
    Console.WriteLine();
    Console.WriteLine("  Expected files:");
    Console.WriteLine($"    {Path.Combine(dataDir, "train.jsonl")}  (Q&A pairs)");
    Console.WriteLine($"    {Path.Combine(dataDir, "dev.jsonl")}    (Q&A pairs)");
    Console.WriteLine($"    {Path.Combine(dataDir, "corpus.jsonl")} (passages)");
    return;
}

// Load corpus (one passage per line)
var corpusLines = File.ReadAllLines(Path.Combine(dataDir, "corpus.jsonl"));
var corpus = corpusLines
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line =>
    {
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("text").GetString() ?? "";
    })
    .Where(text => !string.IsNullOrEmpty(text))
    .ToList();

var retriever = new InMemoryRetriever(corpus);

var trainSet = Example.LoadFromJsonl<QuestionInput, GroundedAnswer>(
    Path.Combine(dataDir, "train.jsonl"));
var devSet = Example.LoadFromJsonl<QuestionInput, GroundedAnswer>(
    Path.Combine(dataDir, "dev.jsonl"));

Console.WriteLine($"  Corpus: {corpus.Count} passages");
Console.WriteLine($"  Loaded {trainSet.Count} training examples, {devSet.Count} dev examples");
Console.WriteLine();

// ── Metric: Answer Quality ──────────────────────────────────
// Combines keyword overlap with citation presence
Func<GroundedAnswer, GroundedAnswer, float> answerMetric = (predicted, expected) =>
{
    if (predicted is null || string.IsNullOrWhiteSpace(predicted.Answer)) return 0f;

    // Keyword overlap between predicted and expected answers
    var expectedWords = expected.Answer
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.ToLowerInvariant().Trim('.', ',', '!', '?'))
        .Where(w => w.Length > 3)
        .ToHashSet();

    if (expectedWords.Count == 0) return 0f;

    var predictedWords = predicted.Answer
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.ToLowerInvariant().Trim('.', ',', '!', '?'))
        .Where(w => w.Length > 3)
        .ToHashSet();

    var overlap = expectedWords.Intersect(predictedWords).Count();
    float recall = (float)overlap / expectedWords.Count;
    float precision = predictedWords.Count > 0 ? (float)overlap / predictedWords.Count : 0f;
    float f1 = (recall + precision) > 0 ? 2 * recall * precision / (recall + precision) : 0f;

    // Bonus for having citations
    float citationBonus = predicted.Citations is { Length: > 0 } ? 0.1f : 0f;

    return Math.Min(1f, f1 + citationBonus);
};

var untypedMetric = Metric.Create(answerMetric);

// ── Step 1: Evaluate Simple RAG Baseline ────────────────────
Console.WriteLine("Step 1: Evaluate Baseline (simple RAG, no expansion/reranking)");
Console.WriteLine("───────────────────────────────────────────────────────────────");

var baselineModule = new AdvancedRagModule(client, retriever, topK: 3, maxHops: 1);
var baseline = await Evaluator.EvaluateAsync(baselineModule, devSet, answerMetric, maxConcurrency: 2);

Console.WriteLine($"  Answer F1: {baseline.AverageScore:P1}");
Console.WriteLine();

// ── Step 2: Evaluate Multi-Hop (unoptimized) ────────────────
Console.WriteLine("Step 2: Multi-Hop RAG (3 hops, no optimization)");
Console.WriteLine("─────────────────────────────────────────────────");

var multiHopModule = new AdvancedRagModule(client, retriever, topK: 5, maxHops: 3);
var multiHop = await Evaluator.EvaluateAsync(multiHopModule, devSet, answerMetric, maxConcurrency: 2);

Console.WriteLine($"  Answer F1: {multiHop.AverageScore:P1}");
Console.WriteLine();

// ── Step 3: Optimize with MIPROv2 ───────────────────────────
Console.WriteLine("Step 3: MIPROv2 Optimization (all 4 predictors)");
Console.WriteLine("─────────────────────────────────────────────────");
Console.WriteLine("  Optimizing: expand, rerank, crag_validate, answer");
Console.WriteLine("  MIPROv2 learns which instructions and demos make each");
Console.WriteLine("  predictor most effective in the pipeline context.");
Console.WriteLine();

var optModule = new AdvancedRagModule(client, retriever, topK: 5, maxHops: 3);
var mipro = new MIPROv2(
    proposalClient: client,
    numTrials: 10,
    numInstructionCandidates: 3,
    numDemoSubsets: 3,
    maxDemos: 3,
    metricThreshold: 0.3f,
    gamma: 0.25,
    seed: 42);

var optimized = await mipro.CompileAsync(optModule, trainSet, untypedMetric);
Console.WriteLine("  Cooling down before final evaluation...");
await Task.Delay(TimeSpan.FromSeconds(30));
var optScore = await Evaluator.EvaluateAsync(optimized, devSet, answerMetric, maxConcurrency: 2);

Console.WriteLine($"  Answer F1: {optScore.AverageScore:P1}");
Console.WriteLine();

// ── Step 4: Show Optimized Predictors ───────────────────────
Console.WriteLine("Step 4: Optimized Predictor State");
Console.WriteLine("──────────────────────────────────");

foreach (var (name, pred) in optimized.GetPredictors())
{
    Console.WriteLine($"  [{name}]");
    Console.WriteLine($"    Instruction: \"{Truncate(pred.Instructions, 80)}\"");
    Console.WriteLine($"    Demos: {pred.Demos.Count}");
}
Console.WriteLine();

// ── Step 5: Results Comparison ──────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Results — RAG-QA Arena Tech                         ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Simple RAG (1 hop):        {baseline.AverageScore,6:P1}                ║");
Console.WriteLine($"║   Multi-Hop RAG (3 hops):    {multiHop.AverageScore,6:P1}                ║");
Console.WriteLine($"║   MIPROv2 Optimized:         {optScore.AverageScore,6:P1}                ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   Multi-hop adds retrieval depth.                    ║");
Console.WriteLine("║   MIPROv2 optimizes HOW each predictor works:        ║");
Console.WriteLine("║     - expand: better query variants                  ║");
Console.WriteLine("║     - rerank: sharper relevance judgments             ║");
Console.WriteLine("║     - crag:   calibrated confidence thresholds        ║");
Console.WriteLine("║     - answer: grounded generation with citations     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Save ────────────────────────────────────────────────────
var artifactPath = Path.Combine(Path.GetTempPath(), "advanced-rag-optimized.json");
await optimized.SaveStateAsync(artifactPath);
Console.WriteLine($"\nSaved optimized module to: {artifactPath}");
File.Delete(artifactPath);

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Helpers ─────────────────────────────────────────────────

static string Truncate(string s, int maxLen) =>
    s.Length <= maxLen ? s : s[..maxLen] + "...";
