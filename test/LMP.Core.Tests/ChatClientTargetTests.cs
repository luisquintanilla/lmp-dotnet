using LMP;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public class ChatClientTargetTests
{
    // ── Factory / null guards ─────────────────────────────────────────────

    [Fact]
    public void For_NullClientThrows()
        => Assert.Throws<ArgumentNullException>(() => ChatClientTarget.For(null!));

    [Fact]
    public void For_DuplicateToolNamesThrows()
    {
        var tool1 = AIFunctionFactory.Create(() => "ok", name: "search");
        var tool2 = AIFunctionFactory.Create(() => "ok", name: "search");
        Assert.Throws<ArgumentException>(
            () => ChatClientTarget.For(new SpyChatClient("hi"), tools: [tool1, tool2]));
    }

    [Fact]
    public void For_UniqueToolNames_Succeeds()
    {
        var t1 = AIFunctionFactory.Create(() => "ok", name: "search");
        var t2 = AIFunctionFactory.Create(() => "ok", name: "calc");
        var target = ChatClientTarget.For(new SpyChatClient("hi"), tools: [t1, t2]);
        Assert.NotNull(target);
    }

    // ── Shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_IsSingleTurn()
    {
        var target = ChatClientTarget.For(new SpyChatClient("response"));
        Assert.Equal(TargetShape.SingleTurn, target.Shape);
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StringInput_SendsUserMessage()
    {
        var spy = new SpyChatClient("hello");
        var target = ChatClientTarget.For(spy);
        await target.ExecuteAsync("Say hi");
        Assert.Single(spy.LastMessages!);
        Assert.Equal(ChatRole.User, spy.LastMessages![0].Role);
        Assert.Equal("Say hi", spy.LastMessages[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_WithSystemPrompt_PrependsSysMessage()
    {
        var spy = new SpyChatClient("world");
        var target = ChatClientTarget.For(spy, systemPrompt: "Be concise.");
        await target.ExecuteAsync("Hello");
        Assert.Equal(2, spy.LastMessages!.Count);
        Assert.Equal(ChatRole.System, spy.LastMessages[0].Role);
        Assert.Equal("Be concise.", spy.LastMessages[0].Text);
        Assert.Equal(ChatRole.User, spy.LastMessages[1].Role);
    }

    [Fact]
    public async Task ExecuteAsync_ChatMessageInput_PassedThrough()
    {
        var spy = new SpyChatClient("ok");
        var target = ChatClientTarget.For(spy);
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.User, "question"),
            new(ChatRole.Assistant, "partial"),
        };
        await target.ExecuteAsync(msgs);
        Assert.Equal(2, spy.LastMessages!.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResponseText()
    {
        var spy = new SpyChatClient("the answer is 42");
        var target = ChatClientTarget.For(spy);
        var (output, _) = await target.ExecuteAsync("What is it?");
        Assert.Equal("the answer is 42", output);
    }

    [Fact]
    public async Task ExecuteAsync_WithTemperature_SetsInOptions()
    {
        var spy = new SpyChatClient("ok");
        var target = ChatClientTarget.For(spy, temperature: 0.8f);
        await target.ExecuteAsync("test");
        Assert.NotNull(spy.LastOptions);
        Assert.Equal(0.8f, spy.LastOptions.Temperature);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedInputType_Throws()
    {
        var target = ChatClientTarget.For(new SpyChatClient("ok"));
        await Assert.ThrowsAsync<ArgumentException>(() => target.ExecuteAsync(42));
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var target = ChatClientTarget.For(new SpyChatClient("ok"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => target.ExecuteAsync(null!));
    }

    // ── GetParameterSpace ─────────────────────────────────────────────────

    [Fact]
    public void GetParameterSpace_NoConfig_ReturnsEmpty()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"));
        Assert.True(target.GetParameterSpace().IsEmpty);
    }

    [Fact]
    public void GetParameterSpace_WithSystemPrompt_HasStringValued()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "Be helpful.");
        var space = target.GetParameterSpace();
        Assert.True(space.Parameters.ContainsKey("system_prompt"));
        Assert.IsType<StringValued>(space.Parameters["system_prompt"]);
    }

    [Fact]
    public void GetParameterSpace_WithTemperature_HasContinuous()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), temperature: 1.0f);
        var space = target.GetParameterSpace();
        Assert.True(space.Parameters.ContainsKey("temperature"));
        var cont = Assert.IsType<Continuous>(space.Parameters["temperature"]);
        Assert.Equal(0.0, cont.Min);
        Assert.Equal(2.0, cont.Max);
    }

    [Fact]
    public void GetParameterSpace_WithTools_HasSubset()
    {
        var t1 = AIFunctionFactory.Create(() => "ok", name: "search");
        var target = ChatClientTarget.For(new SpyChatClient("x"), tools: [t1]);
        var space = target.GetParameterSpace();
        Assert.True(space.Parameters.ContainsKey("tools"));
        Assert.IsType<Subset>(space.Parameters["tools"]);
    }

    // ── GetState / ApplyState round-trip ──────────────────────────────────

    [Fact]
    public void GetState_ReturnsCurrentState()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "test", temperature: 0.5f);
        var state = target.GetState().As<ChatClientState>();
        Assert.Equal("test", state.SystemPrompt);
        Assert.Equal(0.5f, state.Temperature);
    }

    [Fact]
    public void ApplyState_UpdatesState()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "old");
        var newState = new ChatClientState { SystemPrompt = "new", Temperature = 1.2f };
        target.ApplyState(TargetState.From(newState));
        var retrieved = target.GetState().As<ChatClientState>();
        Assert.Equal("new", retrieved.SystemPrompt);
        Assert.Equal(1.2f, retrieved.Temperature);
    }

    [Fact]
    public void ApplyState_NullThrows()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"));
        Assert.Throws<ArgumentNullException>(() => target.ApplyState(null!));
    }

    [Fact]
    public void GetState_ApplyState_RoundTrip()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "round-trip", temperature: 0.7f);
        var savedState = target.GetState();
        target.ApplyState(TargetState.From(new ChatClientState { SystemPrompt = "modified" }));
        target.ApplyState(savedState);
        var restored = target.GetState().As<ChatClientState>();
        Assert.Equal("round-trip", restored.SystemPrompt);
        Assert.Equal(0.7f, restored.Temperature);
    }

    // ── WithParameters ────────────────────────────────────────────────────

    [Fact]
    public void WithParameters_Empty_ClonesCurrentState()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "base");
        var cloned = (ChatClientTarget)target.WithParameters(ParameterAssignment.Empty);
        Assert.Equal("base", cloned.GetState().As<ChatClientState>().SystemPrompt);
    }

    [Fact]
    public void WithParameters_SystemPrompt_UpdatesClone()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), systemPrompt: "old");
        var assignment = ParameterAssignment.Empty.With("system_prompt", "new prompt");
        var cloned = (ChatClientTarget)target.WithParameters(assignment);
        Assert.Equal("new prompt", cloned.GetState().As<ChatClientState>().SystemPrompt);
        // original unchanged
        Assert.Equal("old", target.GetState().As<ChatClientState>().SystemPrompt);
    }

    [Fact]
    public void WithParameters_Temperature_ReadsDouble()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"), temperature: 1.0f);
        var assignment = ParameterAssignment.Empty.With("temperature", 0.3);  // double
        var cloned = (ChatClientTarget)target.WithParameters(assignment);
        var temp = cloned.GetState().As<ChatClientState>().Temperature;
        Assert.NotNull(temp);
        Assert.Equal(0.3f, temp.Value, precision: 5);
    }

    [Fact]
    public void WithParameters_NullThrows()
    {
        var target = ChatClientTarget.For(new SpyChatClient("x"));
        Assert.Throws<ArgumentNullException>(() => target.WithParameters(null!));
    }

    // ── Tool subset + ApplyState re-uses full catalog ─────────────────────

    [Fact]
    public async Task ApplyState_ToolSubset_FilteredFromAllTools()
    {
        var t1 = AIFunctionFactory.Create(() => "ok", name: "search");
        var t2 = AIFunctionFactory.Create(() => "ok", name: "calc");
        var spy = new SpyChatClient("ok");
        var target = ChatClientTarget.For(spy, tools: [t1, t2]);

        // Apply state with only "search" selected
        target.ApplyState(TargetState.From(new ChatClientState
        {
            SelectedToolNames = ["search"]
        }));

        await target.ExecuteAsync("test");

        Assert.NotNull(spy.LastOptions?.Tools);
        Assert.Single(spy.LastOptions.Tools!);
        Assert.Equal("search", ((AIFunction)spy.LastOptions.Tools![0]).Name);
    }

    [Fact]
    public async Task ApplyState_ThenDifferentSubset_UsesCatalogNotPreviousSelection()
    {
        var t1 = AIFunctionFactory.Create(() => "ok", name: "a");
        var t2 = AIFunctionFactory.Create(() => "ok", name: "b");
        var t3 = AIFunctionFactory.Create(() => "ok", name: "c");
        var spy = new SpyChatClient("ok");
        var target = ChatClientTarget.For(spy, tools: [t1, t2, t3]);

        // First subset: only "a"
        target.ApplyState(TargetState.From(new ChatClientState { SelectedToolNames = ["a"] }));
        await target.ExecuteAsync("test1");
        Assert.Single(spy.LastOptions!.Tools!);

        // Second subset: "b" and "c" — must still resolve from full catalog, not from {"a"}
        target.ApplyState(TargetState.From(new ChatClientState { SelectedToolNames = ["b", "c"] }));
        await target.ExecuteAsync("test2");
        Assert.Equal(2, spy.LastOptions!.Tools!.Count);
    }

    // ── GetService ────────────────────────────────────────────────────────

    [Fact]
    public void GetService_ReturnsClientAsTService()
    {
        var spy = new SpyChatClient("ok");
        var target = ChatClientTarget.For(spy);
        Assert.Same(spy, target.GetService<IChatClient>());
    }

    [Fact]
    public void GetService_WrongType_ReturnsNull()
    {
        var target = ChatClientTarget.For(new SpyChatClient("ok"));
        Assert.Null(target.GetService<System.IO.Stream>());
    }
}

// ── Test double ──────────────────────────────────────────────────────────

internal sealed class SpyChatClient : IChatClient
{
    private readonly string _responseText;

    public SpyChatClient(string responseText) => _responseText = responseText;

    public List<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessages = messages.ToList();
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
