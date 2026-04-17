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
    /// Goal → algorithm sequence mapping (Phase B defaults):
    /// <list type="table">
    /// <listheader><term>Goal</term><description>Pipeline</description></listheader>
    /// <item><term>Accuracy</term><description>BootstrapFewShot → GEPA → MIPROv2</description></item>
    /// <item><term>Speed</term><description>SIMBA</description></item>
    /// <item><term>Cost</term><description>BootstrapFewShot → MIPROv2</description></item>
    /// <item><term>Balanced</term><description>BootstrapFewShot → GEPA</description></item>
    /// </list>
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
                .Use(new MIPROv2(client)),

            Goal.Speed => module.AsOptimizationPipeline()
                .Use(new SIMBA(client)),

            Goal.Cost => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new MIPROv2(client)),

            Goal.Balanced => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
                .Use(new GEPA(client)),

            _ => module.AsOptimizationPipeline()
                .Use(new BootstrapFewShot())
        };
    }
}
