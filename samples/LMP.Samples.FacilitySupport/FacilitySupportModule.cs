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
        // Run all three sub-tasks concurrently — they're independent.
        // No manual validation needed: C# enum types produce JSON Schema
        // "enum" constraints that are enforced at the API level.
        var urgencyTask = _urgency.PredictAsync(
            input,
            trace: Trace,
            cancellationToken: cancellationToken);

        var sentimentTask = _sentiment.PredictAsync(
            input,
            trace: Trace,
            cancellationToken: cancellationToken);

        var categoryTask = _category.PredictAsync(
            input,
            trace: Trace,
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
