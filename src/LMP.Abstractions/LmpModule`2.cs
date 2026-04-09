namespace LMP;

/// <summary>
/// Strongly-typed base class for composable LM programs.
/// Subclass this with your concrete input/output types and override
/// <see cref="ForwardAsync(TInput, CancellationToken)"/> to define multi-step LM logic.
/// Optimizers and evaluators work through the untyped <see cref="LmpModule"/> base
/// automatically via the sealed bridge method.
/// </summary>
/// <typeparam name="TInput">The module's input type.</typeparam>
/// <typeparam name="TOutput">The module's output type.</typeparam>
public abstract class LmpModule<TInput, TOutput> : LmpModule
{
    /// <summary>
    /// Defines the module's typed execution logic. Override this to compose
    /// predictors, assertions, and other modules with full type safety.
    /// </summary>
    /// <param name="input">The typed input to the module.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The module's typed output.</returns>
    public abstract Task<TOutput> ForwardAsync(
        TInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sealed bridge: routes untyped calls from optimizers/evaluator to the typed overload.
    /// </summary>
    public sealed override async Task<object> ForwardAsync(
        object input,
        CancellationToken cancellationToken = default)
        => (object)(await ForwardAsync((TInput)input, cancellationToken))!;
}
