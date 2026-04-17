namespace LMP.Optimizers;

/// <summary>
/// Factory for selecting an appropriate <see cref="ISampler"/> for the given parameter space.
/// </summary>
/// <remarks>
/// <para>
/// Phase B default: always returns <see cref="CategoricalTpeSampler"/>.
/// Phase D will add budget-aware selection using <see cref="CostAwareSampler"/> when
/// real token-cost tracking is available via <c>ChatClientBuilder.UseLmpTrace(ctx)</c>.
/// </para>
/// </remarks>
public static class AutoSampler
{
    /// <summary>
    /// Creates an <see cref="ISampler"/> appropriate for the given parameter cardinalities.
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
    public static ISampler For(
        Dictionary<string, int> cardinalities,
        double gamma = 0.25,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(cardinalities);
        return new CategoricalTpeSampler(cardinalities, gamma, seed);
    }
}
