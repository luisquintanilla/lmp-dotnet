using LMP;
using LMP.Samples.AutoOptimize;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP Auto-Optimize — End-to-End Sample
//
// This sample demonstrates the full auto-optimize pipeline:
//   1. [AutoOptimize] attribute on QAModule
//   2. Source gen emits partial void ApplyOptimizedState() declaration
//   3. Generated/QAModule.Optimized.g.cs provides the implementation
//   4. On first GetPredictors() call, optimized state loads automatically
//   5. Predictor has instructions + demos without explicit LoadState call
//
// To optimize:
//   dotnet build -p:LmpAutoOptimize=true
//
// Setup (one-time):
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
// ──────────────────────────────────────────────────────────────

// Same factory the CLI uses — DRY, one definition in QAModule.CreateClient()
IChatClient client = QAModule.CreateClient();

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP Auto-Optimize E2E Sample              ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// Create the module — if a .g.cs exists, ApplyOptimizedState() loads it
var module = new QAModule(client);

// Inspect predictor state
Console.WriteLine("Predictor state after construction:");
Console.WriteLine("───────────────────────────────────");
var predictors = module.GetPredictors();

foreach (var (name, predictor) in predictors)
{
    Console.WriteLine($"  [{name}]");
    Console.WriteLine($"    Instructions: {(string.IsNullOrEmpty(predictor.Instructions) ? "(none)" : predictor.Instructions[..Math.Min(80, predictor.Instructions.Length)] + "...")}");
    Console.WriteLine($"    Demo count:   {predictor.Demos.Count}");
}
Console.WriteLine();

// Run a prediction
Console.WriteLine("Running prediction:");
Console.WriteLine("───────────────────");
var questions = new[]
{
    "What is the capital of France?",
    "Who wrote Romeo and Juliet?",
    "What is photosynthesis?"
};

foreach (var q in questions)
{
    var result = await module.ForwardAsync(new QAInput(q));
    Console.WriteLine($"  Q: {q}");
    Console.WriteLine($"  A: {result.Answer}");
    Console.WriteLine();
}

Console.WriteLine("Done! ✅");
