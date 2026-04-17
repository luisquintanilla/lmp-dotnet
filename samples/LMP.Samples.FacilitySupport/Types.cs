using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LMP.Samples.FacilitySupport;

/// <summary>Urgency levels for facility support requests.</summary>
public enum UrgencyLevel
{
    Low,
    Medium,
    High
}

/// <summary>Sentiment classifications for facility support messages.</summary>
public enum SentimentLevel
{
    Positive,
    Neutral,
    Negative
}

/// <summary>Facility service categories from the FacilitySupportAnalyzer dataset.</summary>
public enum ServiceCategory
{
    [JsonStringEnumMemberName("Routine Maintenance")]
    RoutineMaintenance,
    [JsonStringEnumMemberName("Customer Feedback")]
    CustomerFeedback,
    [JsonStringEnumMemberName("Training and Support")]
    TrainingAndSupport,
    [JsonStringEnumMemberName("Quality and Safety")]
    QualityAndSafety,
    [JsonStringEnumMemberName("Sustainability")]
    Sustainability,
    [JsonStringEnumMemberName("Cleaning Scheduling")]
    CleaningScheduling,
    [JsonStringEnumMemberName("Specialized Cleaning")]
    SpecializedCleaning,
    [JsonStringEnumMemberName("Emergency Repair")]
    EmergencyRepair,
    [JsonStringEnumMemberName("Facility Management")]
    FacilityManagement,
    [JsonStringEnumMemberName("General Inquiries")]
    GeneralInquiries,
    None
}

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
    [Description("Urgency level of the request")]
    public required UrgencyLevel Urgency { get; init; }
}

/// <summary>
/// Sentiment analysis output.
/// </summary>
[LmpSignature("Analyze the sentiment expressed in this facility support request")]
public partial record SentimentOutput
{
    /// <summary>Sentiment of the message.</summary>
    [Description("Sentiment expressed in the message")]
    public required SentimentLevel Sentiment { get; init; }
}

/// <summary>
/// Service category identification output.
/// </summary>
[LmpSignature("Identify the facility service categories relevant to this support request")]
public partial record CategoryOutput
{
    /// <summary>Primary service category.</summary>
    [Description("Primary service category for this request")]
    public required ServiceCategory PrimaryCategory { get; init; }

    /// <summary>Secondary service category, if applicable.</summary>
    [Description("Secondary service category, or None if only one category applies")]
    public required ServiceCategory SecondaryCategory { get; init; }
}

/// <summary>
/// Combined analysis result — the module's final output.
/// All three sub-task results bundled together for evaluation.
/// </summary>
public record AnalysisResult(
    [property: Description("Urgency level")]
    UrgencyLevel Urgency,
    [property: Description("Sentiment")]
    SentimentLevel Sentiment,
    [property: Description("Primary service category")]
    ServiceCategory PrimaryCategory,
    [property: Description("Secondary service category")]
    ServiceCategory SecondaryCategory);
