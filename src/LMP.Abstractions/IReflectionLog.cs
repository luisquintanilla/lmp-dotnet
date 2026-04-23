namespace LMP;

/// <summary>
/// Append-only log of reflective observations produced by optimizer steps such as
/// <c>GEPA</c> and <c>EvaluationCritique</c>. Downstream steps read this log to
/// enrich instruction prompts without additional LM calls.
/// </summary>
/// <remarks>
/// Implementations are expected to be thread-safe: <see cref="Add"/>, <see cref="Entries"/>,
/// <see cref="Count"/>, and the query helpers may be called concurrently.
/// </remarks>
public interface IReflectionLog
{
    /// <summary>All entries in insertion order (snapshot).</summary>
    IReadOnlyList<ReflectionEntry> Entries { get; }

    /// <summary>Number of entries in the log.</summary>
    int Count { get; }

    /// <summary>Appends a new reflection entry to the log.</summary>
    /// <param name="text">Observation text.</param>
    /// <param name="source">Optimizer step that produced this entry (optional).</param>
    /// <param name="predictorPath">
    /// Fully-qualified path of the predictor, if scope is
    /// <see cref="ReflectionScope.Predictor"/>. For composite targets this
    /// matches the prefixed <see cref="TraceEntry.PredictorName"/>.
    /// </param>
    /// <param name="scope">Global (whole module) or Predictor-specific.</param>
    /// <param name="score">Score of the associated example (optional).</param>
    void Add(
        string text,
        string? source = null,
        string? predictorPath = null,
        ReflectionScope scope = ReflectionScope.Global,
        float? score = null);

    /// <summary>Returns all entries matching the given scope.</summary>
    IReadOnlyList<ReflectionEntry> GetEntries(ReflectionScope scope);

    /// <summary>Returns all entries for a specific predictor path.</summary>
    IReadOnlyList<ReflectionEntry> GetEntriesForPredictor(string predictorPath);
}
