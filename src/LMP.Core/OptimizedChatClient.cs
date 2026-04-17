using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// An <see cref="IChatClient"/> middleware that applies an optimized <see cref="ChatClientState"/>
/// to every request — injecting a system prompt, temperature override, or tool selection.
/// </summary>
/// <remarks>
/// Obtain via <see cref="ChatClientOptimizationExtensions.UseOptimized(ChatClientBuilder, ChatClientState)"/>.
/// </remarks>
internal sealed class OptimizedChatClient : DelegatingChatClient
{
    private readonly ChatClientState _state;

    internal OptimizedChatClient(IChatClient innerClient, ChatClientState state)
        : base(innerClient)
    {
        _state = state;
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var augmentedMessages = PrependSystemPrompt(messages);
        var augmentedOptions = OverrideTemperature(options);
        return base.GetResponseAsync(augmentedMessages, augmentedOptions, cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private IEnumerable<ChatMessage> PrependSystemPrompt(IEnumerable<ChatMessage> messages)
    {
        if (_state.SystemPrompt is null)
            return messages;

        return messages.Prepend(new ChatMessage(ChatRole.System, _state.SystemPrompt));
    }

    private ChatOptions? OverrideTemperature(ChatOptions? existing)
    {
        if (_state.Temperature is null)
            return existing;

        var opts = existing?.Clone() ?? new ChatOptions();
        opts.Temperature = _state.Temperature.Value;
        return opts;
    }
}
