namespace LMP;

/// <summary>
/// Typed replacement for <see cref="ISampler"/>. Proposes parameter assignments
/// from a <see cref="TypedParameterSpace"/> and incorporates trial feedback.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ISampler"/>, which is limited to categorical parameters encoded as
/// <c>Dictionary&lt;string, int&gt;</c>, <see cref="ISearchStrategy"/> accepts the full
/// <see cref="TypedParameterSpace"/> — including <see cref="Continuous"/>, <see cref="Integer"/>,
/// <see cref="StringValued"/>, and <see cref="Subset"/> parameters — and returns a
/// typed <see cref="ParameterAssignment"/>.
/// </para>
/// <para>
/// Existing <see cref="ISampler"/> implementations can be bridged via
/// <c>LegacyCategoricalAdapter</c> in <c>LMP.Optimizers</c>. All three built-in samplers
/// (<c>CategoricalTpeSampler</c>, <c>SmacSampler</c>, <c>CostAwareSampler</c>) implement
/// both interfaces for backward compatibility.
/// </para>
/// </remarks>
public interface ISearchStrategy
{
    /// <summary>
    /// Proposes a new parameter assignment to evaluate.
    /// </summary>
    /// <param name="space">
    /// The parameter space describing valid assignments. Strategies that only support
    /// categorical parameters should extract <see cref="Categorical"/> dims and
    /// ignore unsupported kinds.
    /// </param>
    /// <returns>
    /// A <see cref="ParameterAssignment"/> mapping each parameter name to a proposed value.
    /// </returns>
    ParameterAssignment Propose(TypedParameterSpace space);

    /// <summary>
    /// Incorporates the result of evaluating a proposed assignment to refine future proposals.
    /// </summary>
    /// <param name="assignment">The assignment that was evaluated.</param>
    /// <param name="score">Observed metric score (higher is better).</param>
    /// <param name="cost">Resource cost of the evaluation (tokens, latency, API calls).</param>
    void Update(ParameterAssignment assignment, float score, TrialCost cost);
}
