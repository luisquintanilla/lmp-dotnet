using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace LMP.Extensions.Evaluation;

/// <summary>
/// Optimizer step that runs M.E.AI.Evaluation evaluators against trial outputs
/// and writes critique rationale into <see cref="OptimizationContext.ReflectionLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>EvaluationCritique</c> does <em>not</em> modify the target's parameters or state.
/// Its only output is an enriched <see cref="ReflectionLog"/> that downstream optimizer
/// steps — particularly <c>GEPA</c> — can use to improve instructions without extra LLM calls.
/// </para>
/// <para>
/// Place <c>EvaluationCritique</c> <em>before</em> <c>GEPA</c> in a pipeline:
/// <code>
/// var pipeline = OptimizationPipeline.Empty
///     .Use(new EvaluationCritique(coherenceEvaluator, chatConfig, "Coherence"))
///     .Use(new GEPA(reflectionClient));
/// </code>
/// GEPA's <c>ReflectOnPredictor</c> will then include the critique observations as
/// additional context when proposing improved instructions.
/// </para>
/// </remarks>
public sealed class EvaluationCritique : IOptimizer
{
    private readonly IEvaluator _evaluator;
    private readonly ChatConfiguration? _chatConfiguration;
    private readonly string _metricName;
    private readonly float _maxScore;
    private readonly int _maxExamples;

    /// <summary>
    /// Creates an <see cref="EvaluationCritique"/> optimizer.
    /// </summary>
    /// <param name="evaluator">The M.E.AI.Evaluation evaluator to run per trial.</param>
    /// <param name="chatConfiguration">
    /// Chat configuration for LLM-as-judge evaluators (e.g., Coherence).
    /// Pass <c>null</c> for deterministic evaluators (e.g., F1).
    /// </param>
    /// <param name="metricName">
    /// Name of the metric to extract from the evaluator's result.
    /// Must match one of <see cref="IEvaluator.EvaluationMetricNames"/>.
    /// </param>
    /// <param name="maxScore">Maximum score the evaluator returns (default 5.0).</param>
    /// <param name="maxExamples">
    /// Maximum number of training examples to evaluate (default 10).
    /// Use a small value to keep costs down.
    /// </param>
    /// <exception cref="ArgumentNullException">When <paramref name="evaluator"/> is null.</exception>
    public EvaluationCritique(
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration,
        string metricName,
        float maxScore = 5.0f,
        int maxExamples = 10)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxScore, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExamples, 1);

        _evaluator = evaluator;
        _chatConfiguration = chatConfiguration;
        _metricName = metricName;
        _maxScore = maxScore;
        _maxExamples = maxExamples;
    }

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        if (ctx.TrainSet.Count == 0)
            return;

        var evalSet = ctx.TrainSet.Count <= _maxExamples
            ? ctx.TrainSet
            : ctx.TrainSet.Take(_maxExamples).ToList();

        long totalTokens = 0;
        int totalTurns = 0;
        var sw = Stopwatch.StartNew();

        foreach (var example in evalSet)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory)) break;

            object output;
            Trace trace;
            try
            {
                (output, trace) = await ctx.Target.ExecuteAsync(example.WithInputs(), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                continue; // Skip examples where execution failed
            }

            totalTokens += trace.TotalTokens;
            totalTurns += trace.TotalApiCalls;

            float score = ctx.Metric(example, output);

            // Run M.E.AI evaluator on this example's output
            try
            {
                var messages = BuildMessages(example, output);
                var modelResponse = new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, output?.ToString() ?? ""));

                var result = await _evaluator.EvaluateAsync(
                    messages, modelResponse, _chatConfiguration, cancellationToken: ct);

                var rationale = ExtractRationale(result);
                float normalizedScore = ExtractScore(result);

                if (!string.IsNullOrWhiteSpace(rationale))
                {
                    ctx.ReflectionLog.Add(
                        text: rationale,
                        source: nameof(EvaluationCritique),
                        scope: ReflectionScope.Global,
                        score: normalizedScore);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Evaluator failed — skip this example's critique entry
            }

            ctx.TrialHistory.Add(new Trial(score,
                new TrialCost(totalTokens, totalTokens / 2, totalTokens / 2,
                    sw.ElapsedMilliseconds, totalTurns)));
        }

        sw.Stop();
    }

    private static List<ChatMessage> BuildMessages(Example example, object? output) =>
    [
        new(ChatRole.System, "You are a helpful assistant."),
        new(ChatRole.User, example.ToString() ?? "")
    ];

    private string ExtractRationale(EvaluationResult result)
    {
        if (result.TryGet<NumericMetric>(_metricName, out var numericMetric))
            return numericMetric?.Reason ?? "";

        if (result.TryGet<BooleanMetric>(_metricName, out var boolMetric))
            return boolMetric?.Reason ?? "";

        return "";
    }

    private float ExtractScore(EvaluationResult result)
    {
        if (result.TryGet<NumericMetric>(_metricName, out var numericMetric)
            && numericMetric?.Value is not null)
            return Math.Clamp((float)numericMetric.Value / _maxScore, 0f, 1f);

        if (result.TryGet<BooleanMetric>(_metricName, out var boolMetric)
            && boolMetric?.Value is not null)
            return boolMetric.Value.Value ? 1f : 0f;

        return 0f;
    }
}
