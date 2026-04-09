using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

// --- Test types ---

public record AgentInput(string Query);

public record AgentOutput
{
    public required string Answer { get; init; }
    public required string Source { get; init; }
}

// --- Tests ---

/// <summary>
/// Tests for Phase 5.1 — ReActAgent reasoning module.
/// Verifies tool-augmented reasoning, trace recording, validation/retry,
/// and IPredictor compatibility.
/// </summary>
public class ReActAgentTests
{
    // === Constructor tests ===

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new FakeChatClient();
        var tools = new List<AIFunction>();

        var agent = new ReActAgent<AgentInput, AgentOutput>(client, tools);

        Assert.Equal(5, agent.MaxSteps);
        Assert.Empty(agent.Tools);
        Assert.NotNull(agent.Config);
        Assert.Empty(agent.Demos);
    }

    [Fact]
    public void Constructor_CustomMaxSteps()
    {
        var client = new FakeChatClient();
        var tools = new List<AIFunction>();

        var agent = new ReActAgent<AgentInput, AgentOutput>(client, tools, maxSteps: 10);

        Assert.Equal(10, agent.MaxSteps);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReActAgent<AgentInput, AgentOutput>(null!, []));
    }

    [Fact]
    public void Constructor_NullTools_Throws()
    {
        var client = new FakeChatClient();

        Assert.Throws<ArgumentNullException>(() =>
            new ReActAgent<AgentInput, AgentOutput>(client, null!));
    }

    [Fact]
    public void Constructor_MaxStepsLessThan1_Throws()
    {
        var client = new FakeChatClient();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReActAgent<AgentInput, AgentOutput>(client, [], maxSteps: 0));
    }

    // === PredictAsync — basic tests (no tool calls) ===

    [Fact]
    public async Task PredictAsync_ReturnsTypedOutput()
    {
        var fakeClient = new FakeChatClient();
        var expected = new AgentOutput { Answer = "42", Source = "knowledge" };
        fakeClient.EnqueueResponse(expected);

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);
        agent.Instructions = "Answer questions";

        var result = await agent.PredictAsync(new AgentInput("What is the answer?"));

        Assert.Equal("42", result.Answer);
        Assert.Equal("knowledge", result.Source);
    }

    [Fact]
    public async Task PredictAsync_RecordsTrace()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AgentOutput { Answer = "test", Source = "src" });

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);
        var trace = new Trace();

        var input = new AgentInput("question?");
        await agent.PredictAsync(input, trace);

        Assert.Single(trace.Entries);
        Assert.Equal(input, trace.Entries[0].Input);
        Assert.IsType<AgentOutput>(trace.Entries[0].Output);
    }

    [Fact]
    public async Task PredictAsync_SetsInstructionsInPrompt()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AgentOutput { Answer = "yes", Source = "brain" });

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);
        agent.Instructions = "You are a helpful assistant";

        await agent.PredictAsync(new AgentInput("Hi"));

        Assert.Single(fakeClient.SentMessages);
        var messages = fakeClient.SentMessages[0];
        Assert.Contains(messages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("You are a helpful assistant"));
    }

    [Fact]
    public async Task PredictAsync_NullOutput_Throws()
    {
        var fakeClient = new FakeChatClient();
        // Enqueue a response that will deserialize to null for a class
        fakeClient.EnqueueJsonResponse("null");

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.PredictAsync(new AgentInput("test")));
    }

    // === Validation and retry ===

    [Fact]
    public async Task PredictAsync_ValidationPasses_ReturnsImmediately()
    {
        var fakeClient = new FakeChatClient();
        var expected = new AgentOutput { Answer = "good", Source = "test" };
        fakeClient.EnqueueResponse(expected);

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);
        var callCount = 0;

        var result = await agent.PredictAsync(
            new AgentInput("q"),
            validate: output =>
            {
                callCount++;
                // passes
            });

        Assert.Equal("good", result.Answer);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PredictAsync_ValidationFails_RetriesWithFeedback()
    {
        var fakeClient = new FakeChatClient();
        // First attempt — fails validation
        fakeClient.EnqueueResponse(new AgentOutput { Answer = "bad", Source = "v1" });
        // Second attempt — passes
        fakeClient.EnqueueResponse(new AgentOutput { Answer = "good", Source = "v2" });

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);
        var attempt = 0;

        var result = await agent.PredictAsync(
            new AgentInput("q"),
            validate: output =>
            {
                attempt++;
                if (attempt == 1)
                    LmpAssert.That(output, o => o.Answer != "bad", "Answer must not be 'bad'");
            });

        Assert.Equal("good", result.Answer);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task PredictAsync_AllRetriesFail_ThrowsMaxRetriesExceeded()
    {
        var fakeClient = new FakeChatClient();
        for (int i = 0; i <= 3; i++)
            fakeClient.EnqueueResponse(new AgentOutput { Answer = "bad", Source = "fail" });

        var agent = new ReActAgent<AgentInput, AgentOutput>(fakeClient, []);

        await Assert.ThrowsAsync<LmpMaxRetriesExceededException>(
            () => agent.PredictAsync(
                new AgentInput("q"),
                validate: o => LmpAssert.That(o, _ => false, "always fails")));
    }

    // === IPredictor interface ===

    [Fact]
    public void ImplementsIPredictor()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);

        Assert.IsAssignableFrom<IPredictor>(agent);
    }

    [Fact]
    public void InstructionsProperty_IsSettable()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);

        agent.Instructions = "Custom instructions";

        Assert.Equal("Custom instructions", agent.Instructions);
    }

    [Fact]
    public void ConfigProperty_IsSettable()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);

        agent.Config = new ChatOptions { Temperature = 0.5f };

        Assert.Equal(0.5f, agent.Config.Temperature);
    }

    [Fact]
    public void DemosProperty_IsFillable()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);

        agent.Demos.Add((new AgentInput("q"), new AgentOutput { Answer = "a", Source = "s" }));

        Assert.Single(agent.Demos);
    }

    [Fact]
    public void AddDemo_WorksThroughIPredictor()
    {
        var client = new FakeChatClient();
        IPredictor predictor = new ReActAgent<AgentInput, AgentOutput>(client, []);

        predictor.AddDemo(
            new AgentInput("q"),
            new AgentOutput { Answer = "a", Source = "s" });

        Assert.Single(predictor.Demos);
    }

    // === Clone ===

    [Fact]
    public void Clone_ReturnsIndependentCopy()
    {
        var client = new FakeChatClient();
        var tools = new AIFunction[] { AIFunctionFactory.Create(() => "result", "tool1") };
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, tools);
        agent.Instructions = "original";
        agent.Demos.Add((new AgentInput("q"), new AgentOutput { Answer = "a", Source = "s" }));

        var clone = (ReActAgent<AgentInput, AgentOutput>)agent.Clone();

        Assert.Equal("original", clone.Instructions);
        Assert.Single(clone.Demos);

        // Mutating clone doesn't affect original
        clone.Instructions = "modified";
        clone.Demos.Clear();

        Assert.Equal("original", agent.Instructions);
        Assert.Single(agent.Demos);
    }

    // === GetState / LoadState ===

    [Fact]
    public void GetState_CapturesInstructionsAndDemos()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);
        agent.Instructions = "Agent instructions";
        agent.Demos.Add((new AgentInput("q1"), new AgentOutput { Answer = "a1", Source = "s1" }));

        var state = agent.GetState();

        Assert.Equal("Agent instructions", state.Instructions);
        Assert.Single(state.Demos);
    }

    [Fact]
    public void LoadState_RestoresInstructionsAndDemos()
    {
        var client = new FakeChatClient();
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, []);
        agent.Instructions = "original";
        agent.Demos.Add((new AgentInput("q1"), new AgentOutput { Answer = "a1", Source = "s1" }));

        var state = agent.GetState();

        var agent2 = new ReActAgent<AgentInput, AgentOutput>(client, []);
        agent2.LoadState(state);

        Assert.Equal("original", agent2.Instructions);
        Assert.Single(agent2.Demos);
    }

    // === Tools exposure ===

    [Fact]
    public void Tools_ExposedAsReadOnlyList()
    {
        var client = new FakeChatClient();
        var tool = AIFunctionFactory.Create(() => "result", "myTool");

        var agent = new ReActAgent<AgentInput, AgentOutput>(client, [tool]);

        Assert.Single(agent.Tools);
        Assert.Equal("myTool", agent.Tools[0].Name);
    }

    [Fact]
    public void Tools_AcceptsIEnumerable()
    {
        var client = new FakeChatClient();

        IEnumerable<AIFunction> tools = new[]
        {
            AIFunctionFactory.Create(() => "r1", "t1"),
            AIFunctionFactory.Create(() => "r2", "t2")
        };

        var agent = new ReActAgent<AgentInput, AgentOutput>(client, tools);

        Assert.Equal(2, agent.Tools.Count);
    }

    // === Integration: tool calling ===

    [Fact]
    public async Task PredictAsync_WithToolCall_ExecutesToolAndReturnsResult()
    {
        // Create a client that simulates tool calling:
        // 1. First call: responds with a FunctionCallContent to invoke the tool
        // 2. Second call: responds with the final structured output
        var toolCallClient = new ToolCallFakeChatClient();
        var toolWasCalled = false;

        var tool = AIFunctionFactory.Create(
            (string query) =>
            {
                toolWasCalled = true;
                return "The answer is 42";
            },
            "search",
            "Search for information");

        toolCallClient.EnqueueToolCallResponse("search", """{"query": "meaning of life"}""");
        toolCallClient.EnqueueJsonResponse("""{"Answer": "42", "Source": "search tool"}""");

        var agent = new ReActAgent<AgentInput, AgentOutput>(toolCallClient, [tool]);
        agent.Instructions = "Use tools to answer questions";

        var result = await agent.PredictAsync(new AgentInput("What is 42?"));

        Assert.True(toolWasCalled, "Tool should have been called");
        Assert.Equal("42", result.Answer);
        Assert.Equal("search tool", result.Source);
    }

    [Fact]
    public async Task PredictAsync_WithMultipleToolCalls_ExecutesAllTools()
    {
        var toolCallClient = new ToolCallFakeChatClient();
        var tool1Called = false;
        var tool2Called = false;

        var tool1 = AIFunctionFactory.Create(
            (string query) =>
            {
                tool1Called = true;
                return "Article about physics";
            },
            "search",
            "Search knowledge base");

        var tool2 = AIFunctionFactory.Create(
            (string accountId) =>
            {
                tool2Called = true;
                return "Account: Pro plan";
            },
            "getAccount",
            "Get account info");

        // First call: invoke search tool
        toolCallClient.EnqueueToolCallResponse("search", """{"query": "physics"}""");
        // Second call: invoke getAccount tool
        toolCallClient.EnqueueToolCallResponse("getAccount", """{"accountId": "123"}""");
        // Third call: final answer
        toolCallClient.EnqueueJsonResponse("""{"Answer": "Physics info for Pro account", "Source": "combined"}""");

        var agent = new ReActAgent<AgentInput, AgentOutput>(toolCallClient, [tool1, tool2]);
        var result = await agent.PredictAsync(new AgentInput("Tell me about physics for my account"));

        Assert.True(tool1Called, "Tool 1 should have been called");
        Assert.True(tool2Called, "Tool 2 should have been called");
        Assert.Equal("Physics info for Pro account", result.Answer);
    }

    [Fact]
    public async Task PredictAsync_WithToolCall_RecordsTraceOfFinalResult()
    {
        var toolCallClient = new ToolCallFakeChatClient();

        var tool = AIFunctionFactory.Create(
            (string query) => "tool result",
            "search",
            "Search");

        toolCallClient.EnqueueToolCallResponse("search", """{"query": "test"}""");
        toolCallClient.EnqueueJsonResponse("""{"Answer": "found it", "Source": "search"}""");

        var agent = new ReActAgent<AgentInput, AgentOutput>(toolCallClient, [tool]);
        var trace = new Trace();
        var input = new AgentInput("find something");

        await agent.PredictAsync(input, trace);

        Assert.Single(trace.Entries);
        Assert.Equal(input, trace.Entries[0].Input);
        var output = Assert.IsType<AgentOutput>(trace.Entries[0].Output);
        Assert.Equal("found it", output.Answer);
    }

    // === Discoverable by GetPredictors ===

    [Fact]
    public void Agent_IsPredictor_DiscoverableByModules()
    {
        var client = new FakeChatClient();
        var tool = AIFunctionFactory.Create(() => "r", "t");
        var agent = new ReActAgent<AgentInput, AgentOutput>(client, [tool]);

        // Agent is IPredictor — so it would appear in GetPredictors() if stored as a field
        IPredictor predictor = agent;

        Assert.NotNull(predictor.Name);
        Assert.NotNull(predictor.Instructions);
        Assert.NotNull(predictor.Config);
        Assert.NotNull(predictor.Demos);
    }
}

/// <summary>
/// A fake chat client that supports simulating tool call responses.
/// When a tool call response is queued, it returns a <see cref="FunctionCallContent"/>
/// so that <see cref="FunctionInvokingChatClient"/> will invoke the tool.
/// </summary>
internal sealed class ToolCallFakeChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _responseFactories = new();

    /// <summary>
    /// Enqueues a tool call response — the client will return a message requesting
    /// the specified function be called with the given arguments.
    /// </summary>
    public void EnqueueToolCallResponse(string functionName, string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
            ?? new Dictionary<string, object?>();

        _responseFactories.Enqueue(() =>
        {
            var callContent = new FunctionCallContent(
                callId: Guid.NewGuid().ToString("N"),
                name: functionName,
                arguments: args);

            var msg = new ChatMessage(ChatRole.Assistant, [callContent]);
            return new ChatResponse(msg);
        });
    }

    /// <summary>
    /// Enqueues a plain JSON text response (the final answer).
    /// </summary>
    public void EnqueueJsonResponse(string json)
    {
        _responseFactories.Enqueue(() =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_responseFactories.Count == 0)
            throw new InvalidOperationException(
                "ToolCallFakeChatClient: no more canned responses available.");

        return Task.FromResult(_responseFactories.Dequeue()());
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
