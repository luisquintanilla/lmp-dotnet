using System.Collections.Immutable;

namespace LMP;

/// <summary>
/// Describes the optimization parameter space for an LM program.
/// Maps parameter names to their <see cref="ParameterKind"/> descriptors.
/// </summary>
/// <remarks>
/// <para>
/// Instances are immutable. Use <see cref="Add"/>, <see cref="Remove"/>, and
/// <see cref="Merge"/> to build up a space.
/// </para>
/// <para>
/// Optimizer steps populate <see cref="OptimizationContext.SearchSpace"/> as they
/// discover or refine parameters. Downstream steps read it to seed their search.
/// </para>
/// </remarks>
public sealed class TypedParameterSpace
{
    /// <summary>Empty parameter space (no parameters defined).</summary>
    public static TypedParameterSpace Empty { get; } = new();

    private readonly ImmutableDictionary<string, ParameterKind> _parameters;

    private TypedParameterSpace()
        : this(ImmutableDictionary<string, ParameterKind>.Empty) { }

    private TypedParameterSpace(ImmutableDictionary<string, ParameterKind> parameters)
        => _parameters = parameters;

    // ── Read ────────────────────────────────────────────────────────────

    /// <summary>All defined parameters, indexed by name.</summary>
    public IReadOnlyDictionary<string, ParameterKind> Parameters => _parameters;

    /// <summary>Whether no parameters are defined.</summary>
    public bool IsEmpty => _parameters.IsEmpty;

    /// <summary>
    /// Whether the space contains any <see cref="Continuous"/> parameters.
    /// Samplers that only support categorical search should check this flag.
    /// </summary>
    public bool HasContinuous => _parameters.Values.Any(k => k is Continuous);

    /// <summary>Whether the space contains any <see cref="Subset"/> parameters.</summary>
    public bool HasSubset => _parameters.Values.Any(k => k is Subset);

    // ── Mutation (immutable — returns new instance) ──────────────────────

    /// <summary>
    /// Returns a new space with <paramref name="name"/> mapped to <paramref name="kind"/>.
    /// If <paramref name="name"/> already exists, it is overwritten.
    /// </summary>
    public TypedParameterSpace Add(string name, ParameterKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(kind);
        return new TypedParameterSpace(_parameters.SetItem(name, kind));
    }

    /// <summary>
    /// Returns a new space with <paramref name="name"/> removed.
    /// If <paramref name="name"/> is not present, returns this space unchanged.
    /// </summary>
    public TypedParameterSpace Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _parameters.ContainsKey(name)
            ? new TypedParameterSpace(_parameters.Remove(name))
            : this;
    }

    /// <summary>
    /// Returns a new space that is the union of this space and <paramref name="other"/>.
    /// When a parameter name exists in both, <paramref name="other"/>'s kind wins.
    /// </summary>
    public TypedParameterSpace Merge(TypedParameterSpace other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new TypedParameterSpace(_parameters.SetItems(other._parameters));
    }

    // ── Bridge to/from ISampler's Dictionary<string, int> ───────────────

    /// <summary>
    /// Creates a <see cref="TypedParameterSpace"/> from legacy categorical cardinalities.
    /// Each entry becomes a <see cref="Categorical"/> parameter.
    /// </summary>
    /// <param name="cardinalities">Maps parameter name → number of categories.</param>
    public static TypedParameterSpace FromCategorical(Dictionary<string, int> cardinalities)
    {
        ArgumentNullException.ThrowIfNull(cardinalities);
        var builder = ImmutableDictionary.CreateBuilder<string, ParameterKind>();
        foreach (var (name, count) in cardinalities)
            builder[name] = new Categorical(count);
        return new TypedParameterSpace(builder.ToImmutable());
    }

    /// <summary>
    /// Projects the space to a <c>Dictionary&lt;string, int&gt;</c> for legacy
    /// <see cref="ISampler"/> implementations. Non-<see cref="Categorical"/> parameters
    /// are silently omitted.
    /// </summary>
    public Dictionary<string, int> ToCategoricalDictionary()
        => _parameters
            .Where(p => p.Value is Categorical)
            .ToDictionary(p => p.Key, p => ((Categorical)p.Value).Count);
}
