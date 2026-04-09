using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests for Phase 3.3 — Refine iterative improvement module.
/// Verifies that Refine executes predict → critique → re-predict loop,
/// records all steps in the trace, and returns the final refined result.
/// </summary>
public class RefineTests
{
    // --- Constructor tests ---

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Refine<TicketInput, ClassifyTicket>(null!));
    }

    [Fact]
    public void Constructor_ThrowsOnMaxIterationsLessThanOne()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 0));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeMaxIterations()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Refine<TicketInput, ClassifyTicket>(client, maxIterations: -1));
    }

    [Fact]
    public void Constructor_AcceptsValidArgs()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 3);
        Assert.Equal(3, refine.MaxIterations);
    }

    [Fact]
    public void Constructor_DefaultMaxIterationsIsTwo()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        Assert.Equal(2, refine.MaxIterations);
    }

    // --- Inheritance and interface tests ---

    [Fact]
    public void InheritsFromPredictor()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        Assert.IsAssignableFrom<Predictor<TicketInput, ClassifyTicket>>(refine);
    }

    [Fact]
    public void ImplementsIPredictor()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        Assert.IsAssignableFrom<IPredictor>(refine);
    }

    [Fact]
    public void LearnableState_InstructionsAccessible()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        refine.Instructions = "Classify support tickets";
        Assert.Equal("Classify support tickets", refine.Instructions);
    }

    [Fact]
    public void LearnableState_DemosAccessible()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        refine.Demos.Add((
            new TicketInput("Payment", "Double charged"),
            new ClassifyTicket { Category = "Billing", Urgency = 4 }));
        Assert.Single(refine.Demos);
    }

    [Fact]
    public void LearnableState_ConfigAccessible()
    {
        var client = new FakeChatClient();
        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        refine.Config = new ChatOptions { Temperature = 0.7f };
        Assert.Equal(0.7f, refine.Config.Temperature);
    }

    // --- PredictAsync basic tests ---

    [Fact]
    public async Task PredictAsync_ReturnsFinalRefinedResult()
    {
        var client = new FakeChatClient();

        // 1 initial + 2 refinements (default maxIterations=2) = 3 calls
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        var result = await refine.PredictAsync(
            new TicketInput("Refund", "I want a refund"));

        // Should return the last (3rd) response — the final refinement
        Assert.Equal(4, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_MakesCorrectNumberOfCalls()
    {
        var client = new FakeChatClient();

        // maxIterations=3: 1 initial + 3 refinement = 4 calls
        for (int i = 0; i < 4; i++)
            client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = i + 1 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 3);

        await refine.PredictAsync(new TicketInput("Test", "Body"));

        Assert.Equal(4, client.CallCount);
    }

    [Fact]
    public async Task PredictAsync_MaxIterations1_MakesTwoCalls()
    {
        var client = new FakeChatClient();

        // 1 initial + 1 refinement = 2 calls
        client.EnqueueResponse(new ClassifyTicket { Category = "Technical", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Technical", Urgency = 5 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);

        var result = await refine.PredictAsync(
            new TicketInput("Bug", "App crashes"));

        Assert.Equal(5, result.Urgency);
        Assert.Equal(2, client.CallCount);
    }

    // --- Trace recording tests ---

    [Fact]
    public async Task PredictAsync_RecordsAllStepsInTrace()
    {
        var client = new FakeChatClient();

        // 1 initial + 2 refinements = 3 trace entries
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        refine.Name = "classify_refine";

        var trace = new Trace();
        await refine.PredictAsync(
            new TicketInput("Refund", "Money back"), trace);

        // Initial prediction + 2 refinements = 3 entries
        Assert.Equal(3, trace.Entries.Count);
    }

    [Fact]
    public async Task PredictAsync_TraceFirstEntryIsInitialPrediction()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);
        refine.Name = "classify_refine";

        var trace = new Trace();
        await refine.PredictAsync(
            new TicketInput("Refund", "Money back"), trace);

        // First trace entry is the initial prediction with the original input
        var first = trace.Entries[0];
        Assert.Equal("classify_refine", first.PredictorName);
        Assert.IsType<TicketInput>(first.Input);
        Assert.IsType<ClassifyTicket>(first.Output);
    }

    [Fact]
    public async Task PredictAsync_TraceRefinementEntriesContainCritiqueInput()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);

        var trace = new Trace();
        await refine.PredictAsync(
            new TicketInput("Refund", "Money back"), trace);

        // Second trace entry is the refinement with RefineCritiqueInput
        var second = trace.Entries[1];
        Assert.IsType<RefineCritiqueInput<ClassifyTicket>>(second.Input);
        Assert.IsType<ClassifyTicket>(second.Output);
    }

    [Fact]
    public async Task PredictAsync_RefinementInputContainsPreviousOutput()
    {
        var client = new FakeChatClient();

        var initial = new ClassifyTicket { Category = "Billing", Urgency = 2 };
        var refined = new ClassifyTicket { Category = "Billing", Urgency = 4 };

        client.EnqueueResponse(initial);
        client.EnqueueResponse(refined);

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);

        var trace = new Trace();
        await refine.PredictAsync(
            new TicketInput("Refund", "Money back"), trace);

        // The refinement input should carry the previous output
        var critiqueInput = (RefineCritiqueInput<ClassifyTicket>)trace.Entries[1].Input;
        Assert.Equal(2, critiqueInput.PreviousOutput.Urgency);
        Assert.Equal("Billing", critiqueInput.PreviousOutput.Category);
    }

    [Fact]
    public async Task PredictAsync_MultipleRefinements_ChainOutputs()
    {
        var client = new FakeChatClient();

        // 1 initial + 3 refinements
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 1 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 3);

        var trace = new Trace();
        await refine.PredictAsync(
            new TicketInput("Test", "Body"), trace);

        Assert.Equal(4, trace.Entries.Count);

        // Each refinement should carry the previous output
        var critique1 = (RefineCritiqueInput<ClassifyTicket>)trace.Entries[1].Input;
        Assert.Equal(1, critique1.PreviousOutput.Urgency);

        var critique2 = (RefineCritiqueInput<ClassifyTicket>)trace.Entries[2].Input;
        Assert.Equal(2, critique2.PreviousOutput.Urgency);

        var critique3 = (RefineCritiqueInput<ClassifyTicket>)trace.Entries[3].Input;
        Assert.Equal(3, critique3.PreviousOutput.Urgency);
    }

    // --- Prompt / Instructions tests ---

    [Fact]
    public async Task PredictAsync_UsesInstructionsForInitialPrediction()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);
        refine.Instructions = "Classify support tickets by category and urgency";

        await refine.PredictAsync(new TicketInput("Refund", "I want a refund"));

        // First call should use the initial instructions
        var firstMessages = client.SentMessages[0];
        var systemMessage = firstMessages.First(m => m.Role == ChatRole.System);
        Assert.Contains("Classify support tickets by category and urgency", systemMessage.Text);
    }

    [Fact]
    public async Task PredictAsync_RefinerHasCritiqueInstructions()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        await refine.PredictAsync(new TicketInput("Refund", "I want a refund"));

        // Second call (first refinement) should use refiner instructions
        var refinementMessages = client.SentMessages[1];
        var systemMessage = refinementMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMessage);
        Assert.Contains("improved output", systemMessage!.Text);
    }

    // --- Cancellation tests ---

    [Fact]
    public async Task PredictAsync_CancellationTokenPropagates()
    {
        var client = new FakeChatClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            refine.PredictAsync(
                new TicketInput("Test", "test"),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PredictAsync_CancellationDuringRefinement()
    {
        // Provide initial response but cancel during refinement
        var cts = new CancellationTokenSource();
        var client = new CancellingFakeChatClient(cts, cancelOnCall: 2);

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            refine.PredictAsync(
                new TicketInput("Test", "test"),
                cancellationToken: cts.Token));
    }

    // --- Validation tests ---

    [Fact]
    public async Task PredictAsync_WithValidate_PassesOnValidFinalResult()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 5 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        var result = await refine.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: r => LmpAssert.That(r, x => x.Urgency <= 5, "Urgency must be ≤ 5"));

        Assert.Equal(5, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_WithValidate_ThrowsOnInvalidFinalResult()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 8 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 9 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 10 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client);

        await Assert.ThrowsAsync<LmpAssertionException>(() =>
            refine.PredictAsync(
                new TicketInput("Refund", "I want a refund"),
                validate: r => LmpAssert.That(r, x => x.Urgency <= 5, "Urgency must be ≤ 5")));
    }

    // --- Edge case tests ---

    [Fact]
    public async Task PredictAsync_WithDemos_IncludesDemosInInitialPrediction()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);
        refine.Instructions = "Classify tickets";
        refine.Demos.Add((
            new TicketInput("Payment issue", "Double charged"),
            new ClassifyTicket { Category = "Billing", Urgency = 4 }));

        await refine.PredictAsync(new TicketInput("Refund", "Want my money back"));

        // First call should include demo messages
        var messages = client.SentMessages[0];
        // system + demo user + demo assistant + current user = 4 messages
        Assert.True(messages.Count >= 4,
            $"Expected at least 4 messages (system + demo pair + input), got {messages.Count}");
    }

    [Fact]
    public async Task PredictAsync_RefinementInputContainsOriginalInput()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 1);

        var trace = new Trace();
        var originalInput = new TicketInput("Refund", "Money back");
        await refine.PredictAsync(originalInput, trace);

        var critiqueInput = (RefineCritiqueInput<ClassifyTicket>)trace.Entries[1].Input;
        Assert.Equal(originalInput, critiqueInput.OriginalInput);
    }

    [Fact]
    public async Task PredictAsync_ReturnsLastIterationResult()
    {
        var client = new FakeChatClient();

        // 5 iterations: 1 initial + 5 refinements = 6 calls
        for (int i = 0; i < 6; i++)
            client.EnqueueResponse(new ClassifyTicket { Category = $"Cat{i}", Urgency = i });

        var refine = new Refine<TicketInput, ClassifyTicket>(client, maxIterations: 5);

        var result = await refine.PredictAsync(
            new TicketInput("Test", "Body"));

        // Should return the last refinement (urgency=5, category="Cat5")
        Assert.Equal(5, result.Urgency);
        Assert.Equal("Cat5", result.Category);
    }
}

/// <summary>
/// A fake chat client that cancels a <see cref="CancellationTokenSource"/> on a specific call number,
/// used to test cancellation during refinement iterations.
/// </summary>
internal sealed class CancellingFakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    private readonly CancellationTokenSource _cts;
    private readonly int _cancelOnCall;
    private int _callCount;

    public CancellingFakeChatClient(CancellationTokenSource cts, int cancelOnCall)
    {
        _cts = cts;
        _cancelOnCall = cancelOnCall;
    }

    public void EnqueueResponse<T>(T value) where T : class
    {
        _responses.Enqueue(JsonSerializer.Serialize(value));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callCount++;

        if (_callCount >= _cancelOnCall)
        {
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_responses.Count == 0)
            throw new InvalidOperationException("CancellingFakeChatClient: no more responses.");

        var json = _responses.Dequeue();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
