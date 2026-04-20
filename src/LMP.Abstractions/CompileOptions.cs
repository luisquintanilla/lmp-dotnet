namespace LMP;

/// <summary>
/// Options for <see cref="IOptimizer.CompileAsync{TModule}"/>.
/// Controls artifact generation (the <c>.g.cs</c> file) produced during compilation.
/// </summary>
public sealed record CompileOptions
{
    /// <summary>
    /// Output directory for the generated <c>.g.cs</c> artifact.
    /// Default: <c>"Generated"</c> (relative to current directory).
    /// Set to <c>null</c> to suppress artifact generation.
    /// </summary>
    public string? OutputDir { get; init; } = "Generated";

    /// <summary>
    /// Optional path to the training data file, used for staleness detection.
    /// When set, a SHA-256 hash is embedded in the generated file header.
    /// </summary>
    public string? TrainDataPath { get; init; }

    /// <summary>
    /// Optional baseline score (pre-optimization) for comparison.
    /// When set, the generated <c>.g.cs</c> header includes the baseline for context.
    /// </summary>
    public float? Baseline { get; init; }

    /// <summary>
    /// Optimization only — no <c>.g.cs</c> file is written.
    /// Use in tests or runtime-only scenarios.
    /// </summary>
    public static CompileOptions RuntimeOnly { get; } = new() { OutputDir = null };

    /// <summary>
    /// Class name for the generated <see cref="ChatClientTarget"/> artifact
    /// (e.g., <c>"OptimizedSupportClient"</c>).
    /// Required when calling <see cref="OptimizationResult.WriteArtifactAsync"/> on a
    /// <see cref="ChatClientTarget"/> result.
    /// </summary>
    public string? ArtifactClassName { get; init; }

    /// <summary>
    /// Namespace for the generated <see cref="ChatClientTarget"/> artifact
    /// (e.g., <c>"MyApp.Generated"</c>).
    /// Defaults to the global namespace when <see langword="null"/>.
    /// </summary>
    public string? ArtifactNamespace { get; init; }
}
