using System.ComponentModel;

namespace LMP.Samples.IntentClassification;

/// <summary>
/// Input for intent classification — a customer banking query.
/// </summary>
/// <param name="Query">The customer's banking query to classify.</param>
public record ClassifyInput(
    [property: Description("The customer's banking query to classify")]
    string Query);

/// <summary>
/// Output of intent classification — one of 77 Banking77 intent labels.
/// The optimizer learns which few-shot demos best guide the model
/// to pick the correct fine-grained intent from 77 possibilities.
/// </summary>
[LmpSignature("Classify the customer's banking query into one of the predefined intent categories. Return the exact intent label.")]
public partial record ClassifyOutput
{
    /// <summary>The predicted intent label (must be one of the 77 Banking77 intents).</summary>
    [Description("The intent label for this query (e.g., 'card_arrival', 'lost_or_stolen_card', 'exchange_rate')")]
    public required string Intent { get; init; }
}
