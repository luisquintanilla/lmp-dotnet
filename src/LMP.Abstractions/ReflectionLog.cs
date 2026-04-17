namespace LMP;

/// <summary>
/// Log of reflective observations produced by optimizers such as GEPA and EvaluationCritique.
/// Optimizers read this log to inform instruction evolution without additional LM calls.
/// Phase A: stub. Full implementation added in Phase B.
/// </summary>
public sealed class ReflectionLog
{
    /// <summary>Empty reflection log.</summary>
    public static ReflectionLog Empty { get; } = new();
}
