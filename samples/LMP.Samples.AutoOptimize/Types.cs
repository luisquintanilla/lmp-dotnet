using System.ComponentModel;

namespace LMP.Samples.AutoOptimize;

/// <summary>Input for simple Q&amp;A.</summary>
public record QAInput(
    [property: Description("The question to answer")]
    string Question);

/// <summary>Output with the answer.</summary>
[LmpSignature("Answer the given question")]
public partial record QAOutput
{
    [Description("The answer to the question")]
    public required string Answer { get; init; }
}
