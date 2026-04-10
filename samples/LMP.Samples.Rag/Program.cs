using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using LMP.Samples.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// ──────────────────────────────────────────────────────────────
// LMP RAG (Retrieval-Augmented Generation) — End-to-End Sample
//
// This sample demonstrates how to build a RAG pipeline with LMP:
//   1. Create an in-memory retriever with knowledge passages
//   2. Compose a RagModule that retrieves context then answers
//   3. Run a single question through the pipeline
//   4. Evaluate accuracy on a dev set
//
// Uses Azure OpenAI with DefaultAzureCredential (managed identity).
// Configure endpoint and deployment via user secrets:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — RAG Pipeline Demo (Azure OpenAI)    ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// ── Knowledge base passages about the fictional "NovaBridge" product ──

var passages = new[]
{
    "NovaBridge is a cloud-native API gateway and integration platform designed for enterprise workloads. It supports up to 10,000 concurrent users on the Enterprise plan.",
    "NovaBridge provides official SDKs for Python, JavaScript, Go, and C#. Community-maintained SDKs are available for Ruby and Java.",
    "All data in NovaBridge is encrypted at rest using AES-256 and in transit using TLS 1.3. Key management is handled by an integrated HSM.",
    "NovaBridge Enterprise offers a 99.99% uptime SLA with automatic failover across three availability zones. Standard plans offer 99.9% uptime.",
    "NovaBridge supports on-premises database integration through its Hybrid Connector feature, which creates a secure tunnel between the cloud platform and local infrastructure.",
    "Authentication in NovaBridge supports OAuth 2.0, SAML, API keys, and mutual TLS. Multi-factor authentication can be enforced at the organization level.",
    "NovaBridge pricing starts at $49/month for the Starter plan, $199/month for Professional, and custom pricing for Enterprise. All plans include a 14-day free trial.",
    "The NovaBridge CLI tool allows developers to deploy, configure, and monitor API routes directly from the terminal. It is available via npm, pip, and Homebrew.",
    "NovaBridge rate limiting is configurable per route and per API key. The default rate limit is 1,000 requests per minute on the Professional plan.",
    "NovaBridge logs can be exported to any OpenTelemetry-compatible backend. Built-in dashboards provide real-time latency, error rate, and throughput metrics.",
};

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
var retriever = new InMemoryRetriever(passages);

// ── Step 1: Single question answering ────────────────────────
Console.WriteLine("Step 1: Single Question");
Console.WriteLine("───────────────────────");

var module = new RagModule(client, retriever);

var result = await module.ForwardAsync(
    new QuestionInput("What encryption does NovaBridge use?"));

Console.WriteLine($"  Answer:     {result.Answer}");
Console.WriteLine($"  Confidence: {result.Confidence:P0}");
Console.WriteLine();

// ── Step 2: Another question ─────────────────────────────────
Console.WriteLine("Step 2: Another Question");
Console.WriteLine("────────────────────────");

result = await module.ForwardAsync(
    new QuestionInput("Which SDKs does NovaBridge offer?"));

Console.WriteLine($"  Answer:     {result.Answer}");
Console.WriteLine($"  Confidence: {result.Confidence:P0}");
Console.WriteLine();

// ── Step 3: Evaluate on dev set ──────────────────────────────
Console.WriteLine("Step 3: Evaluate on Dev Set");
Console.WriteLine("───────────────────────────");

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var devSet = Example.LoadFromJsonl<QuestionInput, AnswerOutput>(
    Path.Combine(dataDir, "dev.jsonl"));

Func<AnswerOutput, AnswerOutput, float> metric = (prediction, label) =>
{
    // Score: do predicted keywords overlap with expected answer keywords?
    var expectedWords = ExtractKeywords(label.Answer);
    var matchCount = expectedWords.Count(kw =>
        prediction.Answer.Contains(kw, StringComparison.OrdinalIgnoreCase));

    return expectedWords.Length > 0 ? (float)matchCount / expectedWords.Length : 0f;
};

var evalResult = await Evaluator.EvaluateAsync(module, devSet, metric);

Console.WriteLine($"  Examples:      {evalResult.Count}");
Console.WriteLine($"  Average score: {evalResult.AverageScore:P1}");
Console.WriteLine($"  Min score:     {evalResult.MinScore:P1}");
Console.WriteLine($"  Max score:     {evalResult.MaxScore:P1}");
Console.WriteLine();

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
        "your", "you", "our", "this", "that", "it", "and", "or", "i",
        "up", "all", "its"
    };

    return text
        .Split([' ', ',', '.', '!', '?', ';', ':', '-', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 2 && !stopWords.Contains(w))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}



