using System.ComponentModel;

namespace LMP.Samples.FacilitySupport;

/// <summary>
/// Input for facility support analysis — a support email or message.
/// </summary>
/// <param name="Message">The facility support email or message text.</param>
public record SupportInput(
    [property: Description("The facility support email or message text")]
    string Message);

/// <summary>
/// Urgency classification output.
/// </summary>
[LmpSignature("Assess the urgency level of this facility support request")]
public partial record UrgencyOutput
{
    /// <summary>Urgency level of the request.</summary>
    [Description("Urgency level: 'low', 'medium', or 'high'")]
    public required string Urgency { get; init; }
}

/// <summary>
/// Sentiment analysis output.
/// </summary>
[LmpSignature("Analyze the sentiment expressed in this facility support request")]
public partial record SentimentOutput
{
    /// <summary>Sentiment of the message.</summary>
    [Description("Sentiment: 'positive', 'neutral', or 'negative'")]
    public required string Sentiment { get; init; }
}

/// <summary>
/// Service category identification output.
/// </summary>
[LmpSignature("Identify the facility service categories relevant to this support request")]
public partial record CategoryOutput
{
    /// <summary>Primary service category.</summary>
    [Description("Primary service category. Must be one of: 'Routine Maintenance', 'Customer Feedback', 'Training and Support', 'Quality and Safety', 'Sustainability', 'Cleaning Scheduling', 'Specialized Cleaning', 'Emergency Repair', 'Facility Management', 'General Inquiries'")]
    public required string PrimaryCategory { get; init; }

    /// <summary>Secondary service category, if applicable.</summary>
    [Description("Secondary service category from the same list above, or 'None' if only one category applies")]
    public required string SecondaryCategory { get; init; }
}

/// <summary>
/// Combined analysis result — the module's final output.
/// All three sub-task results bundled together for evaluation.
/// </summary>
public record AnalysisResult(
    [property: Description("Urgency level")]
    string Urgency,
    [property: Description("Sentiment")]
    string Sentiment,
    [property: Description("Primary service category")]
    string PrimaryCategory,
    [property: Description("Secondary service category")]
    string SecondaryCategory);
