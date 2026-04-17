namespace LMP;

/// <summary>
/// Indicates the execution shape of an optimization target.
/// </summary>
public enum TargetShape
{
    /// <summary>Single-turn: one input → one output per execution.</summary>
    SingleTurn,

    /// <summary>Multi-turn: multi-step conversation or agent loop.</summary>
    MultiTurn
}
