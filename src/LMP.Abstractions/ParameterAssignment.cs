namespace LMP;

/// <summary>
/// An assignment of values to named optimization parameters. Phase A: stub.
/// Full implementation (typed lookups, immutable builder) added in Phase C.
/// </summary>
public sealed class ParameterAssignment
{
    /// <summary>Empty assignment (no parameters assigned).</summary>
    public static ParameterAssignment Empty { get; } = new();

    /// <summary>Whether this assignment has no parameters set.</summary>
    public bool IsEmpty => true;
}
