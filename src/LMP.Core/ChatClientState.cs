namespace LMP;

/// <summary>
/// Serializable state of an optimized <see cref="ChatClientTarget"/>.
/// Captures the system prompt, temperature, and selected tool names that
/// produced the best evaluation score.
/// </summary>
public sealed record ChatClientState
{
    /// <summary>The optimized system prompt. <see langword="null"/> if not configured.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The optimized temperature. <see langword="null"/> if not configured.</summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Names of the tools selected by the optimizer from the full tool catalog.
    /// <see langword="null"/> if the target was created without a tool pool.
    /// An empty list means no tools are selected.
    /// </summary>
    public IReadOnlyList<string>? SelectedToolNames { get; init; }
}
