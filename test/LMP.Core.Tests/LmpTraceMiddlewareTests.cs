using LMP;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests for the LmpTraceMiddleware behavior, validated through ChatClientTarget.ExecuteAsync
/// since LmpTraceMiddleware is an internal implementation detail.
/// </summary>
public sealed class LmpTraceMiddlewareTests
{
    // ── ChatClientTarget captures usage from response ─────────────────────

    [Fact]
    public async Task ExecuteAsync_ResponseWithUsage_TraceHasOneEntry()
    {
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 };
        var client = new UsageReturningClient("answer", usage);
        var target = ChatClientTarget.For(client);

        var (output, trace) = await target.ExecuteAsync("hello");

        Assert.Equal("answer", output);
        Assert.Single(trace.Entries);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithUsage_EntryHasCorrectTokenCounts()
    {
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 };
        var client = new UsageReturningClient("answer", usage);
        var target = ChatClientTarget.For(client);

        var (_, trace) = await target.ExecuteAsync("hello");

        var entry = trace.Entries[0];
        Assert.Equal(10, entry.Usage!.InputTokenCount);
        Assert.Equal(20, entry.Usage.OutputTokenCount);
        Assert.Equal(30, trace.TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithUsage_EntryPredictorNameIsApiCall()
    {
        var usage = new UsageDetails { TotalTokenCount = 5 };
        var client = new UsageReturningClient("hi", usage);
        var target = ChatClientTarget.For(client);

        var (_, trace) = await target.ExecuteAsync("question");

        Assert.Equal("api_call", trace.Entries[0].PredictorName);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithUsage_EntryOutputMatchesResponseText()
    {
        var usage = new UsageDetails { TotalTokenCount = 1 };
        var client = new UsageReturningClient("the answer", usage);
        var target = ChatClientTarget.For(client);

        var (_, trace) = await target.ExecuteAsync("query");

        Assert.Equal("the answer", (string)trace.Entries[0].Output);
    }

    // ── No entry when response has no usage ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoUsage_TraceIsEmpty()
    {
        var client = new NoUsageClient("hello");
        var target = ChatClientTarget.For(client);

        var (_, trace) = await target.ExecuteAsync("input");

        Assert.Empty(trace.Entries);
        Assert.Equal(0, trace.TotalTokens);
    }

    // ── TotalTokens reflects real usage ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithUsage_TotalTokensMatchesUsage()
    {
        var usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 13, TotalTokenCount = 20 };
        var client = new UsageReturningClient("ok", usage);
        var target = ChatClientTarget.For(client);

        var (_, trace) = await target.ExecuteAsync("test input");

        Assert.Equal(20, trace.TotalTokens);
    }

    // ── Output and text ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ResponseText_ReturnedAsOutput()
    {
        var usage = new UsageDetails { TotalTokenCount = 5 };
        var client = new UsageReturningClient("my response", usage);
        var target = ChatClientTarget.For(client);

        var (output, _) = await target.ExecuteAsync("prompt");

        Assert.Equal("my response", output);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────

file sealed class UsageReturningClient : IChatClient
{
    private readonly string _text;
    private readonly UsageDetails _usage;

    public UsageReturningClient(string text, UsageDetails usage)
    {
        _text = text;
        _usage = usage;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _text))
        {
            Usage = _usage
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

file sealed class NoUsageClient : IChatClient
{
    private readonly string _text;
    public NoUsageClient(string text) => _text = text;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
