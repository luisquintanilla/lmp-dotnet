namespace LMP;

/// <summary>
/// Discriminates the role of a <see cref="Turn"/> within a <see cref="Trajectory"/>.
/// </summary>
public enum TurnKind
{
    /// <summary>A regular conversational exchange (user message → model response).</summary>
    Message,

    /// <summary>A tool or function invocation request (model request → tool dispatch).</summary>
    ToolCall,

    /// <summary>An observation or tool result returned from the environment.</summary>
    Observation
}
