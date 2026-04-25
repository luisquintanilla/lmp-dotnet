namespace LMP;

/// <summary>
/// Adapts a delegate function as an <see cref="IOptimizationTarget"/>.
/// Useful for wrapping arbitrary async functions in the optimization pipeline
/// without defining a full <see cref="LmpModule"/> subclass.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DelegateTarget"/> has no learnable state and no parameter space.
/// It is a thin execution shell — all optimization must occur externally
/// (e.g., by composing with other targets via <see cref="OptimizationTargetExtensions.Then"/>).
/// </para>
/// <para>
/// Usage:
/// <code>
/// var target = DelegateTarget.For(async (input, ct) =>
/// {
///     var question = (string)input;
///     return await AnswerAsync(question, ct);
/// });
/// </code>
/// </para>
/// </remarks>
public sealed class DelegateTarget : IOptimizationTarget
{
    private readonly Func<object, CancellationToken, Task<object>> _func;

    private DelegateTarget(Func<object, CancellationToken, Task<object>> func)
        => _func = func;

    /// <summary>
    /// Creates a <see cref="DelegateTarget"/> from a cancellable async function.
    /// </summary>
    /// <param name="func">The function to wrap. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="func"/> is null.</exception>
    public static DelegateTarget For(Func<object, CancellationToken, Task<object>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return new DelegateTarget(func);
    }

    /// <summary>
    /// Creates a <see cref="DelegateTarget"/> from a simple async function without cancellation.
    /// </summary>
    /// <param name="func">The function to wrap. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="func"/> is null.</exception>
    public static DelegateTarget For(Func<object, Task<object>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return new DelegateTarget((input, _) => func(input));
    }

    /// <inheritdoc />
    public TargetShape Shape => TargetShape.SingleTurn;

    /// <inheritdoc />
    public async Task<(object Output, Trace Trace)> ExecuteAsync(
        object input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = await _func(input, ct).ConfigureAwait(false);
        return (output, new Trace());
    }

    /// <inheritdoc />
    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;

    /// <inheritdoc />
    public TargetState GetState() => TargetState.From(DelegateTargetState.Empty);

    /// <inheritdoc />
    public void ApplyState(TargetState state) { }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="DelegateTarget"/> has no parameters. Returns a new <see cref="DelegateTarget"/>
    /// wrapping the same delegate, satisfying the clone contract for parallel trial evaluation.
    /// </remarks>
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new DelegateTarget(_func);
    }

    /// <inheritdoc />
    public TService? GetService<TService>() where TService : class => null;
}

/// <summary>
/// Placeholder state for <see cref="DelegateTarget"/>, which has no learnable state.
/// </summary>
internal sealed record DelegateTargetState
{
    /// <summary>The singleton empty state instance.</summary>
    public static DelegateTargetState Empty { get; } = new();
}
