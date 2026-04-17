using System.ComponentModel;

namespace LMP.Samples.MathReasoning;

/// <summary>
/// Input for a math reasoning problem.
/// </summary>
/// <param name="Problem">The math problem statement to solve.</param>
public record MathInput(
    [property: Description("The math problem statement to solve")]
    string Problem);

/// <summary>
/// Output of math reasoning — the final answer extracted from the solution.
/// The optimizer learns which instructions and demos produce correct answers.
/// </summary>
[LmpSignature("Solve the given math problem step by step and provide the final answer")]
public partial record MathAnswer
{
    /// <summary>The final numeric or symbolic answer to the math problem.</summary>
    [Description("The final answer to the math problem (e.g., '42', '3/4', '2\\sqrt{3}')")]
    public required string Answer { get; init; }
}
