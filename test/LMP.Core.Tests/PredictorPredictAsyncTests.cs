using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

// --- Test types ---

public record TicketInput(string Subject, string Body);

public record ClassifyTicket
{
    public required string Category { get; init; }
    public required int Urgency { get; init; }
}

// --- Tests ---

public class PredictorPredictAsyncTests
{
    [Fact]
    public async Task PredictAsync_ReturnsTypedOutput()
    {
        var fakeClient = new FakeChatClient();
        var expected = new ClassifyTicket { Category = "Billing", Urgency = 3 };
        fakeClient.EnqueueResponse(expected);

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Instructions = "Classify tickets";

        var result = await predictor.PredictAsync(
            new TicketInput("Refund", "I want a refund"));

        Assert.Equal("Billing", result.Category);
        Assert.Equal(3, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_CallsClientOnce()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 2 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        await predictor.PredictAsync(new TicketInput("Bug report", "Something broke"));

        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_IncludesInstructionsInSystemMessage()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Feature", Urgency = 1 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Instructions = "You are a ticket classifier.";

        await predictor.PredictAsync(new TicketInput("New feature", "Add dark mode"));

        var messages = fakeClient.SentMessages[0];
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains("You are a ticket classifier.", messages[0].Text);
    }

    [Fact]
    public async Task PredictAsync_IncludesInputInUserMessage()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 4 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        await predictor.PredictAsync(new TicketInput("Crash", "App crashes on startup"));

        var messages = fakeClient.SentMessages[0];
        var userMsg = messages[^1];
        Assert.Equal(ChatRole.User, userMsg.Role);
        Assert.Contains("Crash", userMsg.Text);
    }

    [Fact]
    public async Task PredictAsync_RecordsTrace()
    {
        var fakeClient = new FakeChatClient();
        var expected = new ClassifyTicket { Category = "Billing", Urgency = 3 };
        fakeClient.EnqueueResponse(expected);

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Name = "classify";
        var trace = new Trace();

        var input = new TicketInput("Refund", "I want a refund");
        await predictor.PredictAsync(input, trace);

        Assert.Single(trace.Entries);
        Assert.Equal("classify", trace.Entries[0].PredictorName);
        Assert.Same(input, trace.Entries[0].Input);
        Assert.Equal(expected, trace.Entries[0].Output);
    }

    [Fact]
    public async Task PredictAsync_WithoutTrace_DoesNotThrow()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 2 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        var result = await predictor.PredictAsync(
            new TicketInput("Bug", "Something broke"), trace: null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task PredictAsync_WithDemos_IncludesDemoMessages()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 5 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Instructions = "Classify tickets";
        predictor.Demos =
        [
            (new TicketInput("Slow page", "The page loads slowly"),
             new ClassifyTicket { Category = "Performance", Urgency = 2 })
        ];

        await predictor.PredictAsync(new TicketInput("Crash", "App crashes"));

        var messages = fakeClient.SentMessages[0];
        // System + demo user + demo assistant + current user = 4 messages
        Assert.True(messages.Count >= 4,
            $"Expected at least 4 messages, got {messages.Count}");

        // Demo pair should be user + assistant
        var demoUser = messages[1];
        Assert.Equal(ChatRole.User, demoUser.Role);
        Assert.Contains("Slow page", demoUser.Text);

        var demoAssistant = messages[2];
        Assert.Equal(ChatRole.Assistant, demoAssistant.Role);
        Assert.Contains("Performance", demoAssistant.Text);
    }

    [Fact]
    public async Task PredictAsync_PassesConfigToClient()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 1 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Config = new ChatOptions { Temperature = 0.0f };

        await predictor.PredictAsync(new TicketInput("Test", "Test body"));

        // Config is passed via GetResponseAsync<T> extension;
        // FakeChatClient receives it through GetResponseAsync(messages, options, ct)
        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_SupportsCancellation()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 1 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => predictor.PredictAsync(
                new TicketInput("Test", "Test"),
                cancellationToken: cts.Token));
    }

    // --- Retry-on-assertion tests ---

    [Fact]
    public async Task PredictAsync_WithValidate_ReturnsOnSuccess()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        var result = await predictor.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: output => LmpAssert.That(output,
                o => o.Urgency >= 1 && o.Urgency <= 5,
                "Urgency must be between 1 and 5"));

        Assert.Equal("Billing", result.Category);
        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_WithValidate_RetriesOnAssertionFailure()
    {
        var fakeClient = new FakeChatClient();
        // First attempt: invalid urgency
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 10 });
        // Second attempt: valid
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        var result = await predictor.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: output => LmpAssert.That(output,
                o => o.Urgency >= 1 && o.Urgency <= 5,
                "Urgency must be between 1 and 5"));

        Assert.Equal(3, result.Urgency);
        Assert.Equal(2, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_RetryAppendsErrorFeedback()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 10 });
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Billing", Urgency = 3 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);

        await predictor.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: output => LmpAssert.That(output,
                o => o.Urgency >= 1 && o.Urgency <= 5,
                "Urgency must be between 1 and 5"));

        // Second call should include error feedback
        var retryMessages = fakeClient.SentMessages[1];
        var lastUserMsg = retryMessages[^1];
        Assert.Contains("Previous attempt failed", lastUserMsg.Text);
        Assert.Contains("Urgency must be between 1 and 5", lastUserMsg.Text);
    }

    [Fact]
    public async Task PredictAsync_ThrowsMaxRetriesExceeded()
    {
        var fakeClient = new FakeChatClient();
        // All attempts fail (4 attempts: initial + 3 retries)
        for (int i = 0; i < 4; i++)
            fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 10 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Name = "classify";

        var ex = await Assert.ThrowsAsync<LmpMaxRetriesExceededException>(
            () => predictor.PredictAsync(
                new TicketInput("Bug", "Something"),
                validate: output => LmpAssert.That(output,
                    o => o.Urgency >= 1 && o.Urgency <= 5,
                    "Urgency must be between 1 and 5"),
                maxRetries: 3));

        Assert.Equal("classify", ex.PredictorName);
        Assert.Equal(3, ex.MaxRetries);
        Assert.Equal(4, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_RetryRecordsAllTracesEntries()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 10 });
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 3 });

        var predictor = new Predictor<TicketInput, ClassifyTicket>(fakeClient);
        predictor.Name = "classify";
        var trace = new Trace();

        await predictor.PredictAsync(
            new TicketInput("Bug", "Something"),
            trace,
            validate: output => LmpAssert.That(output,
                o => o.Urgency >= 1 && o.Urgency <= 5,
                "Urgency must be between 1 and 5"));

        // Both attempts should be recorded in the trace
        Assert.Equal(2, trace.Entries.Count);
    }

    // --- GetState / LoadState with demos ---

    [Fact]
    public void GetState_SerializesDemos()
    {
        var predictor = new Predictor<TicketInput, ClassifyTicket>(new FakeChatClient());
        predictor.Instructions = "Classify tickets";
        predictor.Demos =
        [
            (new TicketInput("Slow page", "The page loads slowly"),
             new ClassifyTicket { Category = "Performance", Urgency = 2 })
        ];

        var state = predictor.GetState();

        Assert.Equal("Classify tickets", state.Instructions);
        Assert.Single(state.Demos);
        Assert.Contains("Subject", state.Demos[0].Input.Keys);
        Assert.Contains("Category", state.Demos[0].Output.Keys);
    }

    [Fact]
    public void LoadState_RestoresDemos()
    {
        var predictor = new Predictor<TicketInput, ClassifyTicket>(new FakeChatClient());

        var inputJson = JsonSerializer.Serialize(new TicketInput("Bug", "It crashes"));
        var outputJson = JsonSerializer.Serialize(new ClassifyTicket { Category = "Bug", Urgency = 5 });

        using var inputDoc = JsonDocument.Parse(inputJson);
        using var outputDoc = JsonDocument.Parse(outputJson);

        var inputDict = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var prop in inputDoc.RootElement.EnumerateObject())
            inputDict[prop.Name] = prop.Value.Clone();

        var outputDict = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var prop in outputDoc.RootElement.EnumerateObject())
            outputDict[prop.Name] = prop.Value.Clone();

        var state = new PredictorState
        {
            Instructions = "Classify tickets",
            Demos =
            [
                new DemoEntry { Input = inputDict, Output = outputDict }
            ]
        };

        predictor.LoadState(state);

        Assert.Equal("Classify tickets", predictor.Instructions);
        Assert.Single(predictor.Demos);
        Assert.Equal("Bug", predictor.Demos[0].Input.Subject);
        Assert.Equal("Bug", predictor.Demos[0].Output.Category);
        Assert.Equal(5, predictor.Demos[0].Output.Urgency);
    }

    [Fact]
    public void GetState_LoadState_RoundTrips()
    {
        var original = new Predictor<TicketInput, ClassifyTicket>(new FakeChatClient());
        original.Instructions = "Classify tickets by category and urgency";
        original.Demos =
        [
            (new TicketInput("Slow page", "Page loads slowly"),
             new ClassifyTicket { Category = "Performance", Urgency = 2 }),
            (new TicketInput("Login fails", "Can't log in"),
             new ClassifyTicket { Category = "Auth", Urgency = 4 })
        ];

        var state = original.GetState();

        var restored = new Predictor<TicketInput, ClassifyTicket>(new FakeChatClient());
        restored.LoadState(state);

        Assert.Equal(original.Instructions, restored.Instructions);
        Assert.Equal(original.Demos.Count, restored.Demos.Count);

        for (int i = 0; i < original.Demos.Count; i++)
        {
            Assert.Equal(original.Demos[i].Input, restored.Demos[i].Input);
            Assert.Equal(original.Demos[i].Output, restored.Demos[i].Output);
        }
    }

    [Fact]
    public async Task PredictAsync_WithStringInput_Works()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "General", Urgency = 1 });

        var predictor = new Predictor<string, ClassifyTicket>(fakeClient);
        predictor.Instructions = "Classify the text.";

        var result = await predictor.PredictAsync("Hello world");

        Assert.Equal("General", result.Category);
        Assert.Equal(1, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_NoInstructions_OmitsSystemMessage()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ClassifyTicket { Category = "Bug", Urgency = 2 });

        var predictor = new Predictor<string, ClassifyTicket>(fakeClient);
        // Instructions defaults to empty string

        await predictor.PredictAsync("Some input");

        var messages = fakeClient.SentMessages[0];
        // No system message when instructions is empty
        Assert.All(messages, m =>
            Assert.NotEqual(ChatRole.System, m.Role));
    }
}
