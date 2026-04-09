namespace LMP;

/// <summary>
/// Records predictor invocations during a <see cref="LmpModule.ForwardAsync"/> call.
/// Optimizers collect traces from successful examples and use them as few-shot demos.
/// </summary>
public sealed class Trace
{
    private readonly List<TraceEntry> _entries = [];

    /// <summary>
    /// All recorded trace entries in invocation order.
    /// </summary>
    public IReadOnlyList<TraceEntry> Entries => _entries;

    /// <summary>
    /// Records a predictor invocation with its input and output.
    /// </summary>
    /// <param name="predictorName">Name of the predictor that was invoked.</param>
    /// <param name="input">The input passed to the predictor.</param>
    /// <param name="output">The output returned by the predictor.</param>
    public void Record(string predictorName, object input, object output)
    {
        _entries.Add(new TraceEntry(predictorName, input, output));
    }
}

/// <summary>
/// A single predictor invocation record: predictor name, input, and output.
/// </summary>
/// <param name="PredictorName">Name of the predictor that was invoked.</param>
/// <param name="Input">The input passed to the predictor.</param>
/// <param name="Output">The output returned by the predictor.</param>
public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output);
