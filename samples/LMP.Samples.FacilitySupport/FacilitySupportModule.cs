using Microsoft.Extensions.AI;

namespace LMP.Samples.FacilitySupport;

/// <summary>
/// A multi-predictor facility support analysis module.
/// Three independent predictors run in parallel on the same input:
///   1. Urgency classification
///   2. Sentiment analysis
///   3. Service category identification
///
/// GEPA optimizes each predictor's instructions independently —
/// the reflection LLM diagnoses WHY each sub-task fails and evolves
/// targeted instruction improvements.
///
/// Source generator emits GetPredictors() and CloneCore().
/// </summary>
public partial class FacilitySupportModule : LmpModule<SupportInput, AnalysisResult>
{
    private readonly Predictor<SupportInput, UrgencyOutput> _urgency;
    private readonly Predictor<SupportInput, SentimentOutput> _sentiment;
    private readonly Predictor<SupportInput, CategoryOutput> _category;

    /// <summary>Creates a new facility support analysis module.</summary>
    /// <param name="client">The chat client for LM calls.</param>
    public FacilitySupportModule(IChatClient client)
    {
        _urgency = new Predictor<SupportInput, UrgencyOutput>(client) { Name = "urgency" };
        _sentiment = new Predictor<SupportInput, SentimentOutput>(client) { Name = "sentiment" };
        _category = new Predictor<SupportInput, CategoryOutput>(client) { Name = "category" };
    }

    /// <inheritdoc />
    public override async Task<AnalysisResult> ForwardAsync(
        SupportInput input,
        CancellationToken cancellationToken = default)
    {
        // Run all three sub-tasks concurrently — they're independent
        var urgencyTask = _urgency.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
            {
                LmpAssert.That(result,
                    r => r.Urgency is "low" or "medium" or "high" or "critical",
                    "Urgency must be one of: low, medium, high, critical");
            },
            maxRetries: 2,
            cancellationToken: cancellationToken);

        var sentimentTask = _sentiment.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
            {
                LmpAssert.That(result,
                    r => r.Sentiment is "positive" or "neutral" or "negative" or "frustrated",
                    "Sentiment must be one of: positive, neutral, negative, frustrated");
            },
            maxRetries: 2,
            cancellationToken: cancellationToken);

        var categoryTask = _category.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
            {
                LmpAssert.That(result,
                    r => !string.IsNullOrWhiteSpace(r.PrimaryCategory),
                    "Primary category must not be empty");
            },
            maxRetries: 2,
            cancellationToken: cancellationToken);

        await Task.WhenAll(urgencyTask, sentimentTask, categoryTask);

        var urgency = await urgencyTask;
        var sentiment = await sentimentTask;
        var category = await categoryTask;

        return new AnalysisResult(
            Urgency: urgency.Urgency,
            Sentiment: sentiment.Sentiment,
            PrimaryCategory: category.PrimaryCategory,
            SecondaryCategory: category.SecondaryCategory);
    }
}
