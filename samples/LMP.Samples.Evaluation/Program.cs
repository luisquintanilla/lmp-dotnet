using LMP;
using LMP.Extensions.Evaluation;
using LMP.Optimizers;
using LMP.Samples.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

// ──────────────────────────────────────────────────────────────
// LMP + M.E.AI.Evaluation Integration Demo (Azure OpenAI)
//
// Demonstrates four evaluation approaches side by side:
//   1. LMP keyword metric (fast, no LLM needed)
//   2. Custom IEvaluator via Bridge (word overlap, no LLM needed)
//   3. M.E.AI.Evaluation LLM-as-judge (Coherence via Azure OpenAI)
//   4. Combined multi-metric with optimization
//
// The EvaluationBridge adapter converts M.E.AI IEvaluator instances
// into LMP-compatible Func<Example, object, Task<float>> metrics,
// enabling seamless integration with LMP's optimization pipeline.
//
// Prerequisites:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP + M.E.AI.Evaluation Integration Demo          ║");
Console.WriteLine("║   (Azure OpenAI)                                     ║");
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

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());

// Module client — runs the support triage pipeline
IChatClient client = azureClient.GetChatClient(deployment).AsIChatClient();

// Eval client — used by LLM-as-judge evaluators (Coherence, Relevance, etc.)
IChatClient evalClient = azureClient.GetChatClient(deployment).AsIChatClient();
var evalConfig = new ChatConfiguration(evalClient);

Console.WriteLine($"  Using: {endpoint}");
Console.WriteLine($"  Deployment: {deployment}");
Console.WriteLine();
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

var devSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "dev.jsonl"));
var trainSet = Example.LoadFromJsonl<TicketInput, DraftReply>(
    Path.Combine(dataDir, "train.jsonl"));

var module = new SupportTriageModule(client);

// ── Approach 1: LMP Keyword Metric (sync, no LLM) ──────────
Console.WriteLine("Approach 1: LMP Keyword Metric (sync, no LLM)");
Console.WriteLine("──────────────────────────────────────────────");

Func<DraftReply, DraftReply, float> keywordMetric = (prediction, label) =>
{
    float score = 0f;
    var categoryPhrases = new[] { "billing", "technical", "account", "security", "feature" };
    var expectedCategory = categoryPhrases.FirstOrDefault(c =>
        label.ReplyText.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? "";
    if (!string.IsNullOrEmpty(expectedCategory) &&
        prediction.ReplyText.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase))
        score += 0.5f;

    if (prediction.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase))
        score += 0.5f;

    return score;
};

var keywordResult = await Evaluator.EvaluateAsync(module, devSet, keywordMetric);
Console.WriteLine($"  Average: {keywordResult.AverageScore:P1}");
Console.WriteLine($"  Min/Max: {keywordResult.MinScore:P1} / {keywordResult.MaxScore:P1}");
Console.WriteLine();

// ── Approach 2: Custom IEvaluator via Bridge (no LLM) ────────
Console.WriteLine("Approach 2: Custom IEvaluator via EvaluationBridge");
Console.WriteLine("──────────────────────────────────────────────────");
Console.WriteLine("  A custom WordOverlapEvaluator implementing IEvaluator,");
Console.WriteLine("  bridged into LMP's metric system automatically.");
Console.WriteLine();

var wordOverlapEvaluator = new WordOverlapEvaluator();

// Create the LMP metric via EvaluationBridge
var bridgedMetric = EvaluationBridge.CreateTypedMetric<DraftReply, DraftReply>(
    wordOverlapEvaluator,
    chatConfiguration: null,
    metricName: WordOverlapEvaluator.OverlapMetricName,
    maxScore: 1.0f);

// Use the async evaluator overload
var bridgedResult = await Evaluator.EvaluateAsync(module, devSet, bridgedMetric);
Console.WriteLine($"  Word Overlap Average: {bridgedResult.AverageScore:P1}");
Console.WriteLine($"  Word Overlap Min/Max: {bridgedResult.MinScore:P1} / {bridgedResult.MaxScore:P1}");
Console.WriteLine();

// ── Approach 3: M.E.AI Coherence (LLM-as-judge) ────────────
Console.WriteLine("Approach 3: M.E.AI Coherence Evaluator (LLM-as-judge)");
Console.WriteLine("──────────────────────────────────────────────────────");
Console.WriteLine("  CoherenceEvaluator uses Azure OpenAI to judge if a response");
Console.WriteLine("  is readable, well-organized, and user-friendly.");
Console.WriteLine("  Scores 1-5, normalized to [0, 1] by the bridge.");
Console.WriteLine();

var coherenceMetric = EvaluationBridge.CreateMetric(
    new CoherenceEvaluator(), evalConfig,
    CoherenceEvaluator.CoherenceMetricName, maxScore: 5.0f);

var coherenceResult = await Evaluator.EvaluateAsync(module, devSet, coherenceMetric);
Console.WriteLine($"  Coherence Average: {coherenceResult.AverageScore:P1}");
Console.WriteLine($"  Coherence Min/Max: {coherenceResult.MinScore:P1} / {coherenceResult.MaxScore:P1}");
Console.WriteLine();

// ── Approach 4: Combined Multi-Metric Evaluation ────────────
Console.WriteLine("Approach 4: Combined Multi-Metric (Coherence + Relevance)");
Console.WriteLine("──────────────────────────────────────────────────────────");
Console.WriteLine("  EvaluationBridge.CreateCombinedMetric() lets you weight");
Console.WriteLine("  multiple M.E.AI evaluators into a single LMP score.");
Console.WriteLine();

// Combine two LLM-as-judge evaluators: Coherence (60%) + Relevance (40%)
// Both score 1-5, so they share the same maxScore normalization.
var combinedMetric = EvaluationBridge.CreateCombinedMetric(
    [
        (new CoherenceEvaluator(), CoherenceEvaluator.CoherenceMetricName, Weight: 0.6f),
        (new RelevanceEvaluator(), RelevanceEvaluator.RelevanceMetricName, Weight: 0.4f)
    ],
    chatConfiguration: evalConfig,
    maxScore: 5.0f);

var combinedResult = await Evaluator.EvaluateAsync(module, devSet, combinedMetric);
Console.WriteLine($"  Combined Average: {combinedResult.AverageScore:P1}");
Console.WriteLine();

// ── Demonstrate: Bridge works with optimization too ─────────
Console.WriteLine("Step 5: Optimize Using M.E.AI Metric");
Console.WriteLine("──────────────────────────────────────");
Console.WriteLine("  The bridged metric works directly with LMP optimizers.");
Console.WriteLine();

var optimizerModule = new SupportTriageModule(client);
var optimizer = new BootstrapRandomSearch(numTrials: 4, maxDemos: 3, metricThreshold: 0.1f, seed: 42);
// Wrap async bridged metric as sync for CompileAsync (acceptable for demos/non-streaming evaluators)
Func<Example, object, float> syncBridgedMetric = (ex, pred) => bridgedMetric(ex, pred).GetAwaiter().GetResult();
var optimized = await optimizer.CompileAsync(optimizerModule, trainSet, syncBridgedMetric);
var optimizedResult = await Evaluator.EvaluateAsync(optimized, devSet, bridgedMetric);

Console.WriteLine($"  Baseline:  {bridgedResult.AverageScore:P1}");
Console.WriteLine($"  Optimized: {optimizedResult.AverageScore:P1}");
Console.WriteLine();

// ── Summary ─────────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Available M.E.AI Evaluators                        ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine("║   NLP (no LLM needed):                               ║");
Console.WriteLine("║     • F1Evaluator        — token overlap F1          ║");
Console.WriteLine("║     • BLEUEvaluator      — BLEU score               ║");
Console.WriteLine("║     • GLEUEvaluator      — GLEU score               ║");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   Quality (LLM-as-judge, scores 1-5):                ║");
Console.WriteLine("║     • CoherenceEvaluator — readability               ║");
Console.WriteLine("║     • RelevanceEvaluator — on-topic                  ║");
Console.WriteLine("║     • GroundednessEvaluator — factual accuracy       ║");
Console.WriteLine("║     • CompletenessEvaluator — thoroughness           ║");
Console.WriteLine("║     • FluencyEvaluator   — grammar & naturalness     ║");
Console.WriteLine("║     • EquivalenceEvaluator — vs ground truth         ║");
Console.WriteLine("║                                                      ║");
Console.WriteLine("║   All bridge seamlessly into LMP via:                 ║");
Console.WriteLine("║     EvaluationBridge.CreateMetric(evaluator, ...)     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");

// ── Custom IEvaluator ────────────────────────────────────────
// Demonstrates implementing IEvaluator for the bridge.
// In production, use M.E.AI built-in evaluators instead.

file sealed class WordOverlapEvaluator : IEvaluator
{
    public const string OverlapMetricName = "WordOverlap";

    public IReadOnlyCollection<string> EvaluationMetricNames => [OverlapMetricName];

    public ValueTask<MeaiEvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        // Get the expected text from the user message (which contains the ground truth)
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var responseText = modelResponse.Text ?? "";

        // Compute word overlap score
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been",
            "have", "has", "had", "do", "does", "did", "will", "would",
            "to", "of", "in", "for", "on", "with", "at", "by", "from",
            "and", "or", "we", "your", "you", "our", "this", "that", "it"
        };

        var expectedWords = userText
            .Split([' ', ',', '.', '!', '?', ';', ':', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (expectedWords.Count == 0)
        {
            var metric = new NumericMetric(OverlapMetricName, 0.0, "No expected keywords found.");
            return new ValueTask<MeaiEvaluationResult>(new MeaiEvaluationResult(metric));
        }

        var matchCount = expectedWords.Count(kw =>
            responseText.Contains(kw, StringComparison.OrdinalIgnoreCase));

        var score = (double)matchCount / expectedWords.Count;
        var reason = $"Matched {matchCount}/{expectedWords.Count} keywords from expected text.";
        var overlapMetric = new NumericMetric(OverlapMetricName, score, reason);

        overlapMetric.Interpretation = score >= 0.5
            ? new EvaluationMetricInterpretation(EvaluationRating.Good, reason: reason)
            : new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: reason);

        return new ValueTask<MeaiEvaluationResult>(new MeaiEvaluationResult(overlapMetric));
    }
}

// ── Mock Chat Client removed — using Azure OpenAI ───────────
