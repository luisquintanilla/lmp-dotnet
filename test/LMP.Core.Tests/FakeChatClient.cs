using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// A fake <see cref="IChatClient"/> that returns canned JSON responses for testing.
/// Captures sent messages for verification.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();

    /// <summary>
    /// All messages sent to this client across all calls, in order.
    /// </summary>
    public List<IList<ChatMessage>> SentMessages { get; } = [];

    /// <summary>
    /// The number of times <see cref="GetResponseAsync"/> was called.
    /// </summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Enqueues a JSON response to be returned by the next <see cref="GetResponseAsync"/> call.
    /// </summary>
    public void EnqueueResponse<T>(T value) where T : class
    {
        _responses.Enqueue(JsonSerializer.Serialize(value));
    }

    /// <summary>
    /// Enqueues a raw JSON string response.
    /// </summary>
    public void EnqueueJsonResponse(string json)
    {
        _responses.Enqueue(json);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageList = messages.ToList();
        SentMessages.Add(messageList);
        CallCount++;

        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"FakeChatClient: no more canned responses available (call #{CallCount}).");

        var json = _responses.Dequeue();

        var response = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, json));

        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming is not supported by FakeChatClient.");
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
