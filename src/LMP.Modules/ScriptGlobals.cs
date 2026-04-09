namespace LMP;

/// <summary>
/// Provides the globals context for C# scripts executed by
/// <see cref="ProgramOfThought{TInput, TOutput}"/>.
/// The LM-generated code can access the user's input via the <c>Input</c> property.
/// </summary>
/// <typeparam name="TInput">The input type.</typeparam>
public class ScriptGlobals<TInput>
{
    /// <summary>
    /// The user's input, accessible from the generated C# script.
    /// </summary>
    public required TInput Input { get; init; }
}
