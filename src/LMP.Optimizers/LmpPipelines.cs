using Microsoft.Extensions.AI;

namespace LMP.Optimizers;

/// <summary>
/// Factory for creating pre-built <see cref="OptimizationPipeline"/> instances configured
/// for common optimization goals.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>LMP.Optimizers</c> (not <c>LMP.Core</c>) so that it can construct algorithm
/// instances such as <see cref="BootstrapFewShot"/>, <see cref="GEPA"/>, and <see cref="MIPROv2"/>
/// without creating a circular package dependency.
/// </para>
/// <para>
/// The pipeline returned for each goal is a regular <see cref="OptimizationPipeline"/> built
/// from <see cref="OptimizationPipeline.Use"/> calls — fully reproducible and inspectable from
/// Tier 2 code. This upholds the invariant: the Tier 4 one-liner is literally Tier 2's pipeline
/// constructed with defaults.
/// </para>
/// </remarks>
public static class LmpPipelines
{
    /// <summary>
    /// Creates an <see cref="OptimizationPipeline"/> pre-configured for the given optimization goal.
    /// </summary>
    /// <param name="module">The module to optimize.</param>
    /// <param name="client">
    /// Chat client used by LLM-based optimizers (<see cref="GEPA"/>, <see cref="MIPROv2"/>,
    /// <see cref="SIMBA"/>). May be the same model as the module's client, or a
    /// cheaper model for cost efficiency.
    /// </param>
    /// <param name="goal">
    /// Optimization objective that selects the algorithm sequence.
    /// Default is <see cref="Goal.Accuracy"/>.
    /// </param>
    /// <returns>
    /// A configured <see cref="OptimizationPipeline"/> ready to call
    /// <see cref="OptimizationPipeline.OptimizeAsync(System.Collections.Generic.IReadOnlyList{Example},System.Collections.Generic.IReadOnlyList{Example}?,System.Func{Example,object,float},System.Threading.CancellationToken)"/>.
    /// </returns>
    /// <remarks>
    /// Goal → algorithm sequence mapping (Phase J defaults):
    /// <list type="table">
    /// <listheader><term>Goal</term><description>Pipeline</description></listheader>
    /// <item><term>Accuracy</term><description>BootstrapFewShot → GEPA → MIPROv2 → BayesianCalibration</description></item>
    /// <item><term>Speed</term><description>SIMBA</description></item>
    /// <item><term>Cost</term><description>BootstrapFewShot → MIPROv2</description></item>
    /// <item><term>Balanced</term><description>BootstrapFewShot → GEPA → BayesianCalibration</description></item>
    /// </list>
    /// <para>
    /// <see cref="BayesianCalibration"/> is a safe no-op for <see cref="LmpModule"/> pipelines
    /// (its parameter space is always empty). It actively calibrates <see cref="Continuous"/> and
    /// <see cref="Integer"/> hyperparameters when the target is a <c>ChatClientTarget</c>
    /// (temperature, etc.).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/> or <paramref name="client"/> is null.
    /// </exception>
    public static OptimizationPipeline Auto(
        LmpModule module,
        IChatClient client,
        Goal goal = Goal.Accuracy)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(client);

        return goal switch
        {
            Goal.Accuracy => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new GEPA(client))
                .Use(new MIPROv2(client))
                .Use(new BayesianCalibration()),

            Goal.Speed => module.AsOptimizationPipeline()
                .Use(new SIMBA(client)),

            Goal.Cost => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new MIPROv2(client)),

            Goal.Balanced => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new GEPA(client))
                .Use(new BayesianCalibration()),

            _ => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
        };
    }

    /// <summary>
    /// Creates an <see cref="OptimizationPipeline"/> pre-configured to optimize along
    /// a single <see cref="OptimizationAxis"/>.
    /// </summary>
    /// <param name="module">The module to optimize.</param>
    /// <param name="client">
    /// Chat client used by LLM-based optimizers.
    /// May be the same model as the module's client, or a cheaper model for cost efficiency.
    /// </param>
    /// <param name="axis">The optimization axis to target.</param>
    /// <returns>
    /// A configured <see cref="OptimizationPipeline"/> ready to call
    /// <see cref="OptimizationPipeline.OptimizeAsync"/>.
    /// </returns>
    /// <remarks>
    /// Axis → algorithm sequence:
    /// <list type="table">
    /// <listheader><term>Axis</term><description>Pipeline</description></listheader>
    /// <item><term><see cref="OptimizationAxis.Instructions"/></term><description>Delegates to <see cref="Auto"/> with <see cref="Goal.Accuracy"/> (BFS → GEPA → MIPROv2).</description></item>
    /// <item><term><see cref="OptimizationAxis.MultiTurn"/></term><description>BootstrapFewShot → SIMBA.</description></item>
    /// <item><term><see cref="OptimizationAxis.Tools"/></term><description>BootstrapFewShot → MIPROv2 (tool-subset search).</description></item>
    /// <item><term><see cref="OptimizationAxis.Skills"/></term><description>BootstrapFewShot → ContextualBandit on the <c>"skills"</c> parameter. Requires the caller to pre-populate <c>ctx.SearchSpace</c> via <see cref="SkillPoolExtensions.WithSkillPool"/>.</description></item>
    /// <item><term><see cref="OptimizationAxis.Model"/></term><description>MultiFidelity only. Caller must add a <see cref="ModelSelector"/> with the desired model parameter name for full model-selection support.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/> or <paramref name="client"/> is null.
    /// </exception>
    public static OptimizationPipeline ForAxis(
        LmpModule module,
        IChatClient client,
        OptimizationAxis axis)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(client);

        return axis switch
        {
            OptimizationAxis.Instructions => Auto(module, client, Goal.Accuracy),

            OptimizationAxis.MultiTurn => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new SIMBA(client)),

            OptimizationAxis.Tools => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new MIPROv2(client)),

            OptimizationAxis.Skills => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new ContextualBandit("skills")),

            OptimizationAxis.Model => module.AsOptimizationPipeline()
                .Use(new MultiFidelity()),

            _ => Auto(module, client)
        };
    }
}
