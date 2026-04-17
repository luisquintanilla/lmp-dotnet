namespace LMP;

/// <summary>
/// Log of reflective observations produced by optimizer steps such as <c>GEPA</c>
/// and <c>EvaluationCritique</c>. Downstream steps read this log to enrich
/// instruction prompts without additional LM calls.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="Add"/> and <see cref="Entries"/> may be called concurrently.
/// Each <see cref="OptimizationContext"/> starts with a fresh <c>new ReflectionLog()</c>.
/// </remarks>
public sealed class ReflectionLog
{
    /// <summary>
    /// A read-only empty sentinel.
    /// Calling <see cref="Add"/> on this instance throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static ReflectionLog Empty { get; } = new(readOnly: true);

    private readonly bool _readOnly;
    private readonly object _lock = new();
    private readonly List<ReflectionEntry> _entries = [];

    /// <summary>Creates a new, mutable <see cref="ReflectionLog"/>.</summary>
    public ReflectionLog() : this(readOnly: false) { }

    private ReflectionLog(bool readOnly) => _readOnly = readOnly;

    /// <summary>All entries in insertion order (snapshot).</summary>
    public IReadOnlyList<ReflectionEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    /// <summary>Number of entries.</summary>
    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>
    /// Appends a new reflection entry to the log.
    /// </summary>
    /// <param name="text">Observation text.</param>
    /// <param name="source">Optimizer step that produced this entry (optional).</param>
    /// <param name="predictorName">
    /// Name of the predictor, if scope is <see cref="ReflectionScope.Predictor"/>.
    /// </param>
    /// <param name="scope">Global (whole module) or Predictor-specific.</param>
    /// <param name="score">Score of the associated example (optional).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called on <see cref="Empty"/> (read-only sentinel).
    /// </exception>
    public void Add(
        string text,
        string? source = null,
        string? predictorName = null,
        ReflectionScope scope = ReflectionScope.Global,
        float? score = null)
    {
        if (_readOnly)
            throw new InvalidOperationException(
                "ReflectionLog.Empty is read-only. Use 'new ReflectionLog()' for mutable instances.");

        ArgumentNullException.ThrowIfNull(text);

        var entry = new ReflectionEntry(
            text, source, predictorName, scope, score, DateTimeOffset.UtcNow);

        lock (_lock) _entries.Add(entry);
    }

    /// <summary>
    /// Returns all entries matching the given scope.
    /// </summary>
    public IReadOnlyList<ReflectionEntry> GetEntries(ReflectionScope scope)
    {
        lock (_lock)
            return _entries.Where(e => e.Scope == scope).ToArray();
    }

    /// <summary>
    /// Returns all entries for a specific predictor name.
    /// </summary>
    public IReadOnlyList<ReflectionEntry> GetEntriesForPredictor(string predictorName)
    {
        ArgumentNullException.ThrowIfNull(predictorName);
        lock (_lock)
            return _entries
                .Where(e => e.Scope == ReflectionScope.Predictor &&
                            e.PredictorName == predictorName)
                .ToArray();
    }
}
