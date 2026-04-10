using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// ReAct agent: interleaves reasoning (Think) with tool calls (Act)
/// and observation of results (Observe) until the final answer.
/// Uses M.E.AI's <see cref="AIFunction"/> and <see cref="FunctionInvokingChatClient"/>
/// — zero new abstractions.
/// </summary>
/// <typeparam name="TInput">The input type for the agent.</typeparam>
/// <typeparam name="TOutput">The output type for the agent. Must be a reference type.</typeparam>
/// <remarks>
/// The agent wraps the provided <see cref="IChatClient"/> with
/// <see cref="FunctionInvokingChatClient"/> middleware for automatic tool dispatch.
/// When <see cref="PredictAsync"/> is called, the available tools are passed via
/// <see cref="ChatOptions.Tools"/>. The middleware handles the Think → Act → Observe
/// loop internally — each call to <c>GetResponseAsync</c> may trigger multiple tool
/// invocations before the LM produces a final structured response.
/// </remarks>
public class ReActAgent<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly IChatClient _wrappedClient;
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly int _maxSteps;

    /// <summary>
    /// Creates a ReAct agent that uses tools to reason about and answer queries.
    /// The provided <paramref name="client"/> is wrapped with
    /// <see cref="FunctionInvokingChatClient"/> for automatic tool dispatch.
    /// </summary>
    /// <param name="client">The underlying chat client.</param>
    /// <param name="tools">Available tools as <see cref="AIFunction"/> instances.</param>
    /// <param name="maxSteps">Maximum Think→Act→Observe iterations (default: 5).</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/> or <paramref name="tools"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxSteps"/> is less than 1.
    /// </exception>
    public ReActAgent(IChatClient client, IEnumerable<AIFunction> tools, int maxSteps = 5)
        : base(client)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

        _tools = tools.ToList();
        _maxSteps = maxSteps;

        _wrappedClient = new FunctionInvokingChatClient(client)
        {
            MaximumIterationsPerRequest = maxSteps
        };
    }

    /// <summary>
    /// Gets the maximum number of Think→Act→Observe iterations this agent will perform.
    /// </summary>
    public int MaxSteps => _maxSteps;

    /// <summary>
    /// Gets the tools available to this agent.
    /// </summary>
    public IReadOnlyList<AIFunction> Tools => _tools;

    /// <summary>
    /// Executes the ReAct loop: builds a prompt from instructions and input,
    /// passes available tools via <see cref="ChatOptions.Tools"/>, and delegates
    /// to <see cref="FunctionInvokingChatClient"/> for Think → Act → Observe
    /// execution. Returns the structured output once the LM produces a final answer.
    /// </summary>
    /// <param name="input">The input to reason about.</param>
    /// <param name="trace">Optional trace for recording the final (input, output) pair.</param>
    /// <param name="validate">
    /// Optional validation delegate. Called after the agent produces a final answer.
    /// Should use <see cref="LmpAssert.That{T}"/> to throw <see cref="LmpAssertionException"/>
    /// on validation failure, triggering a retry with error feedback.
    /// </param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure (default 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent's final structured output after tool-augmented reasoning.</returns>
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
            var messages = BuildAgentMessages(input, lastError);

            var options = new ChatOptions
            {
                Tools = [.. _tools],
                Temperature = Config.Temperature,
                MaxOutputTokens = Config.MaxOutputTokens,
                TopP = Config.TopP,
                FrequencyPenalty = Config.FrequencyPenalty,
                PresencePenalty = Config.PresencePenalty,
                ModelId = Config.ModelId,
            };

            var response = await _wrappedClient.GetResponseAsync<TOutput>(
                messages, options, cancellationToken: cancellationToken);

            var result = response.Result
                ?? throw new InvalidOperationException(
                    $"ReActAgent '{Name}': structured output returned null.");

            trace?.Record(Name, input!, result, response.Usage);

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
    /// Builds prompt messages with ReAct instruction appended to the system message.
    /// </summary>
    private IList<ChatMessage> BuildAgentMessages(TInput input, string? lastError)
    {
        var messages = BuildMessages(input, lastError);

        var reactInstruction =
            "You have access to tools. Use them when needed to gather information before producing your final answer.";

        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            var existingText = messages[0].Text ?? "";
            messages[0] = new ChatMessage(ChatRole.System,
                existingText + "\n\n" + reactInstruction);
        }
        else
        {
            messages.Insert(0, new ChatMessage(ChatRole.System, reactInstruction));
        }

        return messages;
    }
}
