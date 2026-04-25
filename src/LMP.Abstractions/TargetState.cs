namespace LMP;

/// <summary>
/// Opaque, type-safe container for optimization target state.
/// The runtime type of <see cref="Value"/> is determined by the <see cref="IOptimizationTarget"/>
/// implementation — use <see cref="As{T}"/> to retrieve a typed value.
/// </summary>
/// <param name="Value">The underlying state value.</param>
/// <param name="StateType">The runtime type of <paramref name="Value"/> for validation.</param>
public sealed record TargetState(object Value, Type StateType)
{
    /// <summary>
    /// Creates a <see cref="TargetState"/> capturing the type at construction time.
    /// </summary>
    public static TargetState From<T>(T value) where T : notnull
        => new(value, typeof(T));

    /// <summary>
    /// Returns <see cref="Value"/> as <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Value"/> cannot be cast to <typeparamref name="T"/>.
    /// </exception>
    public T As<T>() where T : class
    {
        if (Value is not T t)
            throw new InvalidOperationException(
                $"TargetState holds {Value.GetType().Name}, expected {typeof(T).Name}.");
        return t;
    }
}
