using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Extension methods for deploying an optimized <see cref="ChatClientTarget"/> state
/// as an <see cref="IChatClient"/> middleware via the M.E.AI <see cref="ChatClientBuilder"/>,
/// and for capturing LM call traces as M.E.AI middleware.
/// </summary>
public static class ChatClientOptimizationExtensions
{
    /// <summary>
    /// Adapts an <see cref="IChatClient"/> as an <see cref="IOptimizationTarget"/> for
    /// use in an optimization pipeline. Configure optimizable parameters (system prompt,
    /// temperature, tool pool) via the supplied <paramref name="configure"/> callback.
    /// </summary>
    /// <example>
    /// <code>
    /// var target = chatClient.AsOptimizationTarget(b =&gt; b
    ///     .WithSystemPrompt("Answer concisely.")
    ///     .WithTemperature(0.7f)
    ///     .WithTools([searchTool, calcTool]));
    /// </code>
    /// </example>
    /// <param name="client">The underlying chat client. Must not be null.</param>
    /// <param name="configure">
    /// Optional configuration callback. When <see langword="null"/>, the resulting target
    /// has no system prompt, no temperature override, and no tool pool.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when two tools share the same name.</exception>
    public static IOptimizationTarget AsOptimizationTarget(
        this IChatClient client,
        Action<ChatClientTargetBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var builder = new ChatClientTargetBuilder();
        configure?.Invoke(builder);
        return ChatClientTarget.Create(client, builder.SystemPrompt, builder.Temperature, builder.Tools);
    }

    /// <summary>
    /// Adds trace-recording middleware that captures per-call token usage and messages
    /// into <paramref name="trace"/> for every <c>GetResponseAsync</c> call on the built client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composes naturally with other M.E.AI middleware (function invocation, logging, retry):
    /// <code>
    /// var client = azureClient.AsChatClient()
    ///     .UseFunctionInvocation()
    ///     .UseLmpTrace(trace)    // captures token usage automatically
    ///     .UseLogging();
    /// </code>
    /// </para>
    /// <para>
    /// <see cref="LmpTraceMiddleware"/> is internal to <c>LMP.Core</c>.
    /// This extension method is in the same assembly, so the access is valid.
    /// </para>
    /// </remarks>
    /// <param name="builder">The chat client builder to augment.</param>
    /// <param name="trace">The trace container to append records to. Must not be null.</param>
    /// <returns>The updated builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="trace"/> is null.
    /// </exception>
    public static ChatClientBuilder UseLmpTrace(
        this ChatClientBuilder builder,
        Trace trace)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(trace);
        return builder.Use(inner => new LmpTraceMiddleware(inner, trace));
    }

    /// <summary>
    /// Adds an <see cref="OptimizedChatClient"/> middleware that applies the given
    /// <paramref name="state"/> to every request — injecting the system prompt,
    /// temperature override, and tool selection captured during optimization.
    /// </summary>
    /// <param name="builder">The chat client builder to augment.</param>
    /// <param name="state">The optimized state to apply. Must not be null.</param>
    /// <param name="toolPool">
    /// Optional pool of <see cref="AITool"/> instances used to resolve
    /// <see cref="ChatClientState.SelectedToolNames"/> at request time.
    /// When <see langword="null"/>, tool names in <paramref name="state"/> are ignored.
    /// </param>
    /// <returns>The updated builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="state"/> is null.
    /// </exception>
    public static ChatClientBuilder UseOptimized(
        this ChatClientBuilder builder,
        ChatClientState state,
        IReadOnlyList<AITool>? toolPool = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(state);
        return builder.Use(inner => new OptimizedChatClient(inner, state, toolPool));
    }

    /// <summary>
    /// Adds an <see cref="OptimizedChatClient"/> middleware that applies the
    /// optimized state from a <see cref="ChatClientTarget"/> optimization result.
    /// </summary>
    /// <remarks>
    /// When the result comes from a <see cref="ChatClientTarget"/> that was created
    /// with a tool pool, the tool pool is automatically threaded through so that
    /// <see cref="ChatClientState.SelectedToolNames"/> is honoured at request time.
    /// </remarks>
    /// <param name="builder">The chat client builder to augment.</param>
    /// <param name="result">
    /// The optimization result. The <see cref="OptimizationResult.Target"/> must be a
    /// <see cref="ChatClientTarget"/>; its state must hold a <see cref="ChatClientState"/>.
    /// </param>
    /// <returns>The updated builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="result"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the result does not come from a <see cref="ChatClientTarget"/> optimization.
    /// </exception>
    public static ChatClientBuilder UseOptimized(
        this ChatClientBuilder builder,
        OptimizationResult result)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Target.GetState().Value is not ChatClientState state)
            throw new NotSupportedException(
                $"UseOptimized requires an OptimizationResult from a ChatClientTarget. " +
                $"The result target holds {result.Target.GetState().StateType.Name}, expected ChatClientState.");

        var toolPool = result.Target is ChatClientTarget cct ? cct.AllTools : null;
        return UseOptimized(builder, state, toolPool);
    }
}
