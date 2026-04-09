using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// Intermediate structured output type for the code-generation step
/// of <see cref="ProgramOfThought{TInput, TOutput}"/>. The LM fills in
/// <see cref="Reasoning"/> with its thought process, then generates executable
/// C# code in <see cref="Code"/>.
/// </summary>
public sealed class CodeGenerationOutput
{
    /// <summary>
    /// Step-by-step reasoning the LM uses to plan the code solution.
    /// </summary>
    [Description("Think step by step about how to solve this problem with C# code")]
    [JsonPropertyOrder(-1)]
    public required string Reasoning { get; init; }

    /// <summary>
    /// Executable C# script code that computes and returns the result.
    /// Must be a valid C# script (top-level statements) that evaluates to the answer.
    /// The user's input is available via the <c>Input</c> global variable.
    /// </summary>
    [Description("C# script code that computes and returns the result. " +
                 "Use the 'Input' variable to access the user's input. " +
                 "The last expression is the return value. " +
                 "Available namespaces: System, System.Linq, System.Collections.Generic, System.Text.")]
    public required string Code { get; init; }
}
