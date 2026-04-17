using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LMP;

/// <summary>
/// Carries all state shared among the steps of an <c>OptimizationPipeline</c> run.
/// Each step reads from and writes to the context fields that belong to its axis.
/// </summary>
public sealed class OptimizationContext
{
    /// <summary>
    /// Creates a context bound to an existing <see cref="IOptimizationTarget"/>.
    /// </summary>
    public static OptimizationContext For(
        IOptimizationTarget target,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        IReadOnlyList<Example>? devSet = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        return new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = metric,
            DevSet = devSet ?? []
        };
    }

    // ── Required ────────────────────────────────────────────────────────

    /// <summary>The target being optimized.</summary>
    public required IOptimizationTarget Target { get; set; }

    /// <summary>Training examples used by optimizer steps.</summary>
    public required IReadOnlyList<Example> TrainSet { get; set; }

    /// <summary>Scoring function: (example, actual output) → score in [0, 1].</summary>
    public required Func<Example, object, float> Metric { get; set; }

    // ── Passed between steps ────────────────────────────────────────────

    /// <summary>Held-out validation set. Empty means use <see cref="TrainSet"/> for evaluation.</summary>
    public IReadOnlyList<Example> DevSet { get; set; } = [];

    /// <summary>Parameter search space, populated and refined by optimizer steps.</summary>
    public TypedParameterSpace SearchSpace { get; set; } = TypedParameterSpace.Empty;

    /// <summary>Accumulated trial history shared across all steps.</summary>
    public TrialHistory TrialHistory { get; } = new();

    /// <summary>Cost budget that controls when the pipeline stops adding new steps.</summary>
    public CostBudget Budget { get; set; } = CostBudget.Unlimited;

    /// <summary>Reflective observations from GEPA, EvaluationCritique, and similar steps.</summary>
    public ReflectionLog ReflectionLog { get; set; } = ReflectionLog.Empty;

    // ── Multi-turn ──────────────────────────────────────────────────────

    /// <summary>
    /// Optional metric for evaluating <see cref="Trajectory"/> output.
    /// Set this when the pipeline includes multi-turn steps that score at the trajectory level.
    /// </summary>
    public ITrajectoryMetric? TrajectoryMetric { get; set; }

    // ── Observability ───────────────────────────────────────────────────

    /// <summary>Optional activity source for OpenTelemetry spans.</summary>
    public ActivitySource? ActivitySource { get; set; }

    /// <summary>Optional structured logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Optional progress reporter for streaming optimization status.</summary>
    public IProgress<OptimizationProgress>? Progress { get; set; }

    // ── Extensibility ───────────────────────────────────────────────────

    /// <summary>
    /// Free-form extensibility bag for inter-step communication not covered by typed fields.
    /// Keys are namespaced by convention, e.g., <c>"gepa:pareto_frontier"</c>.
    /// </summary>
    public IDictionary<string, object> Bag { get; } = new Dictionary<string, object>();
}
