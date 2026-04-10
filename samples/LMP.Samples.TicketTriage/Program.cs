using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using LMP.Samples.TicketTriage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// ──────────────────────────────────────────────────────────────
// LMP Ticket Triage — End-to-End Sample (Azure OpenAI)
//
// This sample demonstrates the full LMP workflow:
//   1. Define typed input/output records
//   2. Create predictors and compose them into a module
//   3. Evaluate baseline accuracy on a dev set
//   4. Optimize with BootstrapFewShot
//   5. Save and load optimized parameters
//
// Uses Azure OpenAI with DefaultAzureCredential (managed identity).
// Configure endpoint and deployment via user secrets:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Ticket Triage Demo (Azure OpenAI)   ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// ── Configure Azure OpenAI via user secrets + managed identity ──
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Set AzureOpenAI:Endpoint in user secrets.");
string deployment = config["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException("Set AzureOpenAI:Deployment in user secrets.");

IChatClient client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

Console.WriteLine($"  Using: {deployment} @ {endpoint}");
Console.WriteLine();

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

Func<DraftReply, DraftReply, float> metric = (prediction, label) =>
{
    // Structured rubric metric (3 dimensions, each 0–1):
    //   1. Category detection: does the reply address the same topic?
    //   2. Keyword coverage: does it include relevant terms from expected reply?
    //   3. Professional tone: does it open with an appropriate greeting?

    float score = 0f;

    // Dimension 1: Category detection via key phrases (0.4 weight)
    var categoryPhrases = new[] { "billing", "technical", "account", "security", "feature" };
    var expectedCategory = categoryPhrases.FirstOrDefault(c =>
        label.ReplyText.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? "";
    if (!string.IsNullOrEmpty(expectedCategory) &&
        prediction.ReplyText.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase))
        score += 0.4f;

    // Dimension 2: Keyword overlap (0.4 weight)
    var keywords = ExtractKeywords(label.ReplyText);
    if (keywords.Length > 0)
    {
        var matchCount = keywords.Count(kw =>
            prediction.ReplyText.Contains(kw, StringComparison.OrdinalIgnoreCase));
        score += 0.4f * matchCount / keywords.Length;
    }

    // Dimension 3: Professional tone — opens with appropriate greeting (0.2 weight)
    if (prediction.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase) ||
        prediction.ReplyText.StartsWith("We sincerely", StringComparison.OrdinalIgnoreCase) ||
        prediction.ReplyText.StartsWith("We apologize", StringComparison.OrdinalIgnoreCase))
        score += 0.2f;

    return score;
};

var baseline = await Evaluator.EvaluateAsync(module, devSet, metric);

Console.WriteLine($"  Examples:      {baseline.Count}");
Console.WriteLine($"  Average score: {baseline.AverageScore:P1}");
Console.WriteLine($"  Min score:     {baseline.MinScore:P1}");
Console.WriteLine($"  Max score:     {baseline.MaxScore:P1}");
Console.WriteLine();

// ── Step 5: Optimize with BootstrapRandomSearch ─────────────
Console.WriteLine("Step 5: Optimize with BootstrapRandomSearch (8 trials)");
Console.WriteLine("──────────────────────────────────────────────────────");

var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));

// Wrap the typed metric for the optimizer (which uses the untyped signature)
var untypedMetric = Metric.Create(metric);
var optimizer = new BootstrapRandomSearch(numTrials: 8, maxDemos: 4, metricThreshold: 0.3f);
var optimized = await optimizer.CompileAsync(module, trainSet, untypedMetric);

var optimizedScore = await Evaluator.EvaluateAsync(optimized, devSet, metric);

Console.WriteLine($"  Optimized average score: {optimizedScore.AverageScore:P1}");
Console.WriteLine($"  Demos in classify: {optimized.GetPredictors().First(p => p.Name == "classify").Predictor.Demos.Count}");
Console.WriteLine($"  Demos in draft:    {optimized.GetPredictors().First(p => p.Name == "draft").Predictor.Demos.Count}");
Console.WriteLine();

// ── Step 6: Save and Load ───────────────────────────────────
Console.WriteLine("Step 6: Save & Load");
Console.WriteLine("────────────────────");

var artifactPath = Path.Combine(Path.GetTempPath(), "triage-optimized.json");
await optimized.SaveStateAsync(artifactPath);
Console.WriteLine($"  Saved to: {artifactPath}");

var loaded = new SupportTriageModule(client);
await loaded.ApplyStateAsync(artifactPath);

var loadedScore = await Evaluator.EvaluateAsync(loaded, devSet, metric);
Console.WriteLine($"  Loaded module score: {loadedScore.AverageScore:P1}");
Console.WriteLine();

// Clean up
// File.Delete(artifactPath);

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



