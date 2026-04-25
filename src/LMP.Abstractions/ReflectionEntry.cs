namespace LMP;

/// <summary>
/// A single reflective observation logged by an optimizer step (e.g., <c>EvaluationCritique</c>,
/// <c>GEPA</c>). Downstream optimizer steps read these entries to improve instructions
/// without additional LLM calls.
/// </summary>
/// <param name="Text">The observation text.</param>
/// <param name="Source">Identifier of the optimizer step that produced this entry (optional).</param>
/// <param name="PredictorName">
/// Name of the predictor this entry relates to. Non-null when <see cref="Scope"/> is
/// <see cref="ReflectionScope.Predictor"/>.
/// </param>
/// <param name="Scope">Whether the observation is module-wide or predictor-specific.</param>
/// <param name="Score">Score associated with the example this entry was derived from (optional).</param>
/// <param name="CreatedAt">When this entry was produced.</param>
public sealed record ReflectionEntry(
    string Text,
    string? Source = null,
    string? PredictorName = null,
    ReflectionScope Scope = ReflectionScope.Global,
    float? Score = null,
    DateTimeOffset CreatedAt = default);
