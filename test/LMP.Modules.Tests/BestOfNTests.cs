using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests for Phase 3.2 — BestOfN parallel selection module.
/// Verifies that BestOfN fires N concurrent predictions, scores each with a reward
/// function, and returns the highest-scoring candidate.
/// </summary>
public class BestOfNTests
{
    // --- Reward functions ---

    private static float UrgencyReward(TicketInput input, ClassifyTicket output)
        => output.Urgency;

    private static float NegativeUrgencyReward(TicketInput input, ClassifyTicket output)
        => -output.Urgency;

    // --- Constructor tests ---

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BestOfN<TicketInput, ClassifyTicket>(null!, 3, UrgencyReward));
    }

    [Fact]
    public void Constructor_ThrowsOnNLessThanOne()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BestOfN<TicketInput, ClassifyTicket>(client, 0, UrgencyReward));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeN()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BestOfN<TicketInput, ClassifyTicket>(client, -1, UrgencyReward));
    }

    [Fact]
    public void Constructor_ThrowsOnNullReward()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentNullException>(() =>
            new BestOfN<TicketInput, ClassifyTicket>(client, 3, null!));
    }

    [Fact]
    public void Constructor_AcceptsValidArgs()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 5, UrgencyReward);
        Assert.Equal(5, bestOfN.N);
    }

    // --- Inheritance and interface tests ---

    [Fact]
    public void InheritsFromPredictor()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        Assert.IsAssignableFrom<Predictor<TicketInput, ClassifyTicket>>(bestOfN);
    }

    [Fact]
    public void ImplementsIPredictor()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        Assert.IsAssignableFrom<IPredictor>(bestOfN);
    }

    [Fact]
    public void LearnableState_InstructionsAccessible()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        bestOfN.Instructions = "Classify support tickets";
        Assert.Equal("Classify support tickets", bestOfN.Instructions);
    }

    [Fact]
    public void LearnableState_DemosAccessible()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        bestOfN.Demos.Add((
            new TicketInput("Payment", "Double charged"),
            new ClassifyTicket { Category = "Billing", Urgency = 4 }));
        Assert.Single(bestOfN.Demos);
    }

    [Fact]
    public void LearnableState_ConfigAccessible()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        bestOfN.Config = new ChatOptions { Temperature = 0.9f };
        Assert.Equal(0.9f, bestOfN.Config.Temperature);
    }

    // --- PredictAsync tests ---

    [Fact]
    public async Task PredictAsync_ReturnsHighestScoringCandidate()
    {
        var client = new FakeChatClient();

        // Enqueue 3 responses with different urgencies
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 5 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Refund", "I want a refund"));

        // Reward is Urgency, so highest urgency wins
        Assert.Equal(5, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_ReturnsLowestWhenRewardIsNegated()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 5 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        // Negative reward: prefer lowest urgency
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, NegativeUrgencyReward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Question", "How do I reset?"));

        Assert.Equal(2, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_N1_ReturnsSingleResult()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(new ClassifyTicket { Category = "Technical", Urgency = 4 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 1, UrgencyReward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Bug", "App crashes on launch"));

        Assert.Equal("Technical", result.Category);
        Assert.Equal(4, result.Urgency);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task PredictAsync_MakesExactlyNCalls()
    {
        var client = new FakeChatClient();

        for (int i = 0; i < 5; i++)
            client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = i + 1 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 5, UrgencyReward);

        await bestOfN.PredictAsync(
            new TicketInput("Payment", "Charged twice"));

        Assert.Equal(5, client.CallCount);
    }

    [Fact]
    public async Task PredictAsync_AllNCalls_RunConcurrently()
    {
        // Use a client that introduces a small delay to verify concurrency
        var client = new DelayedFakeChatClient(delayMs: 100);

        for (int i = 0; i < 5; i++)
            client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = i + 1 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 5, UrgencyReward);

        var sw = Stopwatch.StartNew();
        await bestOfN.PredictAsync(
            new TicketInput("Payment", "Charged twice"));
        sw.Stop();

        // If sequential: 5 × 100ms = 500ms+. If parallel: ~100ms.
        // Allow generous margin but expect significantly less than sequential.
        Assert.True(sw.ElapsedMilliseconds < 400,
            $"Expected parallel execution (<400ms), but took {sw.ElapsedMilliseconds}ms. " +
            "Predictions may be running sequentially instead of concurrently.");
    }

    [Fact]
    public async Task PredictAsync_RecordsAllCandidatesInTrace()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 1 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Technical", Urgency = 3 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Account", Urgency = 2 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);
        bestOfN.Name = "classify_best";

        var trace = new Trace();
        await bestOfN.PredictAsync(
            new TicketInput("Question", "How to?"), trace);

        // All 3 candidates should be recorded in the trace
        Assert.Equal(3, trace.Entries.Count);
        Assert.All(trace.Entries, entry =>
        {
            Assert.Equal("classify_best", entry.PredictorName);
            Assert.IsType<TicketInput>(entry.Input);
            Assert.IsType<ClassifyTicket>(entry.Output);
        });
    }

    [Fact]
    public async Task PredictAsync_CancellationTokenPropagates()
    {
        var client = new FakeChatClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, UrgencyReward);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bestOfN.PredictAsync(
                new TicketInput("Test", "test"),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PredictAsync_UsesInstructionsInPrompt()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 1, UrgencyReward);
        bestOfN.Instructions = "Classify support tickets by category";

        await bestOfN.PredictAsync(
            new TicketInput("Refund", "I want a refund"));

        var messages = client.SentMessages[0];
        var systemMessage = messages.First(m => m.Role == ChatRole.System);
        Assert.Contains("Classify support tickets by category", systemMessage.Text);
    }

    [Fact]
    public async Task PredictAsync_UsesDemosInPrompt()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 1, UrgencyReward);
        bestOfN.Instructions = "Classify tickets";
        bestOfN.Demos.Add((
            new TicketInput("Payment issue", "Double charged"),
            new ClassifyTicket { Category = "Billing", Urgency = 4 }));

        await bestOfN.PredictAsync(
            new TicketInput("Refund", "Want my money back"));

        var messages = client.SentMessages[0];
        // Should have: system, demo user, demo assistant, current user = 4 messages
        Assert.True(messages.Count >= 4,
            $"Expected at least 4 messages (system + demo pair + input), got {messages.Count}");
    }

    [Fact]
    public async Task PredictAsync_RewardReceivesCorrectInput()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var capturedInput = (TicketInput?)null;
        float customReward(TicketInput input, ClassifyTicket output)
        {
            capturedInput = input;
            return output.Urgency;
        }

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 1, customReward);
        var inputTicket = new TicketInput("Test", "Test body");

        await bestOfN.PredictAsync(inputTicket);

        Assert.NotNull(capturedInput);
        Assert.Equal("Test", capturedInput!.Subject);
        Assert.Equal("Test body", capturedInput.Body);
    }

    [Fact]
    public async Task PredictAsync_WithValidate_PassesOnValidBest()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 2 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 4 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 2, UrgencyReward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: r => LmpAssert.That(r, x => x.Urgency <= 5, "Urgency must be ≤ 5"));

        Assert.Equal(4, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_WithValidate_ThrowsOnInvalidBest()
    {
        var client = new FakeChatClient();

        // All candidates have urgency > 5
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 8 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 9 });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 2, UrgencyReward);

        await Assert.ThrowsAsync<LmpAssertionException>(() =>
            bestOfN.PredictAsync(
                new TicketInput("Refund", "I want a refund"),
                validate: r => LmpAssert.That(r, x => x.Urgency <= 5, "Urgency must be ≤ 5")));
    }

    [Fact]
    public async Task PredictAsync_N5_SelectsBestFromFive()
    {
        var client = new FakeChatClient();

        var urgencies = new[] { 3, 1, 5, 2, 4 };
        foreach (var u in urgencies)
            client.EnqueueResponse(new ClassifyTicket { Category = "Mixed", Urgency = u });

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 5, UrgencyReward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Ticket", "Various urgencies"));

        Assert.Equal(5, result.Urgency);
        Assert.Equal(5, client.CallCount);
    }

    [Fact]
    public async Task PredictAsync_WithCustomCategoryReward()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 5 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Technical", Urgency = 1 });
        client.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        // Reward prefers "Technical" category regardless of urgency
        float reward(TicketInput _, ClassifyTicket output)
            => output.Category == "Technical" ? 100f : 0f;

        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 3, reward);

        var result = await bestOfN.PredictAsync(
            new TicketInput("Bug", "System error"));

        Assert.Equal("Technical", result.Category);
    }

    [Fact]
    public void NProperty_ReturnsConstructorValue()
    {
        var client = new FakeChatClient();
        var bestOfN = new BestOfN<TicketInput, ClassifyTicket>(client, 7, UrgencyReward);
        Assert.Equal(7, bestOfN.N);
    }
}

/// <summary>
/// A variant of FakeChatClient that adds an artificial delay to simulate real LM latency,
/// used to verify BestOfN runs predictions concurrently.
/// </summary>
internal sealed class DelayedFakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    private readonly int _delayMs;
    private readonly object _lock = new();

    public DelayedFakeChatClient(int delayMs)
    {
        _delayMs = delayMs;
    }

    public void EnqueueResponse<T>(T value) where T : class
    {
        _responses.Enqueue(JsonSerializer.Serialize(value));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Simulate network latency — all N tasks should run this delay concurrently
        await Task.Delay(_delayMs, cancellationToken);

        string json;
        lock (_lock)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("DelayedFakeChatClient: no more responses.");
            json = _responses.Dequeue();
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
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
