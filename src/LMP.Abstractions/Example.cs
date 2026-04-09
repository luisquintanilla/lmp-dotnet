namespace LMP;

/// <summary>
/// Non-generic base class for training/validation examples.
/// Optimizers and evaluators work with this type to remain agnostic of
/// concrete TInput/TLabel types.
/// </summary>
public abstract record Example
{
    /// <summary>
    /// Returns the input portion of this example as an untyped object.
    /// Used by optimizers to feed inputs into <see cref="LmpModule.ForwardAsync"/>.
    /// </summary>
    public abstract object WithInputs();

    /// <summary>
    /// Returns the label (ground truth) portion of this example as an untyped object.
    /// Used by metric functions to compare against module output.
    /// </summary>
    public abstract object GetLabel();
}

/// <summary>
/// A single training/validation example pairing an input with its expected label.
/// </summary>
/// <typeparam name="TInput">The module's input type.</typeparam>
/// <typeparam name="TLabel">The expected output type (ground truth).</typeparam>
/// <param name="Input">The input data for the example.</param>
/// <param name="Label">The expected output (ground truth) for the example.</param>
public sealed record Example<TInput, TLabel>(TInput Input, TLabel Label) : Example
{
    /// <inheritdoc/>
    public override object WithInputs() => Input!;

    /// <inheritdoc/>
    public override object GetLabel() => Label!;
}
