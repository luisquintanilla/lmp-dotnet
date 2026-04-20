using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Samples.Modules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — Inference-Time Modules                           ║");
Console.WriteLine("║   BestOfN · Refine · ProgramOfThought · LmpSuggest      ║");
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

const string TestTicket = "I was charged twice on my last invoice and need this resolved urgently.";
var ticketInput = new TicketInput(TestTicket);
Console.WriteLine($"  Test ticket: \"{TestTicket}\"");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 1: Baseline — single Predictor forward pass
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 1: Baseline — single Predictor forward pass");
Console.WriteLine("──────────────────────────────────────────────────");
Console.WriteLine("  Predictor<TIn, TOut> makes one structured LM call and returns");
Console.WriteLine("  the result. Source gen emits a PromptBuilder that uses");
Console.WriteLine("  [LmpSignature] and [Description] attributes as the LM prompt.");
Console.WriteLine();

var baselineModule = new InferenceModule(client) { Strategy = PredictionStrategy.Baseline };
var baselineReply  = await baselineModule.ForwardAsync(ticketInput);

Console.WriteLine($"  Strategy: Baseline (1 LM call)");
Console.WriteLine($"  Reply:    {baselineReply.ReplyText[..Math.Min(120, baselineReply.ReplyText.Length)]}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 2: BestOfN — N parallel predictions, pick the highest-reward result
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 2: BestOfN — 3 parallel predictions, pick the best");
Console.WriteLine("──────────────────────────────────────────────────────────");
Console.WriteLine("  BestOfN<TIn, TOut>(client, n: 3, reward) fires N concurrent");
Console.WriteLine("  predictions via Task.WhenAll and returns the highest-scoring one.");
Console.WriteLine("  Cost: N× tokens. Best for latency-sensitive quality improvement.");
Console.WriteLine();

var bestOfNModule = new InferenceModule(client) { Strategy = PredictionStrategy.BestOfN };
var bestOfNReply  = await bestOfNModule.ForwardAsync(ticketInput);

Console.WriteLine($"  Strategy: BestOfN (3 parallel LM calls)");
Console.WriteLine($"  Reply:    {bestOfNReply.ReplyText[..Math.Min(120, bestOfNReply.ReplyText.Length)]}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 3: Refine — iterative predict → self-critique → improve loop
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 3: Refine — iterative predict → critique → improve");
Console.WriteLine("──────────────────────────────────────────────────────────");
Console.WriteLine("  Refine<TIn, TOut>(client, maxIterations: 2) starts with an");
Console.WriteLine("  initial prediction and then asks the LM to critique and improve");
Console.WriteLine("  it twice. Each iteration sees the original input + previous output.");
Console.WriteLine("  Cost: (1 + maxIterations)× tokens for sequential quality gain.");
Console.WriteLine();

var refineModule = new InferenceModule(client) { Strategy = PredictionStrategy.Refine };
var refineReply  = await refineModule.ForwardAsync(ticketInput);

Console.WriteLine($"  Strategy: Refine (1 initial + 2 critique iterations = 3 LM calls)");
Console.WriteLine($"  Reply:    {refineReply.ReplyText[..Math.Min(120, refineReply.ReplyText.Length)]}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// Step 4: ProgramOfThought — LM generates C# code, Roslyn executes it
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 4: ProgramOfThought — LM writes C# code, Roslyn runs it");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  ProgramOfThought<TIn, TOut> asks the LM to generate C# code,");
Console.WriteLine("  then executes it via Roslyn scripting. If the code fails to");
Console.WriteLine("  compile or runs with an error, the error is fed back to the LM");
Console.WriteLine("  and it tries again. Perfect for deterministic computations.");
Console.WriteLine();

// ProgramOfThought uses Roslyn scripting — suppress AOT-incompatibility warnings
#pragma warning disable IL2026, IL3050
var pot = new ProgramOfThought<MathProblem, string>(client);
#pragma warning restore IL2026, IL3050

var problems = new[]
{
    new MathProblem("(150 + 350) * 2 / 5"),          // = 200
    new MathProblem("Math.Pow(2, 10) - 24"),          // = 1000
    new MathProblem("string.Join(\", \", Enumerable.Range(1, 5).Select(x => x * x))"),  // = 1, 4, 9, 16, 25
};

foreach (var problem in problems)
{
    try
    {
        var result = await pot.PredictAsync(problem);
        Console.WriteLine($"  Expression: {problem.Expression}");
        Console.WriteLine($"  Answer:     {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Expression: {problem.Expression}");
        Console.WriteLine($"  Error:      {ex.Message}");
    }
    Console.WriteLine();
}

// ══════════════════════════════════════════════════════════════════════════════
// Step 5: LmpSuggest vs LmpAssert — soft and hard guardrails
// ══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("Step 5: LmpSuggest vs LmpAssert — soft and hard guardrails");
Console.WriteLine("──────────────────────────────────────────────────────────────");
Console.WriteLine("  LmpAssert.That() throws LmpAssertionException on failure →");
Console.WriteLine("    predictor retries with the failure message in context (hard).");
Console.WriteLine("  LmpSuggest.That() returns false on failure but never throws →");
Console.WriteLine("    caller decides what to do, execution continues (soft).");
Console.WriteLine();

var guardModule = new InferenceModule(client) { Strategy = PredictionStrategy.Baseline };
var guardPredictor = new Predictor<TicketInput, DraftReply>(client);

// Hard assertion: reply must be at least 30 characters (retries if too short)
Console.WriteLine("  LmpAssert (hard) — reply must be ≥ 30 characters:");
DraftReply assertReply;
try
{
    assertReply = await guardPredictor.PredictAsync(
        ticketInput,
        validate: result => LmpAssert.That(
            result,
            r => r.ReplyText.Length >= 30,
            "Reply must be at least 30 characters"));
    Console.WriteLine($"  ✅ Assertion passed — reply length: {assertReply.ReplyText.Length}");
}
catch (LmpMaxRetriesExceededException)
{
    Console.WriteLine("  ❌ Max retries exceeded — reply consistently too short");
}
Console.WriteLine();

// Soft suggestion: reply should start with "Thank you" (logs warning, never throws)
Console.WriteLine("  LmpSuggest (soft) — reply should start with 'Thank you':");
var suggestReply = await guardPredictor.PredictAsync(ticketInput);
bool isPolite = LmpSuggest.That(
    suggestReply,
    r => r.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase),
    "Reply should start with 'Thank you for reaching out.'");

if (isPolite)
    Console.WriteLine("  ✅ Suggestion satisfied — reply starts with 'Thank you'");
else
    Console.WriteLine("  ⚠  Suggestion not met — continuing anyway (soft guardrail)");
Console.WriteLine();

// ── Results Summary ───────────────────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║   Inference Module Strategy Comparison                       ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine("║   Module          LM Calls   Use When                        ║");
Console.WriteLine("║   ────────────    ─────────  ──────────────────────────────  ║");
Console.WriteLine("║   Predictor       1          Default — fast, low cost        ║");
Console.WriteLine("║   BestOfN(n=3)    3 ∥        High quality, latency budget    ║");
Console.WriteLine("║   Refine(iter=2)  3 seq      Self-critique, nuanced tasks    ║");
Console.WriteLine("║   ProgramOfThought 1–4       Deterministic computation       ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine("║   Guardrails:                                                ║");
Console.WriteLine("║   LmpAssert — hard: throws + retries on constraint failure   ║");
Console.WriteLine("║   LmpSuggest — soft: returns bool, caller handles            ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
