namespace LMP;

/// <summary>
/// A single training/validation example pairing an input with its expected label.
/// </summary>
/// <typeparam name="TInput">The module's input type.</typeparam>
/// <typeparam name="TLabel">The expected output type (ground truth).</typeparam>
/// <param name="Input">The input data for the example.</param>
/// <param name="Label">The expected output (ground truth) for the example.</param>
public record Example<TInput, TLabel>(TInput Input, TLabel Label)
{
    /// <summary>
    /// Extracts just the input portion — used when running the module
    /// during optimization (inputs go to the module, labels go to the metric).
    /// </summary>
    public TInput WithInputs() => Input;
}
