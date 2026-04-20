using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Optimizers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — ChatClientTarget: MEAI-Native Optimization       ║");
Console.WriteLine("║   Optimize any IChatClient without writing a Module      ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
string endpoint   = config["AzureOpenAI:Endpoint"]    ?? throw new InvalidOperationException("Set AzureOpenAI:Endpoint in user secrets.");
string deployment = config["AzureOpenAI:Deployment"]  ?? throw new InvalidOperationException("Set AzureOpenAI:Deployment in user secrets.");

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
IChatClient client = azureClient.GetChatClient(deployment).AsIChatClient();

Console.WriteLine($"  Endpoint:   {endpoint}");
Console.WriteLine($"  Deployment: {deployment}");
Console.WriteLine();

// ── Data loading ──────────────────────────────────────────────────────────────
// ChatClientTarget requires string input/output. Load typed JSONL and convert.
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var rawTrain = Example.LoadFromJsonl<TicketInput, ReplyLabel>(
    Path.Combine(dataDir, "train.jsonl"));
var rawDev = Example.LoadFromJsonl<TicketInput, ReplyLabel>(
    Path.Combine(dataDir, "dev.jsonl"));

IReadOnlyList<Example> trainSet = rawTrain.Take(20)
    .Select(e => (Example)new Example<string, string>(e.Input.TicketText, e.Label.ReplyText))
    .ToList();
IReadOnlyList<Example> devSet = rawDev.Take(10)
    .Select(e => (Example)new Example<string, string>(e.Input.TicketText, e.Label.ReplyText))
    .ToList();

Console.WriteLine($"  Train: {trainSet.Count} examples   Dev: {devSet.Count} examples");
Console.WriteLine();

// ── Metric: keyword overlap between predicted reply and expected reply ─────────
Func<Example, object, float> keywordMetric = (example, output) =>
{
    var predicted = (output as string) ?? output?.ToString() ?? "";
    var expected  = (example.GetLabel() as string) ?? "";
    var keywords  = Keywords(expected);
    if (keywords.Length == 0) return 0f;
    return (float)keywords.Count(kw => predicted.Contains(kw, StringComparison.OrdinalIgnoreCase))
           / keywords.Length;
};

// ══════════════════════════════════════════════════════════════════════════════
// Step 1: Baseline — raw IChatClient, fixed system prompt, measure quality
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 1: Baseline — ChatClientTarget with a fixed system prompt");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  ChatClientTarget.For() wraps any IChatClient as an optimization");
Console.WriteLine("  target. No [Predict] attribute, no source gen, no LmpModule.");
Console.WriteLine();

const string SupportPrompt =
    "You are a helpful customer support agent. " +
    "Respond politely and address the customer's concern directly.";

var baselineTarget = ChatClientTarget.For(client, systemPrompt: SupportPrompt, temperature: 0.7f);

// Running with no optimizers measures the baseline quality only.
var baselineResult = await OptimizationPipeline.For(baselineTarget)
    .OptimizeAsync(trainSet, devSet, keywordMetric);

Console.WriteLine($"  Baseline quality: {baselineResult.BaselineScore:P1}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 2: BayesianCalibration — automatically tune the temperature parameter
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 2: BayesianCalibration — finding the optimal temperature");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  BayesianCalibration treats temperature as a Continuous hyperparameter");
Console.WriteLine("  and uses TPE to find the value that maximises keyword-overlap quality.");
Console.WriteLine();

var calibTarget = ChatClientTarget.For(client, systemPrompt: SupportPrompt, temperature: 0.7f);

var calibResult = await OptimizationPipeline.For(calibTarget)
    .Use(new BayesianCalibration(numRefinements: 6, continuousSteps: 4, seed: 42))
    .OptimizeAsync(trainSet, devSet, keywordMetric);

var calibState = calibResult.Target.GetState().As<ChatClientState>();
Console.WriteLine($"  Baseline:              {calibResult.BaselineScore:P1}");
Console.WriteLine($"  After calibration:     {calibResult.OptimizedScore:P1}");
Console.WriteLine($"  Learned temperature:   {calibState.Temperature:F2}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 3: ChainTarget — two-stage classify → draft pipeline
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 3: ChainTarget — classify then draft (two-stage pipeline)");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  ChainTarget.For(stage1, stage2) pipes the string output of stage1");
Console.WriteLine("  as input to stage2. BayesianCalibration tunes each stage's");
Console.WriteLine("  temperature independently: child_0.temperature / child_1.temperature.");
Console.WriteLine();

// Stage 1: classify the ticket into a structured category + summary
var classifyTarget = ChatClientTarget.For(
    client,
    systemPrompt: "Classify this customer-support ticket. " +
                  "Start with 'Category: Billing|Technical|Account|General. ' " +
                  "then one sentence summarising the issue.",
    temperature: 0.2f);

// Stage 2: draft a reply given the category + summary from stage 1
var draftTarget = ChatClientTarget.For(
    client,
    systemPrompt: "You are a support agent. " +
                  "Write a brief, professional reply for the described support issue. " +
                  "Start with 'Thank you for reaching out.'",
    temperature: 0.7f);

var chain = ChainTarget.For(classifyTarget, draftTarget);

var chainResult = await OptimizationPipeline.For(chain)
    .Use(new BayesianCalibration(numRefinements: 4, continuousSteps: 4, seed: 42))
    .OptimizeAsync(trainSet, devSet, keywordMetric);

Console.WriteLine($"  Chain baseline:        {chainResult.BaselineScore:P1}");
Console.WriteLine($"  Chain after tuning:    {chainResult.OptimizedScore:P1}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 4: UseOptimized — deploy the calibrated state as MEAI middleware
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 4: UseOptimized — deploy as M.E.AI middleware");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  UseOptimized() injects the learned system prompt and temperature");
Console.WriteLine("  into every future GetResponseAsync call via MEAI's builder API.");
Console.WriteLine("  The resulting IChatClient can be used anywhere in your application.");
Console.WriteLine();

// Build a new client with the calibrated state as middleware
var deployedClient = new ChatClientBuilder(client)
    .UseOptimized(calibResult)     // calibResult.Target is ChatClientTarget — state holds ChatClientState
    .Build();

Console.WriteLine($"  Deployed with temperature: {calibState.Temperature:F2}");
Console.WriteLine($"  System prompt: \"{SupportPrompt[..60]}...\"");
Console.WriteLine();

// Test the deployed client with a sample ticket
var testTicket = rawDev.FirstOrDefault()?.Input.TicketText
    ?? "I was charged twice on my last invoice and need a refund.";
var testReply = await deployedClient.GetResponseAsync(testTicket);
var replyText = testReply.Text ?? "";
Console.WriteLine($"  Test ticket: \"{testTicket[..Math.Min(70, testTicket.Length)]}\"");
Console.WriteLine($"  Reply:       \"{(replyText.Length > 100 ? replyText[..100] + "..." : replyText)}\"");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 5: Emit compile-time artifact — IChatClient factory
//
// LmpModule path:       CLI calls CSharpArtifactWriter.WriteAsync(module, ...) → partial void ApplyOptimizedState()
// ChatClientTarget path: call WriteForChatClientTargetAsync(state, ...) → static IChatClient Build(baseClient)
//
// Both produce .g.cs files you commit to source control. Next build: zero I/O, compile-time constants.
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 5: Emit compile-time artifact — IChatClient factory");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  For LmpModule, the CLI calls CSharpArtifactWriter.WriteAsync() and emits");
Console.WriteLine("  a partial void ApplyOptimizedState() implementation. ChatClientTarget has");
Console.WriteLine("  the same philosophy: emit a static IChatClient Build(baseClient) factory");
Console.WriteLine("  with the learned state baked in as compile-time constants.");
Console.WriteLine();

// Walk up from AppContext.BaseDirectory (bin/Debug/net10.0) to find the .csproj — dev-time only.
static string FindProjectDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("*.csproj").Length > 0) return dir.FullName;
        dir = dir.Parent!;
    }
    return AppContext.BaseDirectory; // fallback: write alongside the binary
}

var artifactDir = Path.Combine(FindProjectDir(), "Generated");
var artifactPath = await CSharpArtifactWriter.WriteForChatClientTargetAsync(
    state: calibState,
    className: "OptimizedSupportClient",
    ns: "LMP.Samples.ChatClientTarget",
    outputDir: artifactDir,
    score: calibResult.OptimizedScore,
    optimizerName: "BayesianCalibration",
    baseline: calibResult.BaselineScore);

Console.WriteLine($"  Generated: {artifactPath}");
Console.WriteLine();
Console.WriteLine(await File.ReadAllTextAsync(artifactPath));
Console.WriteLine();
Console.WriteLine("  ► Commit Generated/OptimizedSupportClient.g.cs to source control.");
Console.WriteLine("  ► On next build (dotnet build), this compiles into the assembly:");
Console.WriteLine("      IChatClient client = OptimizedSupportClient.Build(rawAzureClient);");
Console.WriteLine("    Zero I/O. No re-optimization. Compile-time constants only.");
Console.WriteLine();

// ── Results Summary ───────────────────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║   ChatClientTarget — Optimization Results                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║   Step 1  Baseline (temperature=0.7):    {baselineResult.BaselineScore,6:P1}              ║");
Console.WriteLine($"║   Step 2  BayesianCalibration:           {calibResult.OptimizedScore,6:P1}  (temp={calibState.Temperature:F2})  ║");
Console.WriteLine($"║   Step 3  ChainTarget (2-stage):         {chainResult.OptimizedScore,6:P1}              ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine("║                                                              ║");
Console.WriteLine("║   Key takeaways:                                             ║");
Console.WriteLine("║   • ChatClientTarget — no LmpModule subclass needed          ║");
Console.WriteLine("║   • BayesianCalibration — tunes Continuous params (temp)     ║");
Console.WriteLine("║   • ChainTarget — chains targets, each tuned independently   ║");
Console.WriteLine("║   • UseOptimized — deploys via M.E.AI ChatClientBuilder      ║");
Console.WriteLine("║                                                              ║");
Console.WriteLine("║   Compile-time artifact paths:                               ║");
Console.WriteLine("║   • LmpModule  → CLI emits partial void ApplyOptimizedState()║");
Console.WriteLine("║   • ChatClient → Step 5 emits static IChatClient Build(...)  ║");
Console.WriteLine("║     Both: commit the .g.cs → next build uses it, zero I/O   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

// ── File-local types and helpers ──────────────────────────────────────────────
static string[] Keywords(string text)
{
    var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "have", "has", "had", "do", "does", "did", "will", "would",
        "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "and", "or", "we", "your", "you", "our", "this", "that", "it",
        "as", "into", "i", "me", "my"
    };
    return text
        .Split([' ', ',', '.', '!', '?', ';', ':', '-', '(', ')'],
               StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 2 && !stopWords.Contains(w))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

// JSONL shapes — must match the data file's "input" and "label" objects
record TicketInput(string TicketText);
record ReplyLabel(string ReplyText);
