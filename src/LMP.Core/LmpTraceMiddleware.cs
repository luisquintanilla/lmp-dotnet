using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that records token usage into a <see cref="Trace"/>
/// after each <see cref="GetResponseAsync"/> call.
/// </summary>
/// <remarks>
/// Used internally by <see cref="ChatClientTarget"/> to capture real LM token counts.
/// Obtainable by callers via
/// <see cref="ChatClientBuilderExtensions.UseLmpTrace(ChatClientBuilder, out Trace)"/>
/// for composing in an M.E.AI middleware chain.
/// </remarks>
internal sealed class LmpTraceMiddleware : DelegatingChatClient
{
    private readonly Trace _trace;

    internal LmpTraceMiddleware(IChatClient innerClient, Trace trace)
        : base(innerClient)
    {
        _trace = trace;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var response = await base.GetResponseAsync(msgList, options, cancellationToken)
            .ConfigureAwait(false);

        if (response.Usage is { } usage)
        {
            var inputText = msgList.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                            ?? string.Empty;
            _trace.Record("api_call", inputText, response.Text ?? string.Empty, usage);
        }

        return response;
    }
}
