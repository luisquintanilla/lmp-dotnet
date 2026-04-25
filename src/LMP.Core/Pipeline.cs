using System.Collections;

namespace LMP;

/// <summary>
/// Collection-initializable composition of <see cref="IOptimizationTarget"/> stages,
/// equivalent to chaining them with <see cref="OptimizationTargetExtensions.Then"/>
/// but with explicit input/output marker types for documentation.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// var pipeline = new Pipeline&lt;string, Draft&gt; { stage1, stage2, stage3 };
/// var (output, _) = await pipeline.ExecuteAsync(input, ct);
/// </code>
/// </para>
/// <para>
/// <typeparamref name="TIn"/> and <typeparamref name="TOut"/> are markers for documentation
/// and inference; runtime enforcement of the input/output types lands in T3 with the
/// generic <c>IOptimizationTarget&lt;TIn, TOut&gt;</c> family.
/// </para>
/// </remarks>
/// <typeparam name="TIn">Marker type for the input consumed by the first stage.</typeparam>
/// <typeparam name="TOut">Marker type for the output produced by the last stage.</typeparam>
public sealed class Pipeline<TIn, TOut> : IOptimizationTarget, IEnumerable<IOptimizationTarget>
{
    private readonly List<IOptimizationTarget> _stages = [];
    private ChainTarget? _chain;

    /// <summary>Creates an empty pipeline. Use <see cref="Add"/> or collection initializer to populate.</summary>
    public Pipeline() { }

    /// <summary>Adds a stage to the pipeline. Required for collection initializer support.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is null.</exception>
    public void Add(IOptimizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _stages.Add(target);
        _chain = null;
    }

    /// <summary>The composed stages in execution order.</summary>
    public IReadOnlyList<IOptimizationTarget> Stages => _stages;

    /// <inheritdoc />
    public IEnumerator<IOptimizationTarget> GetEnumerator() => _stages.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ChainTarget Chain => _chain ??= new ChainTarget([.. _stages]);

    /// <inheritdoc />
    public TargetShape Shape => Chain.Shape;

    /// <inheritdoc />
    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Chain.ExecuteAsync(input, ct);

    /// <inheritdoc />
    public TypedParameterSpace GetParameterSpace() => Chain.GetParameterSpace();

    /// <inheritdoc />
    public TargetState GetState() => Chain.GetState();

    /// <inheritdoc />
    public void ApplyState(TargetState state) => Chain.ApplyState(state);

    /// <inheritdoc />
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        => Chain.WithParameters(assignment);

    /// <inheritdoc />
    public TService? GetService<TService>() where TService : class => Chain.GetService<TService>();

    /// <inheritdoc />
    public Task<Trajectory> ExecuteTrajectoryAsync(
        object input, Example? source = null, CancellationToken ct = default)
        => ((IOptimizationTarget)Chain).ExecuteTrajectoryAsync(input, source, ct);

    /// <inheritdoc />
    public Task WriteArtifactAsync(CompileOptions options, CancellationToken ct = default)
        => ((IOptimizationTarget)Chain).WriteArtifactAsync(options, ct);
}
