using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LMP.Aspire.Hosting;

/// <summary>
/// Provides OpenTelemetry <see cref="ActivitySource"/> and <see cref="Meter"/>
/// for LMP optimization telemetry. When an Aspire dashboard is connected,
/// these instruments surface as traces and metrics automatically.
/// </summary>
public static class LmpTelemetry
{
    /// <summary>
    /// The name used for the <see cref="ActivitySource"/> and <see cref="Meter"/>.
    /// </summary>
    public const string SourceName = "LMP.Optimization";

    /// <summary>
    /// Activity source for tracing optimization lifecycle events.
    /// Emits activities for optimization runs, individual trials, and evaluations.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>
    /// Meter for optimization metrics (trial scores, iteration counts, durations).
    /// </summary>
    public static Meter Meter { get; } = new(SourceName);

    /// <summary>
    /// Histogram recording the score of each optimization trial.
    /// </summary>
    public static Histogram<double> TrialScore { get; } =
        Meter.CreateHistogram<double>(
            "lmp.optimization.trial.score",
            unit: "{score}",
            description: "Score of each optimization trial on the validation set.");

    /// <summary>
    /// Counter tracking the total number of completed optimization trials.
    /// </summary>
    public static Counter<long> TrialCount { get; } =
        Meter.CreateCounter<long>(
            "lmp.optimization.trial.count",
            unit: "{trial}",
            description: "Total number of optimization trials completed.");

    /// <summary>
    /// Histogram recording the duration of optimization runs in seconds.
    /// </summary>
    public static Histogram<double> OptimizationDuration { get; } =
        Meter.CreateHistogram<double>(
            "lmp.optimization.duration",
            unit: "s",
            description: "Duration of optimization runs in seconds.");

    /// <summary>
    /// Histogram recording the duration of individual evaluations in seconds.
    /// </summary>
    public static Histogram<double> EvaluationDuration { get; } =
        Meter.CreateHistogram<double>(
            "lmp.evaluation.duration",
            unit: "s",
            description: "Duration of evaluation runs in seconds.");

    /// <summary>
    /// Counter tracking the total number of examples evaluated across all runs.
    /// </summary>
    public static Counter<long> ExamplesEvaluated { get; } =
        Meter.CreateCounter<long>(
            "lmp.evaluation.examples",
            unit: "{example}",
            description: "Total number of examples evaluated.");

    /// <summary>
    /// Starts an activity for an optimization run. The caller should dispose the
    /// returned <see cref="Activity"/> when the run completes.
    /// </summary>
    /// <param name="moduleName">The name of the module being optimized.</param>
    /// <param name="optimizerName">The optimizer type name.</param>
    /// <returns>An <see cref="Activity"/> if a listener is registered, otherwise <c>null</c>.</returns>
    public static Activity? StartOptimization(string moduleName, string optimizerName)
    {
        var activity = ActivitySource.StartActivity("lmp.optimize");
        activity?.SetTag("lmp.module", moduleName);
        activity?.SetTag("lmp.optimizer", optimizerName);
        return activity;
    }

    /// <summary>
    /// Starts an activity for a single optimization trial.
    /// </summary>
    /// <param name="trialIndex">The zero-based index of the trial.</param>
    /// <returns>An <see cref="Activity"/> if a listener is registered, otherwise <c>null</c>.</returns>
    public static Activity? StartTrial(int trialIndex)
    {
        var activity = ActivitySource.StartActivity("lmp.optimize.trial");
        activity?.SetTag("lmp.trial.index", trialIndex);
        return activity;
    }

    /// <summary>
    /// Starts an activity for an evaluation run.
    /// </summary>
    /// <param name="datasetSize">The number of examples in the evaluation set.</param>
    /// <returns>An <see cref="Activity"/> if a listener is registered, otherwise <c>null</c>.</returns>
    public static Activity? StartEvaluation(int datasetSize)
    {
        var activity = ActivitySource.StartActivity("lmp.evaluate");
        activity?.SetTag("lmp.evaluation.dataset_size", datasetSize);
        return activity;
    }

    /// <summary>
    /// Records the result of a completed trial.
    /// </summary>
    /// <param name="trialIndex">The zero-based index of the trial.</param>
    /// <param name="score">The trial's score on the validation set.</param>
    /// <param name="activity">The trial activity to annotate (if not null).</param>
    public static void RecordTrialResult(int trialIndex, double score, Activity? activity = null)
    {
        TrialScore.Record(score, new KeyValuePair<string, object?>("lmp.trial.index", trialIndex));
        TrialCount.Add(1);
        activity?.SetTag("lmp.trial.score", score);
    }

    /// <summary>
    /// Records the completion of an evaluation.
    /// </summary>
    /// <param name="averageScore">The average score across all examples.</param>
    /// <param name="exampleCount">The number of examples evaluated.</param>
    /// <param name="durationSeconds">The duration of the evaluation in seconds.</param>
    public static void RecordEvaluation(double averageScore, int exampleCount, double durationSeconds)
    {
        EvaluationDuration.Record(durationSeconds);
        ExamplesEvaluated.Add(exampleCount);
    }
}
