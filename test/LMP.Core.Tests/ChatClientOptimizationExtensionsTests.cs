using LMP;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public class ChatClientOptimizationExtensionsTests
{
    // ── UseOptimized(ChatClientState) ─────────────────────────────────────

    [Fact]
    public void UseOptimized_State_NullBuilderThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => ChatClientOptimizationExtensions.UseOptimized(null!, new ChatClientState()));
    }

    [Fact]
    public void UseOptimized_State_NullStateThrows()
    {
        var builder = new ChatClientBuilder(new SpyChatClient("x"));
        Assert.Throws<ArgumentNullException>(
            () => builder.UseOptimized((ChatClientState)null!));
    }

    [Fact]
    public async Task UseOptimized_State_InjectsSystemPrompt()
    {
        var spy = new SpyChatClient("response");
        var state = new ChatClientState { SystemPrompt = "You are a helper." };

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(2, spy.LastMessages!.Count);
        Assert.Equal(ChatRole.System, spy.LastMessages[0].Role);
        Assert.Equal("You are a helper.", spy.LastMessages[0].Text);
    }

    [Fact]
    public async Task UseOptimized_State_OverridesTemperature()
    {
        var spy = new SpyChatClient("response");
        var state = new ChatClientState { Temperature = 0.4f };

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(0.4f, spy.LastOptions!.Temperature);
    }

    [Fact]
    public async Task UseOptimized_State_NoSystemPrompt_DoesNotPrepend()
    {
        var spy = new SpyChatClient("response");
        var state = new ChatClientState { Temperature = 0.5f };

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Single(spy.LastMessages!);
        Assert.Equal(ChatRole.User, spy.LastMessages![0].Role);
    }

    [Fact]
    public async Task UseOptimized_State_NullFields_PassesThrough()
    {
        var spy = new SpyChatClient("response");
        var state = new ChatClientState();  // all nulls

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Single(spy.LastMessages!);
        Assert.Null(spy.LastOptions?.Temperature);
    }

    // ── UseOptimized(OptimizationResult) ──────────────────────────────────

    [Fact]
    public void UseOptimized_Result_NullBuilderThrows()
    {
        var result = FakeResult(new ChatClientState());
        Assert.Throws<ArgumentNullException>(
            () => ChatClientOptimizationExtensions.UseOptimized(null!, result));
    }

    [Fact]
    public void UseOptimized_Result_NullResultThrows()
    {
        var builder = new ChatClientBuilder(new SpyChatClient("x"));
        Assert.Throws<ArgumentNullException>(
            () => builder.UseOptimized((OptimizationResult)null!));
    }

    [Fact]
    public void UseOptimized_Result_NonChatClientTarget_Throws()
    {
        // ModuleTarget produces ModuleState, not ChatClientState
        var fakeModuleResult = new OptimizationResult
        {
            Target = ModuleTarget.For(new FakeModule()),
            BaselineScore = 0f,
            OptimizedScore = 0f,
            Trials = []
        };
        var builder = new ChatClientBuilder(new SpyChatClient("x"));
        Assert.Throws<NotSupportedException>(() => builder.UseOptimized(fakeModuleResult));
    }

    [Fact]
    public async Task UseOptimized_Result_ChatClientTarget_AppliesState()
    {
        var chatClientTarget = ChatClientTarget.For(
            new SpyChatClient("ok"), systemPrompt: "Use result.");
        var result = new OptimizationResult
        {
            Target = chatClientTarget,
            BaselineScore = 0f,
            OptimizedScore = 0.9f,
            Trials = []
        };

        var spy = new SpyChatClient("final response");
        var client = new ChatClientBuilder(spy)
            .UseOptimized(result)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        Assert.Equal(2, spy.LastMessages!.Count);
        Assert.Equal("Use result.", spy.LastMessages[0].Text);
    }

    // ── Returns builder for chaining ──────────────────────────────────────

    [Fact]
    public void UseOptimized_ReturnsSameBuilderForChaining()
    {
        var spy = new SpyChatClient("x");
        var builder = new ChatClientBuilder(spy);
        var returned = builder.UseOptimized(new ChatClientState());
        Assert.Same(builder, returned);
    }

    // ── UseLmpTrace ───────────────────────────────────────────────────────

    [Fact]
    public void UseLmpTrace_NullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ChatClientOptimizationExtensions.UseLmpTrace(null!, new Trace()));
    }

    [Fact]
    public void UseLmpTrace_NullTrace_Throws()
    {
        var builder = new ChatClientBuilder(new SpyChatClient("x"));
        Assert.Throws<ArgumentNullException>(
            () => builder.UseLmpTrace(null!));
    }

    [Fact]
    public void UseLmpTrace_ReturnsSameBuilder()
    {
        var trace = new Trace();
        var builder = new ChatClientBuilder(new SpyChatClient("x"));
        var returned = builder.UseLmpTrace(trace);
        Assert.Same(builder, returned);
    }

    [Fact]
    public async Task UseLmpTrace_RecordsUsageEntry_WhenResponseHasUsage()
    {
        var trace = new Trace();
        var innerClient = new UsageReportingSpyClient("result",
            new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 });

        var client = new ChatClientBuilder(innerClient)
            .UseLmpTrace(trace)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Single(trace.Entries);
        Assert.NotNull(trace.Entries[0].Usage);
        Assert.Equal(10, trace.Entries[0].Usage!.InputTokenCount);
        Assert.Equal(20, trace.Entries[0].Usage!.OutputTokenCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static OptimizationResult FakeResult(ChatClientState state)
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"));
        target.ApplyState(TargetState.From(state));
        return new OptimizationResult
        {
            Target = target,
            BaselineScore = 0f,
            OptimizedScore = 0f,
            Trials = []
        };
    }
}

// ── Minimal fake module for ModuleTarget tests ─────────────────────────────
file sealed class FakeModule : LmpModule
{
    protected override LmpModule CloneCore() => new FakeModule();
    public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
        => Task.FromResult<object>(string.Empty);
}

/// <summary>A spy that returns a response with configurable UsageDetails.</summary>
file sealed class UsageReportingSpyClient : IChatClient
{
    private readonly string _text;
    private readonly UsageDetails _usage;
    public UsageReportingSpyClient(string text, UsageDetails usage) { _text = text; _usage = usage; }
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _text))
        {
            Usage = _usage
        };
        return Task.FromResult(response);
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
