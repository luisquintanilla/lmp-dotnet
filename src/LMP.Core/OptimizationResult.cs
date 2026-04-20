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
    /// Writes a <c>.g.cs</c> artifact for the optimized target.
    /// Does nothing when <paramref name="options"/> is <c>null</c> or has no output directory.
    /// </summary>
    /// <param name="options">Compilation options controlling artifact output path and metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the generated file, or <c>null</c> if no file was written.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the target is a <see cref="ChatClientTarget"/> and
    /// <see cref="CompileOptions.ArtifactClassName"/> is not set, or when the target type
    /// does not support artifact generation.
    /// </exception>
    public async Task<string?> WriteArtifactAsync(
        CompileOptions? options,
        CancellationToken ct = default)
    {
        if (options?.OutputDir is null)
            return null;

        // LmpModule path
        var module = Target.GetService<LmpModule>();
        if (module is not null)
            return await CSharpArtifactWriter.WriteAsync(
                module,
                options.OutputDir,
                OptimizedScore,
                "OptimizationPipeline",
                options.TrainDataPath,
                BaselineScore,
                ct);

        // ChatClientTarget path
        if (Target is ChatClientTarget)
        {
            if (options.ArtifactClassName is null)
                throw new InvalidOperationException(
                    "Set CompileOptions.ArtifactClassName when calling WriteArtifactAsync on a ChatClientTarget result.");

            var state = Target.GetState().As<ChatClientState>();
            return await CSharpArtifactWriter.WriteForChatClientTargetAsync(
                state,
                options.ArtifactClassName,
                options.ArtifactNamespace ?? "",
                options.OutputDir,
                OptimizedScore,
                "OptimizationPipeline",
                BaselineScore,
                ct);
        }

        throw new InvalidOperationException(
            $"WriteArtifactAsync is not supported for target type '{Target.GetType().Name}'. " +
            "Override WriteArtifactAsync on your custom target to provide artifact generation.");
    }
}
