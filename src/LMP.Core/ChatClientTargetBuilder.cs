using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Fluent builder used by
/// <see cref="ChatClientOptimizationExtensions.AsOptimizationTarget(IChatClient, Action{ChatClientTargetBuilder}?)"/>
/// to configure the optimizable parameters of a <see cref="ChatClientTarget"/>.
/// </summary>
public sealed class ChatClientTargetBuilder
{
    internal string? SystemPrompt { get; private set; }
    internal float? Temperature { get; private set; }
    internal IReadOnlyList<AITool>? Tools { get; private set; }

    internal ChatClientTargetBuilder() { }

    /// <summary>
    /// Sets the initial system prompt. Registered as a <see cref="StringValued"/>
    /// optimization parameter (<c>"system_prompt"</c>).
    /// </summary>
    public ChatClientTargetBuilder WithSystemPrompt(string systemPrompt)
    {
        SystemPrompt = systemPrompt;
        return this;
    }

    /// <summary>
    /// Sets the initial sampling temperature (0–2). Registered as a
    /// <see cref="Continuous"/> optimization parameter (<c>"temperature"</c>).
    /// </summary>
    public ChatClientTargetBuilder WithTemperature(float temperature)
    {
        Temperature = temperature;
        return this;
    }

    /// <summary>
    /// Sets the pool of tools available to the chat client. All tools are initially
    /// selected; the optimizer narrows the subset. Tool names must be unique.
    /// </summary>
    public ChatClientTargetBuilder WithTools(IReadOnlyList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = tools;
        return this;
    }
}
