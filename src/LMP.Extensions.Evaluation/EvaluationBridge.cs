using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace LMP.Extensions.Evaluation;

/// <summary>
/// Bridges Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/> instances
/// into LMP's metric system (<c>Func&lt;Example, object, Task&lt;float&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// M.E.AI.Evaluation evaluators work at the ChatMessage/ChatResponse level.
/// LMP metrics work at the Example/object (module output) level.
/// This bridge reconstructs a minimal conversation from the LMP Example and
/// module output, then runs the evaluator against it.
/// </remarks>
public static class EvaluationBridge
{
    /// <summary>
    /// Creates an LMP async metric function from an M.E.AI.Evaluation <see cref="IEvaluator"/>.
    /// </summary>
    /// <param name="evaluator">The M.E.AI evaluator to bridge (e.g., CoherenceEvaluator).</param>
    /// <param name="chatConfiguration">
    /// The chat configuration for the evaluator (required for LLM-as-judge evaluators like Coherence).
    /// Pass <c>null</c> for evaluators that don't need an LLM (e.g., F1Evaluator).
    /// </param>
    /// <param name="metricName">
    /// The name of the metric to extract from the <see cref="EvaluationResult"/>.
    /// Must match one of the evaluator's <see cref="IEvaluator.EvaluationMetricNames"/>.
    /// </param>
    /// <param name="maxScore">
    /// The maximum score the evaluator returns (default 5.0 for quality evaluators).
    /// Used to normalize the score to [0, 1] for LMP compatibility.
    /// </param>
    /// <returns>An LMP-compatible async metric function.</returns>
    public static Func<Example, object, Task<float>> CreateMetric(
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration,
        string metricName,
        float maxScore = 5.0f)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxScore, 0);

        return async (example, output) =>
        {
            // Reconstruct a minimal conversation from LMP's Example + output
            var messages = BuildMessages(example, output);
            var modelResponse = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, output?.ToString() ?? ""));

            var result = await evaluator.EvaluateAsync(
                messages, modelResponse, chatConfiguration);

            return ExtractNormalizedScore(result, metricName, maxScore);
        };
    }

    /// <summary>
    /// Creates an LMP typed async metric from an M.E.AI.Evaluation evaluator.
    /// Uses <see cref="Metric.CreateAsync{TPredicted, TExpected}"/> under the hood.
    /// </summary>
    public static Func<Example, object, Task<float>> CreateTypedMetric<TPredicted, TExpected>(
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration,
        string metricName,
        float maxScore = 5.0f)
    {
        // Wrap via LMP's typed async metric factory which handles the
        // Example → (TPredicted, TExpected) unpacking internally
        return Metric.CreateAsync<TPredicted, TExpected>(
            async (predicted, expected) =>
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, "You are a helpful assistant."),
                    new(ChatRole.User, expected?.ToString() ?? "")
                };
                var modelResponse = new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, predicted?.ToString() ?? ""));

                var result = await evaluator.EvaluateAsync(
                    messages, modelResponse, chatConfiguration);

                return ExtractNormalizedScore(result, metricName, maxScore);
            });
    }

    /// <summary>
    /// Runs multiple M.E.AI evaluators and returns a combined score (average of all metrics).
    /// </summary>
    public static Func<Example, object, Task<float>> CreateCombinedMetric(
        IEnumerable<(IEvaluator Evaluator, string MetricName, float Weight)> evaluators,
        ChatConfiguration? chatConfiguration,
        float maxScore = 5.0f)
    {
        var evaluatorList = evaluators.ToList();
        var totalWeight = evaluatorList.Sum(e => e.Weight);

        return async (example, output) =>
        {
            var messages = BuildMessages(example, output);
            var modelResponse = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, output?.ToString() ?? ""));

            float weightedSum = 0f;

            foreach (var (evaluator, metricName, weight) in evaluatorList)
            {
                var result = await evaluator.EvaluateAsync(
                    messages, modelResponse, chatConfiguration);

                var score = ExtractNormalizedScore(result, metricName, maxScore);
                weightedSum += score * weight;
            }

            return weightedSum / totalWeight;
        };
    }

    /// <summary>
    /// Builds a minimal ChatMessage conversation from an LMP Example and output.
    /// </summary>
    private static List<ChatMessage> BuildMessages(Example example, object? output)
    {
        // Include both inputs and the full example representation so evaluators
        // that compare expected vs predicted text have the ground truth available.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, example.ToString() ?? "")
        };

        return messages;
    }

    /// <summary>
    /// Extracts and normalizes a numeric score from an EvaluationResult.
    /// </summary>
    private static float ExtractNormalizedScore(
        EvaluationResult result, string metricName, float maxScore)
    {
        if (result.TryGet<NumericMetric>(metricName, out var numericMetric)
            && numericMetric?.Value is not null)
        {
            return Math.Clamp((float)numericMetric.Value / maxScore, 0f, 1f);
        }

        if (result.TryGet<BooleanMetric>(metricName, out var boolMetric)
            && boolMetric?.Value is not null)
        {
            return boolMetric.Value.Value ? 1f : 0f;
        }

        // Metric not found or null — return 0
        return 0f;
    }
}
