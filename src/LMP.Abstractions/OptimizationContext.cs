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
    public IReflectionLog ReflectionLog { get; set; } = new ReflectionLog();

    /// <summary>
    /// Typed diagnostic state shared across optimizer steps (baseline score, opaque
    /// inter-step snapshots). Replaces the untyped <c>Bag</c> from earlier revisions.
    /// </summary>
    public OptimizationDiagnostics Diagnostics { get; } = new();

    /// <summary>
    /// Optional multi-objective metric. When set, optimizers that support vector metrics
    /// use this alongside <see cref="Metric"/> to track cost/quality trade-offs in
    /// <see cref="ParetoBoundary"/>.
    /// </summary>
    public IMetric? VectorMetric { get; set; }

    /// <summary>
    /// Optional multi-objective Pareto frontier populated by cost-aware optimizer steps
    /// such as <c>ModelSelector</c> and <c>MultiFidelity</c>.
    /// </summary>
    public ParetoFrontier? ParetoBoundary { get; set; }

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

}
