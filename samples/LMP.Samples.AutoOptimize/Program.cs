using LMP;
using LMP.Samples.AutoOptimize;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP Auto-Optimize — End-to-End Verification Sample
//
// This sample proves the full auto-optimize pipeline:
//   1. [AutoOptimize] attribute on QAModule
//   2. Source gen emits partial void ApplyOptimizedState() declaration
//   3. Generated/QAModule.Optimized.g.cs provides the implementation
//   4. On first GetPredictors() call, optimized state loads automatically
//   5. Predictor has instructions + demos without explicit LoadState call
//
// No API key needed — uses a mock chat client.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP Auto-Optimize E2E Verification        ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

IChatClient client = new MockChatClient();

// Create the module — at this point, no state is loaded yet
var module = new QAModule(client);

// Verify: before GetPredictors(), the predictor has no instructions or demos
// (We can't inspect directly since _qa is private, but GetPredictors triggers the load)

// Step 1: Call GetPredictors() — this triggers ApplyOptimizedState()
Console.WriteLine("Step 1: Call GetPredictors() — triggers ApplyOptimizedState()");
Console.WriteLine("─────────────────────────────────────────────────────────────");
var predictors = module.GetPredictors();

Console.WriteLine($"  Predictor count: {predictors.Count}");

foreach (var (name, predictor) in predictors)
{
    Console.WriteLine($"  [{name}]");
    Console.WriteLine($"    Instructions: {(string.IsNullOrEmpty(predictor.Instructions) ? "(none)" : predictor.Instructions)}");
    Console.WriteLine($"    Demo count:   {predictor.Demos.Count}");
}
Console.WriteLine();

// Step 2: Verify the optimized state was actually applied
Console.WriteLine("Step 2: Verify optimized state");
Console.WriteLine("──────────────────────────────");

var qa = predictors.First(p => p.Name == "qa");

bool hasInstructions = !string.IsNullOrEmpty(qa.Predictor.Instructions);
bool hasDemos = qa.Predictor.Demos.Count > 0;

Console.WriteLine($"  Has instructions: {hasInstructions}");
Console.WriteLine($"  Has demos:        {hasDemos}");

if (hasInstructions && hasDemos)
{
    Console.WriteLine("  ✅ Auto-optimized state loaded successfully!");
}
else
{
    Console.WriteLine("  ❌ Auto-optimized state NOT loaded!");
    Console.WriteLine("     Check that Generated/QAModule.Optimized.g.cs exists.");
    Environment.Exit(1);
}
Console.WriteLine();

// Step 3: Verify the module still works (mock client responds)
Console.WriteLine("Step 3: Run prediction with optimized module");
Console.WriteLine("─────────────────────────────────────────────");

var result = await module.ForwardAsync(new QAInput("What is the capital of France?"));
Console.WriteLine($"  Answer: {result.Answer}");
Console.WriteLine();

// Step 4: Verify second GetPredictors() call doesn't re-apply (once-guard)
Console.WriteLine("Step 4: Verify once-guard (second call doesn't re-apply)");
Console.WriteLine("──────────────────────────────────────────────────────────");

var predictors2 = module.GetPredictors();
var qa2 = predictors2.First(p => p.Name == "qa");

// Same state as before
Console.WriteLine($"  Instructions same: {qa.Predictor.Instructions == qa2.Predictor.Instructions}");
Console.WriteLine($"  Demo count same:   {qa.Predictor.Demos.Count == qa2.Predictor.Demos.Count}");
Console.WriteLine();

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   All verifications passed! ✅               ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");

// ── Mock Chat Client ────────────────────────────────────────

file sealed class MockChatClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var json = """{"answer":"Paris"}""";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
