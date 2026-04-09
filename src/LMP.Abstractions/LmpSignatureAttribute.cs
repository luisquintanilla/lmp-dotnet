namespace LMP;

/// <summary>
/// Marks a partial record as an LM output type — a typed contract
/// defining instructions and output fields for a single LM interaction.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LmpSignatureAttribute(string instructions) : Attribute
{
    /// <summary>
    /// Task-level instructions sent to the LM. Describes what the LM should do.
    /// </summary>
    public string Instructions { get; } = instructions;
}
