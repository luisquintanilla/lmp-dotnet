using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Post-MVP reasoning module: the LM generates C# code, Roslyn scripting executes it,
/// and a structured result is returned. C# all the way down — no Deno, no Python.
/// </summary>
/// <typeparam name="TInput">The input type for the predictor.</typeparam>
/// <typeparam name="TOutput">The output type for the predictor. Must be a reference type.</typeparam>
/// <remarks>
/// <para>
/// The flow is: (1) ask the LM to generate C# code that solves the problem,
/// (2) extract the code from the structured output, (3) execute it via Roslyn scripting,
/// (4) deserialize the result to <typeparamref name="TOutput"/>. If execution fails,
/// the error is fed back to the LM for a retry.
/// </para>
/// <para>
/// The generated script has access to the user's input via a <c>Input</c> global variable
/// of type <typeparamref name="TInput"/>. Available namespaces are restricted to:
/// <c>System</c>, <c>System.Linq</c>, <c>System.Collections.Generic</c>,
/// <c>System.Text</c>, <c>System.Text.Json</c>.
/// </para>
/// </remarks>
public class ProgramOfThought<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private static readonly string[] s_defaultImports =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.Text",
        "System.Text.Json"
    ];

    private readonly Predictor<TInput, CodeGenerationOutput> _codeGen;
    private readonly TimeSpan _executionTimeout;

    /// <summary>
    /// Creates a ProgramOfThought predictor that generates and executes C# code.
    /// </summary>
    /// <param name="client">The chat client to use for LM calls.</param>
    /// <param name="executionTimeout">
    /// Maximum time allowed for script execution (default: 30 seconds).
    /// Prevents infinite loops in LM-generated code.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    public ProgramOfThought(IChatClient client, TimeSpan? executionTimeout = null)
        : base(client)
    {
        _executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(30);

        _codeGen = new Predictor<TInput, CodeGenerationOutput>(client)
        {
            Instructions = BuildCodeGenInstructions()
        };
    }

    /// <summary>
    /// Gets the maximum execution time allowed for generated scripts.
    /// </summary>
    public TimeSpan ExecutionTimeout => _executionTimeout;

    /// <summary>
    /// Gets the internal code generation predictor. Exposed for optimizer access
    /// to learnable state (instructions, demos).
    /// </summary>
    internal Predictor<TInput, CodeGenerationOutput> CodeGenerator => _codeGen;

    /// <summary>
    /// Executes the ProgramOfThought flow: asks the LM to generate C# code,
    /// executes it via Roslyn scripting, and returns the typed result.
    /// If execution fails, retries with error feedback in the prompt.
    /// </summary>
    /// <param name="input">The input to process.</param>
    /// <param name="trace">Optional trace for recording invocations.</param>
    /// <param name="validate">Optional validation delegate for the final result.</param>
    /// <param name="maxRetries">Maximum retry attempts on failure (default 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result computed by the LM-generated C# code.</returns>
    public override async Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        Action<TOutput>? validate = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        string? lastError = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            // Step 1: Ask LM to generate C# code
            CodeGenerationOutput codeOutput;
            try
            {
                codeOutput = await _codeGen.PredictAsync(
                    input, trace, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastError = $"Code generation failed: {ex.Message}";
                _codeGen.Instructions = BuildCodeGenInstructions(lastError);
                continue;
            }

            // Step 2: Execute the generated code
            TOutput result;
            try
            {
                result = await ExecuteCodeAsync(codeOutput.Code, input, cancellationToken);
            }
            catch (CompilationErrorException ex)
            {
                lastError = $"Compilation error: {ex.Message}";
                _codeGen.Instructions = BuildCodeGenInstructions(lastError);
                if (attempt >= maxRetries)
                    throw new LmpMaxRetriesExceededException(Name, maxRetries);
                continue;
            }
            catch (TimeoutException)
            {
                lastError = "Script execution timed out. Simplify the algorithm or reduce complexity.";
                _codeGen.Instructions = BuildCodeGenInstructions(lastError);
                if (attempt >= maxRetries)
                    throw new LmpMaxRetriesExceededException(Name, maxRetries);
                continue;
            }
            catch (Exception ex)
            {
                lastError = $"Runtime error: {ex.Message}";
                _codeGen.Instructions = BuildCodeGenInstructions(lastError);
                if (attempt >= maxRetries)
                    throw new LmpMaxRetriesExceededException(Name, maxRetries);
                continue;
            }

            trace?.Record(Name, input!, result);

            // Step 3: Validate the result if a validator is provided
            if (validate is null)
                return result;

            try
            {
                validate(result);
                return result;
            }
            catch (LmpAssertionException ex)
            {
                lastError = $"Validation failed: {ex.Message}";
                _codeGen.Instructions = BuildCodeGenInstructions(lastError);
            }
        }

        throw new LmpMaxRetriesExceededException(Name, maxRetries);
    }

    /// <summary>
    /// Executes LM-generated C# code via Roslyn scripting with timeout protection.
    /// </summary>
    internal async Task<TOutput> ExecuteCodeAsync(
        string code, TInput input, CancellationToken cancellationToken)
    {
        var globals = new ScriptGlobals<TInput> { Input = input };

        var options = ScriptOptions.Default
            .WithImports(s_defaultImports)
            .WithReferences(
                typeof(object).Assembly,                              // System.Runtime
                typeof(Enumerable).Assembly,                          // System.Linq
                typeof(List<>).Assembly,                              // System.Collections.Generic
                typeof(JsonSerializer).Assembly,                      // System.Text.Json
                typeof(TInput).Assembly,                              // User's input type assembly
                typeof(TOutput).Assembly                              // User's output type assembly
            );

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_executionTimeout);

        try
        {
            var state = await CSharpScript.RunAsync(
                code, options, globals, typeof(ScriptGlobals<TInput>), cts.Token);

            return ConvertResult(state.ReturnValue);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Script execution exceeded the timeout of {_executionTimeout.TotalSeconds}s.");
        }
    }

    private static TOutput ConvertResult(object? returnValue)
    {
        if (returnValue is null)
            throw new InvalidOperationException(
                "Script returned null. The last expression must evaluate to a non-null value.");

        if (returnValue is TOutput typed)
            return typed;

        // Try JSON round-trip for structural conversion (e.g., anonymous type → TOutput)
        var json = JsonSerializer.Serialize(returnValue);
        return JsonSerializer.Deserialize<TOutput>(json)
            ?? throw new InvalidOperationException(
                $"Could not convert script result of type '{returnValue.GetType().Name}' to '{typeof(TOutput).Name}'.");
    }

    private string BuildCodeGenInstructions(string? lastError = null)
    {
        var baseInstructions = $"""
            You are a C# code generator. Given the input, write a C# script that computes the answer.

            Rules:
            - The script is executed via Roslyn scripting (top-level statements, no Main method).
            - Access the user's input via the `Input` variable (type: {typeof(TInput).Name}).
            - The LAST expression in the script is the return value.
            - Available namespaces: System, System.Linq, System.Collections.Generic, System.Text, System.Text.Json.
            - The result type must be: {typeof(TOutput).Name}
            - Do NOT use Console.WriteLine or other side effects.
            - Keep the code simple and efficient.
            """;

        if (!string.IsNullOrEmpty(Instructions))
            baseInstructions = Instructions + "\n\n" + baseInstructions;

        if (lastError is not null)
            baseInstructions += $"\n\nPrevious attempt failed: {lastError}. Fix the issue.";

        return baseInstructions;
    }
}
