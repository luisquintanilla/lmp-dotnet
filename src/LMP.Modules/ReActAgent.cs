using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// ReAct agent: interleaves reasoning (Think) with tool calls (Act)
/// and observation of results (Observe) until the final answer.
/// Uses M.E.AI's <see cref="AIFunction"/> and <see cref="FunctionInvokingChatClient"/> —
/// zero new abstractions.
/// </summary>
/// <typeparam name="TInput">The input type for the agent.</typeparam>
/// <typeparam name="TOutput">The output type for the agent. Must be a reference type.</typeparam>
/// <remarks>
/// The agent wraps the provided <see cref="IChatClient"/> with
/// <see cref="ChatClientBuilderExtensions.UseFunctionInvocation"/> for automatic
/// tool dispatch. The Think → Act → Observe loop is handled internally by
/// <see cref="FunctionInvokingChatClient"/>. Each call to
/// <see cref="PredictAsync"/> may trigger multiple tool invocations before the LM
/// produces a final structured response.
/// </remarks>
public class ReActAgent<TInput, TOutput> : Predictor<TInput, TOutput>
    where TOutput : class
{
    private readonly IChatClient _wrappedClient;
    private readonly IList<AIFunction> _tools;
    private readonly int _maxSteps;

    /// <summary>
    /// Creates a ReAct agent that uses tools to reason about and answer queries.
    /// </summary>
    /// <param name="client">
    /// The base chat client. Will be wrapped with
    /// <see cref="ChatClientBuilderExtensions.UseFunctionInvocation"/> for automatic tool dispatch.
    /// </param>
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

        _tools = tools as IList<AIFunction> ?? tools.ToList();
        _maxSteps = maxSteps;

        // Wrap with FunctionInvokingChatClient for automatic tool dispatch
        _wrappedClient = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    /// Gets the maximum number of Think→Act→Observe iterations this agent will perform.
    /// </summary>
    public int MaxSteps => _maxSteps;

    /// <summary>
    /// Gets the tools available to this agent.
    /// </summary>
    public IReadOnlyList<AIFunction> Tools => _tools.AsReadOnly();

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
            var messages = BuildMessages(input, lastError);

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

            trace?.Record(Name, input!, result);

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
    /// Creates an independent copy of this agent with the same client binding
    /// and tools but separate learnable state (Demos, Instructions, Config).
    /// </summary>
    /// <returns>A new <see cref="IPredictor"/> with independent learnable state.</returns>
    public override IPredictor Clone()
    {
        var clone = (ReActAgent<TInput, TOutput>)base.Clone();
        return clone;
    }
}
