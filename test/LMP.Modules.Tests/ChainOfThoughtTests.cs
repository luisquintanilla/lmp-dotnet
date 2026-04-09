using System.Text.Json;
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

/// <summary>
/// Tests for Phase 3.1 — ChainOfThought reasoning module.
/// Verifies that ChainOfThought wraps prediction with step-by-step reasoning,
/// returns only TOutput to the caller, and captures reasoning in traces.
/// </summary>
public class ChainOfThoughtTests
{
    [Fact]
    public async Task PredictAsync_ReturnsTypedOutputWithoutReasoning()
    {
        var fakeClient = new FakeChatClient();
        var expected = new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "The ticket mentions billing issues, so category is billing. Urgency seems moderate.",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 3 }
        };
        fakeClient.EnqueueResponse(expected);

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        cot.Instructions = "Classify tickets";

        var result = await cot.PredictAsync(
            new TicketInput("Refund", "I want a refund"));

        Assert.Equal("Billing", result.Category);
        Assert.Equal(3, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_CallsClientOnce()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "Thinking...",
            Result = new ClassifyTicket { Category = "Bug", Urgency = 2 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        await cot.PredictAsync(new TicketInput("Bug report", "Something broke"));

        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task PredictAsync_RecordsUnwrappedOutputInTrace()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "Step 1: Read the ticket. Step 2: It's about billing.",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 4 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        cot.Name = "classify_cot";

        var trace = new Trace();
        await cot.PredictAsync(new TicketInput("Overcharge", "I was charged twice"), trace);

        Assert.Single(trace.Entries);
        var entry = trace.Entries[0];
        Assert.Equal("classify_cot", entry.PredictorName);
        Assert.IsType<TicketInput>(entry.Input);

        // The trace output should be the unwrapped TOutput (not ChainOfThoughtResult)
        // so that optimizers can add it as a demo via AddDemo(input, output)
        var output = Assert.IsType<ClassifyTicket>(entry.Output);
        Assert.Equal("Billing", output.Category);
    }

    [Fact]
    public async Task PredictAsync_IncludesCoTInstructionInMessages()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "thinking...",
            Result = new ClassifyTicket { Category = "Feature", Urgency = 1 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        cot.Instructions = "You are a ticket classifier.";

        await cot.PredictAsync(new TicketInput("New feature", "Add dark mode"));

        var messages = fakeClient.SentMessages[0];
        var systemMessage = messages.First(m => m.Role == ChatRole.System);
        Assert.Contains("You are a ticket classifier.", systemMessage.Text);
        Assert.Contains("Let's think step by step.", systemMessage.Text);
    }

    [Fact]
    public async Task PredictAsync_AddsCoTSystemMessage_WhenNoInstructions()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "thinking...",
            Result = new ClassifyTicket { Category = "Account", Urgency = 2 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        // No instructions set

        await cot.PredictAsync(new TicketInput("Reset", "Reset my password"));

        var messages = fakeClient.SentMessages[0];
        // Should still have a system message with CoT instruction
        var systemMessage = messages.First(m => m.Role == ChatRole.System);
        Assert.Contains("Let's think step by step.", systemMessage.Text);
    }

    [Fact]
    public async Task PredictAsync_InheritsFromPredictor()
    {
        var fakeClient = new FakeChatClient();
        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        // ChainOfThought should be a Predictor
        Assert.IsAssignableFrom<Predictor<TicketInput, ClassifyTicket>>(cot);
    }

    [Fact]
    public async Task PredictAsync_ImplementsIPredictor()
    {
        var fakeClient = new FakeChatClient();
        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        // Should be castable to IPredictor
        Assert.IsAssignableFrom<IPredictor>(cot);
    }

    [Fact]
    public async Task PredictAsync_NamePropertyWorks()
    {
        var fakeClient = new FakeChatClient();
        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        cot.Name = "my_cot_predictor";
        Assert.Equal("my_cot_predictor", cot.Name);
    }

    [Fact]
    public async Task PredictAsync_InstructionsPropertyWorks()
    {
        var fakeClient = new FakeChatClient();
        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        cot.Instructions = "Classify tickets by category";
        Assert.Equal("Classify tickets by category", cot.Instructions);
    }

    [Fact]
    public async Task PredictAsync_ConfigPropertyWorks()
    {
        var fakeClient = new FakeChatClient();
        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        cot.Config = new ChatOptions { Temperature = 0.7f };
        Assert.Equal(0.7f, cot.Config.Temperature);
    }

    [Fact]
    public async Task PredictAsync_Retry_OnAssertionFailure()
    {
        var fakeClient = new FakeChatClient();

        // First response: bad urgency
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "First attempt reasoning",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 10 }
        });

        // Second response: valid
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "Corrected reasoning",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 3 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        var result = await cot.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: r => LmpAssert.That(r, x => x.Urgency >= 1 && x.Urgency <= 5,
                "Urgency must be between 1 and 5"));

        Assert.Equal(2, fakeClient.CallCount);
        Assert.Equal(3, result.Urgency);
    }

    [Fact]
    public async Task PredictAsync_ThrowsMaxRetriesExceeded_WhenAllAttemptsFail()
    {
        var fakeClient = new FakeChatClient();

        // Enqueue 4 bad responses (1 initial + 3 retries)
        for (int i = 0; i < 4; i++)
        {
            fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
            {
                Reasoning = $"Attempt {i + 1}",
                Result = new ClassifyTicket { Category = "Billing", Urgency = 10 }
            });
        }

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        await Assert.ThrowsAsync<LmpMaxRetriesExceededException>(() =>
            cot.PredictAsync(
                new TicketInput("Refund", "I want a refund"),
                validate: r => LmpAssert.That(r, x => x.Urgency >= 1 && x.Urgency <= 5,
                    "Urgency must be between 1 and 5")));

        Assert.Equal(4, fakeClient.CallCount); // 1 initial + 3 retries
    }

    [Fact]
    public async Task PredictAsync_RetryIncludesErrorFeedback()
    {
        var fakeClient = new FakeChatClient();

        // First response fails validation
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "thinking...",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 10 }
        });

        // Second response passes
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "corrected",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 3 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        cot.Instructions = "Classify tickets";

        await cot.PredictAsync(
            new TicketInput("Refund", "I want a refund"),
            validate: r => LmpAssert.That(r, x => x.Urgency <= 5, "Urgency too high"));

        // Second call should include error feedback
        var secondCallMessages = fakeClient.SentMessages[1];
        var lastUserMessage = secondCallMessages.Last(m => m.Role == ChatRole.User);
        Assert.Contains("Previous attempt failed", lastUserMessage.Text);
    }

    [Fact]
    public async Task PredictAsync_CancellationToken_IsPassed()
    {
        var fakeClient = new FakeChatClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        // FakeChatClient checks cancellation token
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cot.PredictAsync(
                new TicketInput("Test", "test"),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PredictAsync_ThrowsOnNullResult()
    {
        var fakeClient = new FakeChatClient();
        // Return JSON that will deserialize with null Result
        fakeClient.EnqueueJsonResponse("{\"reasoning\":\"thinking...\",\"result\":null}");

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);

        // Should throw because Result is null
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cot.PredictAsync(new TicketInput("Test", "test")));
    }

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ChainOfThought<TicketInput, ClassifyTicket>(null!));
    }

    [Fact]
    public void ChainOfThoughtResult_RoundTrips()
    {
        var original = new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "Step 1: analyze. Step 2: classify.",
            Result = new ClassifyTicket { Category = "Technical", Urgency = 4 }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ChainOfThoughtResult<ClassifyTicket>>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Step 1: analyze. Step 2: classify.", deserialized!.Reasoning);
        Assert.Equal("Technical", deserialized.Result.Category);
        Assert.Equal(4, deserialized.Result.Urgency);
    }

    [Fact]
    public void ChainOfThoughtResult_ReasoningAppearsFirst_InJson()
    {
        var result = new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "thinking...",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 1 }
        };

        var json = JsonSerializer.Serialize(result);
        var reasoningIndex = json.IndexOf("\"Reasoning\"", StringComparison.OrdinalIgnoreCase);
        var resultIndex = json.IndexOf("\"Result\"", StringComparison.OrdinalIgnoreCase);

        // Due to [JsonPropertyOrder(-1)], Reasoning should be first
        Assert.True(reasoningIndex < resultIndex,
            $"Reasoning should appear before Result in JSON. Got: {json}");
    }

    [Fact]
    public async Task PredictAsync_DemosAreIncludedInMessages()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "thinking...",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 2 }
        });

        var cot = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        cot.Instructions = "Classify tickets";
        cot.Demos.Add((
            new TicketInput("Payment issue", "Double charged"),
            new ClassifyTicket { Category = "Billing", Urgency = 4 }));

        await cot.PredictAsync(new TicketInput("Refund", "Want my money back"));

        var messages = fakeClient.SentMessages[0];
        // Should have: system, demo user, demo assistant, current user = 4 messages
        Assert.True(messages.Count >= 4,
            $"Expected at least 4 messages (system + demo pair + input), got {messages.Count}");
    }

    [Fact]
    public async Task PredictAsync_TraceOutputIsCompatibleWithAddDemo()
    {
        // Regression test: ChainOfThought trace entries must be usable as demos
        // by optimizers via IPredictor.AddDemo(input, output). Previously, the
        // trace recorded ChainOfThoughtResult<T> which caused InvalidCastException.
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new ChainOfThoughtResult<ClassifyTicket>
        {
            Reasoning = "It mentions billing, so...",
            Result = new ClassifyTicket { Category = "Billing", Urgency = 3 }
        });

        var teacher = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        teacher.Name = "classify";

        var trace = new Trace();
        await teacher.PredictAsync(new TicketInput("Bill", "Overcharged"), trace);

        // Simulate what BootstrapFewShot does: take trace entries and add as demos
        var student = new ChainOfThought<TicketInput, ClassifyTicket>(fakeClient);
        var entry = trace.Entries[0];

        // This must not throw InvalidCastException
        IPredictor studentPredictor = student;
        studentPredictor.AddDemo(entry.Input, entry.Output);

        Assert.Single(student.Demos);
        Assert.Equal("Billing", student.Demos[0].Output.Category);
    }
}
