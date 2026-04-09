using System.Collections;
using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// A typed LM call: takes <typeparamref name="TInput"/>, returns <typeparamref name="TOutput"/>
/// via structured output. Contains learnable parameters (demos, instructions) that optimizers tune.
/// </summary>
/// <typeparam name="TInput">The input type for the predictor.</typeparam>
/// <typeparam name="TOutput">The output type for the predictor. Must be a reference type.</typeparam>
public class Predictor<TInput, TOutput> : IPredictor
    where TOutput : class
{
    private readonly IChatClient _client;

    /// <summary>
    /// Creates a predictor bound to the given chat client.
    /// </summary>
    /// <param name="client">The chat client to use for LM calls.</param>
    public Predictor(IChatClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Name = $"{typeof(TInput).Name}\u2192{typeof(TOutput).Name}";
    }

    /// <inheritdoc />
    public string Name { get; set; }

    /// <inheritdoc />
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Few-shot demonstration examples. Filled by optimizers.
    /// Each demo is an (input, output) pair included in the prompt.
    /// </summary>
    public List<(TInput Input, TOutput Output)> Demos { get; set; } = [];

    /// <inheritdoc />
    IList IPredictor.Demos => Demos;

    /// <inheritdoc />
    public ChatOptions Config { get; set; } = new();

    /// <summary>
    /// Executes a single LM call: builds prompt from instructions + demos + input,
    /// calls <see cref="IChatClient.GetResponseAsync{T}"/>, returns typed output.
    /// </summary>
    /// <param name="input">The input to predict from.</param>
    /// <param name="trace">Optional trace for recording the invocation.</param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The predicted output.</returns>
    /// <exception cref="NotImplementedException">
    /// Thrown until the source generator and prompt builder are wired in Phase 2.
    /// </exception>
    public virtual Task<TOutput> PredictAsync(
        TInput input,
        Trace? trace = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "PredictAsync will be wired in Phase 2 when the source generator emits PromptBuilder.");
    }

    /// <inheritdoc />
    public PredictorState GetState()
    {
        return new PredictorState
        {
            Instructions = Instructions,
            Demos = [],
            Config = null
        };
    }

    /// <inheritdoc />
    public void LoadState(PredictorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Instructions = state.Instructions;
    }
}
