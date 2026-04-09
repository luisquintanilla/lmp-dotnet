using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Chain-of-thought prompting: extends the output type with a Reasoning field
/// so the LM "thinks out loud" before answering. The reasoning is captured in
/// the trace for optimizer consumption but stripped from the returned result.
/// </summary>
/// <typeparam name="TInput">The input type for the predictor.</typeparam>
/// <typeparam name="TOutput">The output type for the predictor. Must be a reference type.</typeparam>
/// <remarks>
/// At compile time, the source generator detects <c>ChainOfThought&lt;TIn, TOut&gt;</c> usages
/// and emits an extended output record (<c>{TOut}WithReasoning</c>) with a Reasoning field
/// prepended before the original output fields. At runtime, the generic
/// <see cref="ChainOfThoughtResult{TOutput}"/> wrapper is used to call the LM with reasoning enabled.
/// </remarks>
public class ChainOfThought<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly IChatClient _chatClient;

    /// <summary>
    /// Creates a chain-of-thought predictor bound to the given chat client.
    /// </summary>
    /// <param name="client">The chat client to use for LM calls.</param>
    public ChainOfThought(IChatClient client) : base(client)
    {
        _chatClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Executes a chain-of-thought prediction: asks the LM to reason step by step,
    /// then extracts the final <typeparamref name="TOutput"/> from the extended response.
    /// The reasoning is captured in the trace via the extended result type.
    /// </summary>
    /// <param name="input">The input to predict from.</param>
    /// <param name="trace">Optional trace for recording the invocation (includes reasoning).</param>
    /// <param name="validate">
    /// Optional validation delegate. Called after each prediction attempt.
    /// Should use <see cref="LmpAssert.That{T}"/> to throw <see cref="LmpAssertionException"/>
    /// on validation failure, triggering a retry with error feedback.
    /// </param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure (default 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The predicted output (without reasoning).</returns>
    /// <exception cref="LmpMaxRetriesExceededException">
    /// Thrown when all retry attempts are exhausted due to repeated assertion failures.
    /// </exception>
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
            var messages = BuildCoTMessages(input, lastError);

            var response = await _chatClient.GetResponseAsync<ChainOfThoughtResult<TOutput>>(
                messages, Config, cancellationToken: cancellationToken);

            var extended = response.Result
                ?? throw new InvalidOperationException(
                    $"ChainOfThought '{Name}': structured output returned null.");

            var result = extended.Result
                ?? throw new InvalidOperationException(
                    $"ChainOfThought '{Name}': structured output Result field was null.");

            // Record the full extended result (with Reasoning) in trace
            trace?.Record(Name, input!, extended);

            if (validate is null)
                return result;

            try
            {
                validate(result);
                return result;
            }
            catch (LmpAssertionException ex)
            {
                lastError = ex.Message;
            }
        }

        throw new LmpMaxRetriesExceededException(Name, maxRetries);
    }

    /// <summary>
    /// Builds prompt messages with chain-of-thought instruction prepended.
    /// </summary>
    private IList<ChatMessage> BuildCoTMessages(TInput input, string? lastError)
    {
        var messages = BuildMessages(input, lastError);

        // Prepend CoT instruction to the system message if present,
        // or add a new system message if none exists
        var cotInstruction = "Let's think step by step.";

        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            var existingText = messages[0].Text ?? "";
            messages[0] = new ChatMessage(ChatRole.System,
                existingText + "\n\n" + cotInstruction);
        }
        else
        {
            messages.Insert(0, new ChatMessage(ChatRole.System, cotInstruction));
        }

        return messages;
    }
}
