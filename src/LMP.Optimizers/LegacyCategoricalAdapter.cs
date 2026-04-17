#pragma warning disable CS0618 // ISampler is obsolete — this adapter is the bridge

namespace LMP.Optimizers;

/// <summary>
/// Bridges a legacy <see cref="ISampler"/> as an <see cref="ISearchStrategy"/>,
/// providing a one-version migration path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LegacyCategoricalAdapter"/> extracts <see cref="Categorical"/> parameters
/// from the <see cref="TypedParameterSpace"/> and forwards them to the inner
/// <see cref="ISampler"/> using its <c>Dictionary&lt;string, int&gt;</c> contract.
/// Non-<see cref="Categorical"/> parameter kinds are silently ignored — the inner
/// sampler never sees them.
/// </para>
/// <para>
/// Use this adapter to wire any existing <see cref="ISampler"/> into a new
/// <see cref="ISearchStrategy"/>-aware pipeline:
/// <code>
/// ISearchStrategy strategy = new LegacyCategoricalAdapter(new SmacSampler(cardinalities));
/// </code>
/// </para>
/// </remarks>
public sealed class LegacyCategoricalAdapter : ISearchStrategy
{
    private readonly ISampler _inner;

    /// <summary>
    /// Creates a new adapter wrapping the given <see cref="ISampler"/>.
    /// </summary>
    /// <param name="inner">The legacy sampler to bridge. Must not be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inner"/> is null.
    /// </exception>
    public LegacyCategoricalAdapter(ISampler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// The number of trials recorded by the inner sampler.
    /// </summary>
    public int TrialCount => _inner.TrialCount;

    /// <summary>
    /// Extracts <see cref="Categorical"/> parameters from <paramref name="space"/>,
    /// delegates to the inner <see cref="ISampler.Propose"/>, and converts the result
    /// to a <see cref="ParameterAssignment"/>.
    /// </summary>
    /// <remarks>
    /// Non-<see cref="Categorical"/> parameters are not passed to the inner sampler.
    /// If the space has no categorical parameters, the inner sampler receives an empty
    /// dictionary and returns an empty proposal.
    /// </remarks>
    public ParameterAssignment Propose(TypedParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);
        var config = _inner.Propose();
        return ParameterAssignment.FromCategorical(config);
    }

    /// <summary>
    /// Converts <paramref name="assignment"/> back to a <c>Dictionary&lt;string, int&gt;</c>
    /// and delegates to the inner <see cref="ISampler.Update(Dictionary{string,int}, float, TrialCost)"/>.
    /// </summary>
    public void Update(ParameterAssignment assignment, float score, TrialCost cost)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var config = assignment.ToCategoricalDictionary();
        _inner.Update(config, score, cost);
    }
}

#pragma warning restore CS0618
