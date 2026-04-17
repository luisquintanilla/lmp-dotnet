namespace LMP;

/// <summary>
/// Adapts an <see cref="LmpModule"/> as an <see cref="IOptimizationTarget"/>.
/// All optimizer steps that work on <c>LmpModule</c> instances use this adapter.
/// </summary>
public sealed class ModuleTarget : IOptimizationTarget
{
    private readonly LmpModule _module;

    private ModuleTarget(LmpModule module) => _module = module;

    /// <summary>Creates a <see cref="ModuleTarget"/> wrapping the given module.</summary>
    public static ModuleTarget For(LmpModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new ModuleTarget(module);
    }

    /// <inheritdoc />
    public TargetShape Shape => TargetShape.SingleTurn;

    /// <inheritdoc />
    public async Task<(object Output, Trace Trace)> ExecuteAsync(
        object input, CancellationToken ct = default)
    {
        var trace = new Trace();
        _module.Trace = trace;
        try
        {
            var output = await _module.ForwardAsync(input, ct);
            return (output, trace);
        }
        finally
        {
            _module.Trace = null;
        }
    }

    /// <inheritdoc />
    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;

    /// <inheritdoc />
    public TargetState GetState() => TargetState.From(_module.GetState());

    /// <inheritdoc />
    public void ApplyState(TargetState state)
        => _module.ApplyState(state.As<ModuleState>());

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="assignment"/> is non-empty (Phase C adds full parameter support).
    /// </exception>
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        if (!assignment.IsEmpty)
            throw new NotSupportedException(
                "ModuleTarget: TypedParameterSpace parameter application is a Phase C feature. " +
                "Pass ParameterAssignment.Empty to clone without parameter changes.");
        return new ModuleTarget(_module.Clone());
    }

    /// <inheritdoc />
    public TService? GetService<TService>() where TService : class
        => _module as TService;

    /// <summary>Returns the underlying module.</summary>
    public LmpModule Module => _module;
}
