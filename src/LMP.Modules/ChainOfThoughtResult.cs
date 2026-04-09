using System.ComponentModel;
using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// Generic wrapper that extends a <typeparamref name="TOutput"/> with step-by-step reasoning.
/// Used internally by <see cref="ChainOfThought{TInput, TOutput}"/> as the actual type
/// sent to the LM via structured output. The LM fills in <see cref="Reasoning"/> first,
/// then the nested <see cref="Result"/> fields.
/// </summary>
/// <typeparam name="TOutput">The original output type.</typeparam>
public sealed class ChainOfThoughtResult<TOutput> where TOutput : class
{
    /// <summary>
    /// Step-by-step reasoning produced by the LM before the final answer.
    /// Captured in the trace for optimizer consumption but not exposed to the caller.
    /// </summary>
    [Description("Think step by step to work toward the answer")]
    [JsonPropertyOrder(-1)]
    public required string Reasoning { get; init; }

    /// <summary>
    /// The actual output matching the original <typeparamref name="TOutput"/> type.
    /// </summary>
    public required TOutput Result { get; init; }
}
