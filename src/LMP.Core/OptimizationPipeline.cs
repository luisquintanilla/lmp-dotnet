using System.Diagnostics;

namespace LMP;

/// <summary>
/// Composes multiple <see cref="IOptimizer"/> steps into a single optimization run.
/// Steps share an <see cref="OptimizationContext"/> and execute in declaration order.
/// Implements <see cref="IOptimizer"/> — pipelines can nest inside other pipelines.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// var result = await module
///     .AsOptimizationPipeline()
///     .WithBudget(b => b.MaxTokens(500_000))
///     .Use(new BootstrapFewShot(maxDemos: 4))
///     .Use(new GEPA(client, generations: 3))
///     .OptimizeAsync(trainSet, devSet, metric, ct);
/// </code>
/// </remarks>
public sealed class OptimizationPipeline : IOptimizer
{
    private readonly IOptimizationTarget _target;
    private readonly List<IOptimizer> _steps = [];
    private CostBudget _budget = CostBudget.Unlimited;
    private ActivitySource? _activitySource;
    private IProgress<OptimizationProgress>? _progress;

    private OptimizationPipeline(IOptimizationTarget target) => _target = target;

    /// <summary>Creates a pipeline for the given module.</summary>
    public static OptimizationPipeline For(LmpModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new OptimizationPipeline(module);
    }

    /// <summary>Creates a pipeline for the given target.</summary>
    public static OptimizationPipeline For(IOptimizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new OptimizationPipeline(target);
    }

    // ── Fluent builder ───────────────────────────────────────────────────

    /// <summary>Adds an optimizer step to the pipeline.</summary>
    public OptimizationPipeline Use(IOptimizer step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
        return this;
    }

    /// <summary>Configures a cost budget. The pipeline stops adding steps when the budget is exceeded.</summary>
    public OptimizationPipeline WithBudget(Action<CostBudget.Builder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new CostBudget.Builder();
        configure(builder);
        _budget = builder.Build();
        return this;
    }

    /// <summary>Attaches an <see cref="ActivitySource"/> for OpenTelemetry spans.</summary>
    public OptimizationPipeline UseTelemetry(ActivitySource activitySource)
    {
        _activitySource = activitySource;
        return this;
    }

    /// <summary>Attaches a progress reporter for streaming optimization status.</summary>
    public OptimizationPipeline UseProgress(IProgress<OptimizationProgress> progress)
    {
        _progress = progress;
        return this;
    }

    /// <summary>Returns the steps in this pipeline (for inspection/testing the invariant).</summary>
    public IReadOnlyList<IOptimizer> Steps => _steps;

    // ── Run ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs all steps and returns an <see cref="OptimizationResult"/> with baseline and
    /// final scores computed on the same evaluation set.
    /// </summary>
    public async Task<OptimizationResult> OptimizeAsync(
        IReadOnlyList<Example> trainSet,
        IReadOnlyList<Example>? devSet,
        Func<Example, object, float> metric,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        var evalSet = devSet?.Count > 0 ? devSet : trainSet;

        var ctx = new OptimizationContext
        {
            Target = _target,
            TrainSet = trainSet,
            DevSet = devSet ?? [],
            Metric = metric,
            Budget = _budget,
            ActivitySource = _activitySource,
            Progress = _progress,
        };

        // Evaluate baseline on the same set we will use for final scoring
        float baseline = await EvaluateAsync(_target, evalSet, metric, ct).ConfigureAwait(false);
        ctx.Diagnostics.BaselineScore = baseline;

        await OptimizeAsync(ctx, ct).ConfigureAwait(false);

        float optimized = await EvaluateAsync(_target, evalSet, metric, ct).ConfigureAwait(false);

        return new OptimizationResult
        {
            Target = _target,
            BaselineScore = baseline,
            OptimizedScore = optimized,
            Trials = ctx.TrialHistory.Trials
        };
    }

    // ── IOptimizer ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Budget.IsWithinBudget(ctx.TrialHistory))
                break;

            using var span = ctx.ActivitySource?.StartActivity(step.GetType().Name);
            await step.OptimizeAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static async Task<float> EvaluateAsync(
        IOptimizationTarget target,
        IReadOnlyList<Example> evalSet,
        Func<Example, object, float> metric,
        CancellationToken ct)
    {
        if (evalSet.Count == 0)
            return 0f;

        float total = 0f;
        int count = 0;

        foreach (var example in evalSet)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (output, _) = await target.ExecuteAsync(example.WithInputs(), ct).ConfigureAwait(false);
                total += metric(example, output);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip failures */ }
        }

        return count == 0 ? 0f : total / count;
    }
}
