using System.Collections.Immutable;

namespace LMP;

/// <summary>
/// An immutable assignment of values to named optimization parameters,
/// as proposed by an <see cref="ISearchStrategy"/>.
/// </summary>
/// <remarks>
/// Values are typed by the <see cref="ParameterKind"/> of their corresponding parameter:
/// <list type="table">
/// <listheader><term>ParameterKind</term><description>Value type in assignment</description></listheader>
/// <item><term><see cref="Categorical"/></term><description><c>int</c> (category index)</description></item>
/// <item><term><see cref="Integer"/></term><description><c>int</c></description></item>
/// <item><term><see cref="Continuous"/></term><description><c>double</c></description></item>
/// <item><term><see cref="StringValued"/></term><description><c>string</c></description></item>
/// <item><term><see cref="Subset"/></term><description><c>IReadOnlyList&lt;object&gt;</c></description></item>
/// <item><term><see cref="Composite"/></term><description><see cref="ParameterAssignment"/></description></item>
/// </list>
/// </remarks>
public sealed class ParameterAssignment
{
    /// <summary>Empty assignment (no parameters assigned).</summary>
    public static ParameterAssignment Empty { get; } = new();

    private readonly ImmutableDictionary<string, object> _values;

    private ParameterAssignment()
        : this(ImmutableDictionary<string, object>.Empty) { }

    private ParameterAssignment(ImmutableDictionary<string, object> values)
        => _values = values;

    // ── Read ────────────────────────────────────────────────────────────

    /// <summary>All assigned parameter values, indexed by name.</summary>
    public IReadOnlyDictionary<string, object> Values => _values;

    /// <summary>Whether no parameters are assigned.</summary>
    public bool IsEmpty => _values.IsEmpty;

    /// <summary>
    /// Gets the assigned value for <paramref name="name"/>, cast to <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Parameter not found.</exception>
    /// <exception cref="InvalidCastException">Value is not of type <typeparamref name="T"/>.</exception>
    public T Get<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_values.TryGetValue(name, out var val))
            throw new KeyNotFoundException($"Parameter '{name}' not found in assignment.");
        return (T)val;
    }

    /// <summary>
    /// Tries to get the assigned value for <paramref name="name"/>,
    /// cast to <typeparamref name="T"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the parameter exists and can be cast; otherwise <see langword="false"/>.</returns>
    public bool TryGet<T>(string name, out T value)
    {
        if (!string.IsNullOrWhiteSpace(name) && _values.TryGetValue(name, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    // ── Mutation (immutable — returns new instance) ──────────────────────

    /// <summary>
    /// Returns a new assignment with <paramref name="name"/> set to <paramref name="value"/>.
    /// If <paramref name="name"/> already exists, it is overwritten.
    /// </summary>
    public ParameterAssignment With(string name, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return new ParameterAssignment(_values.SetItem(name, value));
    }

    // ── Bridge to/from ISampler's Dictionary<string, int> ───────────────

    /// <summary>
    /// Creates a <see cref="ParameterAssignment"/> from a legacy categorical configuration
    /// (parameter name → category index).
    /// </summary>
    public static ParameterAssignment FromCategorical(Dictionary<string, int> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var builder = ImmutableDictionary.CreateBuilder<string, object>();
        foreach (var (name, idx) in config)
            builder[name] = (object)idx;
        return new ParameterAssignment(builder.ToImmutable());
    }

    /// <summary>
    /// Projects this assignment to a <c>Dictionary&lt;string, int&gt;</c> for legacy
    /// <see cref="ISampler"/> implementations. Non-integer values are silently omitted.
    /// </summary>
    public Dictionary<string, int> ToCategoricalDictionary()
        => _values
            .Where(p => p.Value is int)
            .ToDictionary(p => p.Key, p => (int)p.Value);
}
