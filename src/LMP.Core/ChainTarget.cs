namespace LMP;

/// <summary>
/// An <see cref="IOptimizationTarget"/> that pipes the output of each target
/// into the input of the next, forming a sequential processing chain.
/// </summary>
/// <remarks>
/// <para>
/// Parameters from each child target are exposed in the merged
/// <see cref="GetParameterSpace"/> prefixed by <c>"child_{i}."</c>
/// (e.g., <c>"child_0.system_prompt"</c>, <c>"child_1.temperature"</c>).
/// <see cref="WithParameters"/> routes values to the correct child by stripping
/// the prefix before forwarding.
/// </para>
/// <para>
/// Construction is internal — use <see cref="OptimizationTargetExtensions.Then"/>
/// for two-element chains or <see cref="Pipeline{TIn, TOut}"/> for collection-init
/// composition of three or more stages.
/// </para>
/// </remarks>
public sealed class ChainTarget : IOptimizationTarget
{
    private readonly IReadOnlyList<IOptimizationTarget> _targets;

    internal ChainTarget(IReadOnlyList<IOptimizationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("At least one target is required.", nameof(targets));
        for (int i = 0; i < targets.Count; i++)
            if (targets[i] is null)
                throw new ArgumentException($"targets[{i}] is null.", nameof(targets));
        _targets = targets;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see cref="TargetShape.MultiTurn"/> if any child target is multi-turn;
    /// otherwise <see cref="TargetShape.SingleTurn"/>.
    /// </remarks>
    public TargetShape Shape => _targets.Any(t => t.Shape == TargetShape.MultiTurn)
        ? TargetShape.MultiTurn
        : TargetShape.SingleTurn;

    /// <inheritdoc />
    /// <remarks>
    /// Executes each target in sequence, passing the output of each as input to
    /// the next. The combined <see cref="Trace"/> merges all child trace entries
    /// in execution order. To preserve the symmetric prefix contract with
    /// <see cref="GetParameterSpace"/>, each merged entry's
    /// <see cref="TraceEntry.PredictorName"/> is rewritten to
    /// <c>"child_{i}.{originalName}"</c>, where <c>i</c> is the zero-based index
    /// of the producing child. Consumers that match trace entries against
    /// parameter slot keys (e.g., BootstrapFewShot matching
    /// <c>"{PredictorName}.demos"</c>) therefore see the same fully-qualified
    /// slot path on both sides.
    /// </remarks>
    public async Task<(object Output, Trace Trace)> ExecuteAsync(
        object input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        object current = input;
        var combined = new Trace();

        for (int i = 0; i < _targets.Count; i++)
        {
            var (output, trace) = await _targets[i].ExecuteAsync(current, ct).ConfigureAwait(false);
            current = output;
            foreach (var entry in trace.Entries)
                combined.Record($"child_{i}.{entry.PredictorName}", entry.Input, entry.Output, entry.Usage);
        }

        return (current, combined);
    }

    /// <summary>
    /// Returns a merged parameter space where each child's parameters are prefixed
    /// by <c>"child_{i}."</c> (zero-based index). This prefix mirrors the one applied
    /// by <see cref="ExecuteAsync"/> to child trace entries'
    /// <see cref="TraceEntry.PredictorName"/>, so slot keys and trace entry names
    /// share the same fully-qualified path.
    /// </summary>
    public TypedParameterSpace GetParameterSpace()
    {
        var space = TypedParameterSpace.Empty;
        for (int i = 0; i < _targets.Count; i++)
        {
            var childSpace = _targets[i].GetParameterSpace();
            foreach (var (name, kind) in childSpace.Parameters)
                space = space.Add($"child_{i}.{name}", kind);
        }
        return space;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns a <see cref="TargetState"/> whose <see cref="TargetState.Value"/> is a
    /// <c>TargetState[]</c> — one entry per child in order.
    /// </remarks>
    public TargetState GetState()
    {
        var states = _targets.Select(t => t.GetState()).ToArray();
        return TargetState.From(states);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the state array length does not match the number of child targets.
    /// </exception>
    public void ApplyState(TargetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var states = state.As<TargetState[]>();
        if (states.Length != _targets.Count)
            throw new ArgumentException(
                $"State array has {states.Length} entries; expected {_targets.Count}.",
                nameof(state));
        for (int i = 0; i < _targets.Count; i++)
            _targets[i].ApplyState(states[i]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Values whose keys start with <c>"child_{i}."</c> are stripped of their prefix
    /// and forwarded to child <c>i</c>. Keys that do not match any child prefix are ignored.
    /// </remarks>
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        var newTargets = new IOptimizationTarget[_targets.Count];
        for (int i = 0; i < _targets.Count; i++)
        {
            var prefix = $"child_{i}.";
            var childAssignment = ParameterAssignment.Empty;
            foreach (var (key, value) in assignment.Values)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    childAssignment = childAssignment.With(key[prefix.Length..], value);
            }
            newTargets[i] = _targets[i].WithParameters(childAssignment);
        }

        return new ChainTarget(newTargets);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the first non-null result from child targets in order, or null if none match.
    /// </remarks>
    public TService? GetService<TService>() where TService : class
        => _targets.Select(t => t.GetService<TService>())
                   .FirstOrDefault(s => s is not null);

    /// <summary>The chained targets in execution order.</summary>
    public IReadOnlyList<IOptimizationTarget> Targets => _targets;
}
