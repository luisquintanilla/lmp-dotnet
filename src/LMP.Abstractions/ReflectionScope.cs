namespace LMP;

/// <summary>
/// Scope of a <see cref="ReflectionEntry"/>, indicating how broadly it should be applied.
/// </summary>
public enum ReflectionScope
{
    /// <summary>
    /// Applies to the whole module run.
    /// Suitable for cross-cutting observations from evaluators or human feedback.
    /// </summary>
    Global,

    /// <summary>
    /// Applies to a specific predictor by name.
    /// GEPA uses Predictor-scoped entries to improve that predictor's instructions.
    /// </summary>
    Predictor
}
