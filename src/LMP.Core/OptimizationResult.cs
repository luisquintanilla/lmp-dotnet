namespace LMP;

/// <summary>
/// The result of a completed optimization run.
/// Contains the optimized target, score metrics, and trial history.
/// Call <see cref="WriteArtifactAsync"/> to persist the optimized state as a <c>.g.cs</c> file.
/// </summary>
public sealed record OptimizationResult
{
    /// <summary>The optimized target (module state has been updated in-place).</summary>
    public required IOptimizationTarget Target { get; init; }

    /// <summary>Score before any optimization (on the evaluation set).</summary>
    public required float BaselineScore { get; init; }

    /// <summary>Score after optimization (on the same evaluation set as baseline).</summary>
    public required float OptimizedScore { get; init; }

    /// <summary>All trials recorded during the optimization run.</summary>
    public required IReadOnlyList<Trial> Trials { get; init; }

    /// <summary>
    /// Writes a <c>.g.cs</c> artifact for the optimized module.
    /// Does nothing when <paramref name="options"/> is <c>null</c> or has no output directory.
    /// </summary>
    /// <param name="options">Compilation options controlling artifact output path and metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the generated file, or <c>null</c> if no file was written.</returns>
    public async Task<string?> WriteArtifactAsync(
        CompileOptions? options,
        CancellationToken ct = default)
    {
        if (options?.OutputDir is null)
            return null;

        var module = Target.GetService<LmpModule>()
            ?? throw new InvalidOperationException(
                "WriteArtifactAsync requires an LmpModule target (ModuleTarget). " +
                "Non-module targets must implement WriteArtifactAsync directly.");

        return await CSharpArtifactWriter.WriteAsync(
            module,
            options.OutputDir,
            OptimizedScore,
            "OptimizationPipeline",
            options.TrainDataPath,
            BaselineScore,
            ct);
    }
}
