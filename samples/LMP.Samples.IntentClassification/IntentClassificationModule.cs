using Microsoft.Extensions.AI;

namespace LMP.Samples.IntentClassification;

/// <summary>
/// A single-predictor classification module for Banking77 intents.
/// Demonstrates that even a simple Predictor benefits enormously
/// from few-shot demo selection on high-cardinality classification.
/// Source generator emits GetPredictors() and CloneCore().
/// </summary>
public partial class IntentClassificationModule : LmpModule<ClassifyInput, ClassifyOutput>
{
    private readonly Predictor<ClassifyInput, ClassifyOutput> _classify;

    /// <summary>Creates a new intent classification module.</summary>
    /// <param name="client">The chat client for LM calls.</param>
    public IntentClassificationModule(IChatClient client)
    {
        _classify = new Predictor<ClassifyInput, ClassifyOutput>(client) { Name = "classify" };
    }

    /// <inheritdoc />
    public override async Task<ClassifyOutput> ForwardAsync(
        ClassifyInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _classify.PredictAsync(
            input,
            trace: Trace,
            validate: output =>
                LmpAssert.That(output,
                    o => !string.IsNullOrWhiteSpace(o.Intent),
                    "Intent label must not be empty"),
            maxRetries: 2,
            cancellationToken: cancellationToken);

        return result;
    }
}
