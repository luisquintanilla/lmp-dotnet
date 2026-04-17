#pragma warning disable CS0618 // references obsolete ISampler intentionally for the legacy overload

namespace LMP.Optimizers;

/// <summary>
/// Factory for selecting an appropriate <see cref="ISearchStrategy"/> (or legacy
/// <see cref="ISampler"/>) for the given parameter space and budget.
/// </summary>
/// <remarks>
/// <para>
/// Selection logic:
/// <list type="table">
/// <listheader><term>Condition</term><description>Returned strategy</description></listheader>
/// <item>
///   <term><see cref="CostBudget.MaxTokens"/> is finite</term>
///   <description><see cref="CostAwareSampler"/> — adapts step size based on token cost</description>
/// </item>
/// <item>
///   <term>Otherwise</term>
///   <description><see cref="CategoricalTpeSampler"/> — TPE acquisition with gamma = 0.25</description>
/// </item>
/// </list>
/// </para>
/// <para>
/// Phase D will add <see cref="SmacSampler"/> selection when the space contains
/// <see cref="Continuous"/> parameters, once real token-cost tracking is available
/// via <c>ChatClientBuilder.UseLmpTrace(ctx)</c>.
/// </para>
/// </remarks>
public static class AutoSampler
{
    /// <summary>
    /// Creates an <see cref="ISearchStrategy"/> appropriate for the given
    /// <see cref="TypedParameterSpace"/> and optional <see cref="CostBudget"/>.
    /// </summary>
    /// <param name="space">
    /// The parameter space to search. Only <see cref="Categorical"/> dimensions are
    /// used by the returned strategy in Phase C; other kinds are extracted via
    /// <see cref="TypedParameterSpace.ToCategoricalDictionary"/>.
    /// </param>
    /// <param name="budget">
    /// Optional cost budget. When <see cref="CostBudget.MaxTokens"/> is finite,
    /// returns a <see cref="CostAwareSampler"/>; otherwise <see cref="CategoricalTpeSampler"/>.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <returns>
    /// A configured <see cref="ISearchStrategy"/> ready to drive a search loop.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="space"/> is null.
    /// </exception>
    public static ISearchStrategy For(
        TypedParameterSpace space,
        CostBudget? budget = null,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(space);

        var cardinalities = space.ToCategoricalDictionary();

        if (budget?.MaxTokens.HasValue == true && cardinalities.Count > 0)
            return new CostAwareSampler(cardinalities, seed: seed ?? 42);

        return new CategoricalTpeSampler(cardinalities, seed: seed);
    }

    /// <summary>
    /// Creates an <see cref="ISampler"/> appropriate for the given categorical cardinalities.
    /// </summary>
    /// <param name="cardinalities">Maps parameter name → number of categorical values.</param>
    /// <param name="gamma">
    /// TPE quantile threshold. Top <paramref name="gamma"/> fraction of trials are considered
    /// "good" candidates. Must be in (0, 1). Default is 0.25.
    /// </param>
    /// <param name="seed">Optional random seed for reproducibility.</param>
    /// <returns>A <see cref="CategoricalTpeSampler"/> configured for the given space.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="cardinalities"/> is null.
    /// </exception>
    [Obsolete("Use For(TypedParameterSpace, CostBudget?) for typed parameter spaces and budget-aware selection.")]
    public static ISampler For(
        Dictionary<string, int> cardinalities,
        double gamma = 0.25,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(cardinalities);
        return new CategoricalTpeSampler(cardinalities, gamma, seed);
    }
}

#pragma warning restore CS0618
