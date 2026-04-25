namespace LMP;

/// <summary>
/// Vertical extensibility seam: any LM-backed component that can be optimized.
/// Implementations include <see cref="LmpModule"/> (its own optimization target),
/// <c>ChatClientTarget</c> (wraps <c>IChatClient</c>), <c>ChainTarget</c> (sequential
/// composition), and user-defined adapters.
/// </summary>
public interface IOptimizationTarget
{
    /// <summary>Execution shape of this target.</summary>
    TargetShape Shape { get; }

    /// <summary>
    /// Executes the target for a single input.
    /// Returns the output and the execution trace together — trace is not a side-channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned trace describes only this invocation and MUST NOT carry state from prior calls.
    /// Implementations should create a fresh <see cref="Trace"/> per call (see
    /// <see cref="LmpModule.ExecuteAsync"/> reference implementation).
    /// </para>
    /// <para>
    /// Composite implementations (e.g., <c>ChainTarget</c>, <c>Pipeline</c>) MUST prefix each
    /// child trace entry's <see cref="TraceEntry.PredictorName"/> with the same prefix that
    /// <see cref="GetParameterSpace"/> applies to the corresponding child's parameters
    /// (for <c>ChainTarget</c>, <c>"child_{i}."</c>). This keeps the parameter slot space and
    /// trace entry names symmetric: consumers comparing a <see cref="TraceEntry.PredictorName"/>
    /// to a local predictor key MUST compare against the fully-qualified slot path
    /// (e.g., <c>"child_0.classify"</c>), not the leaf name alone.
    /// </para>
    /// </remarks>
    Task<(object Output, Trace Trace)> ExecuteAsync(
        object input,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the optimization parameter space for this target.
    /// </summary>
    TypedParameterSpace GetParameterSpace();

    /// <summary>Returns the current learnable state of this target.</summary>
    TargetState GetState();

    /// <summary>Applies a previously captured <see cref="TargetState"/> to this target.</summary>
    void ApplyState(TargetState state);

    /// <summary>
    /// Returns a new target that is a clone of this one with the given parameter assignment applied.
    /// Enables parallel trial evaluation without shared mutable state.
    /// </summary>
    /// <remarks>
    /// When <paramref name="assignment"/> is <see cref="ParameterAssignment.Empty"/>, the returned
    /// target is a state-independent clone of this target. Optimizers use this idiom to create
    /// teacher/student copies without shared mutable state.
    /// </remarks>
    IOptimizationTarget WithParameters(ParameterAssignment assignment);

    /// <summary>
    /// Returns an optional service from this target (e.g., the underlying <see cref="LmpModule"/>
    /// or <c>IChatClient</c>). Returns <c>null</c> when the service is not available.
    /// </summary>
    TService? GetService<TService>() where TService : class;

    /// <summary>
    /// Executes the target for a single input and returns the execution as a
    /// <see cref="Trajectory"/>. Multi-turn targets should override this to provide
    /// per-step detail. The default implementation wraps <see cref="ExecuteAsync"/> via
    /// <see cref="Trajectory.FromTrace(Trace, Example?)"/>, producing a single-turn trajectory.
    /// </summary>
    /// <param name="input">The input to execute against the target.</param>
    /// <param name="source">
    /// Optional dataset example that triggered this execution; stored on the returned
    /// <see cref="Trajectory"/> as <see cref="Trajectory.Source"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    async Task<Trajectory> ExecuteTrajectoryAsync(
        object input,
        Example? source = null,
        CancellationToken ct = default)
    {
        var (_, trace) = await ExecuteAsync(input, ct);
        return Trajectory.FromTrace(trace, source);
    }

    /// <summary>
    /// Writes an optimization artifact (e.g., <c>.g.cs</c>) for this target.
    /// Default implementation is a no-op. <see cref="LmpModule"/> overrides via
    /// <see cref="OptimizationResult.WriteArtifactAsync"/> dispatch.
    /// </summary>
    Task WriteArtifactAsync(CompileOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
}
