using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Extension methods for deploying an optimized <see cref="ChatClientTarget"/> state
/// as an <see cref="IChatClient"/> middleware via the M.E.AI <see cref="ChatClientBuilder"/>.
/// </summary>
public static class ChatClientOptimizationExtensions
{
    /// <summary>
    /// Adds an <see cref="OptimizedChatClient"/> middleware that applies the given
    /// <paramref name="state"/> to every request — injecting the system prompt,
    /// temperature override, and tool selection captured during optimization.
    /// </summary>
    /// <param name="builder">The chat client builder to augment.</param>
    /// <param name="state">The optimized state to apply. Must not be null.</param>
    /// <returns>The updated builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="state"/> is null.
    /// </exception>
    public static ChatClientBuilder UseOptimized(
        this ChatClientBuilder builder,
        ChatClientState state)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(state);
        return builder.Use(inner => new OptimizedChatClient(inner, state));
    }

    /// <summary>
    /// Adds an <see cref="OptimizedChatClient"/> middleware that applies the
    /// optimized state from a <see cref="ChatClientTarget"/> optimization result.
    /// </summary>
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

        return UseOptimized(builder, state);
    }
}
