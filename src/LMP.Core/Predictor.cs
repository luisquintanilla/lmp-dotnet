using System.Collections;
using System.ComponentModel;
using System.Text.Json;
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

    /// <summary>
    /// The chat client used for LM calls. Exposed for use by source-generated
    /// interceptors that inline the <see cref="PredictAsync"/> logic.
    /// </summary>
    public IChatClient Client => _client;

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
    /// Optional delegate for building prompt messages. When set (typically by source-generated code),
    /// this is used instead of the default <see cref="BuildMessages"/> implementation.
    /// Signature: (instructions, input, demos, lastError) → messages.
    /// </summary>
    internal Func<string, TInput, IReadOnlyList<(TInput Input, TOutput Output)>?, string?, IList<ChatMessage>>? MessageBuilder { get; set; }

    /// <summary>
    /// Sets the prompt builder delegate for this predictor instance.
    /// This method is called by source-generated interceptor code to wire
    /// type-specific prompt formatting before the first prediction.
    /// </summary>
    /// <param name="builder">The prompt builder delegate.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void SetPromptBuilder(Func<string, TInput, IReadOnlyList<(TInput Input, TOutput Output)>?, string?, IList<ChatMessage>> builder)
    {
        MessageBuilder ??= builder;
    }

    /// <summary>
    /// Executes a single LM call: builds prompt from instructions + demos + input,
    /// calls <c>GetResponseAsync&lt;TOutput&gt;</c>, records trace, and returns typed output.
    /// If <paramref name="validate"/> is provided, retries on <see cref="LmpAssertionException"/>
    /// up to <paramref name="maxRetries"/> times with error feedback in the prompt.
    /// </summary>
    /// <param name="input">The input to predict from.</param>
    /// <param name="trace">Optional trace for recording the invocation.</param>
    /// <param name="validate">
    /// Optional validation delegate. Called after each prediction attempt.
    /// Should use <see cref="LmpAssert.That{T}"/> to throw <see cref="LmpAssertionException"/>
    /// on validation failure, triggering a retry with error feedback.
    /// </param>
    /// <param name="maxRetries">Maximum retry attempts on assertion failure (default 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The predicted output.</returns>
    /// <exception cref="LmpMaxRetriesExceededException">
    /// Thrown when all retry attempts are exhausted due to repeated assertion failures.
    /// </exception>
    public virtual async Task<TOutput> PredictAsync(
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

            var response = await _client.GetResponseAsync<TOutput>(
                messages, Config, cancellationToken: cancellationToken);

            var result = response.Result
                ?? throw new InvalidOperationException(
                    $"Predictor '{Name}': structured output returned null.");

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
    /// Builds the prompt messages for an LM call.
    /// Uses <see cref="MessageBuilder"/> if set (source-generated), otherwise falls back
    /// to a default implementation using <see cref="Instructions"/> and <c>ToString()</c>.
    /// </summary>
    /// <param name="input">The current input.</param>
    /// <param name="lastError">Optional error from a previous assertion failure for retry feedback.</param>
    /// <returns>The list of chat messages to send to the LM.</returns>
    protected virtual IList<ChatMessage> BuildMessages(TInput input, string? lastError)
    {
        if (MessageBuilder is not null)
            return MessageBuilder(Instructions, input, Demos, lastError);

        return BuildDefaultMessages(input, lastError);
    }

    private IList<ChatMessage> BuildDefaultMessages(TInput input, string? lastError)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(Instructions))
            messages.Add(new ChatMessage(ChatRole.System, Instructions));

        // Few-shot demo pairs
        foreach (var (demoInput, demoOutput) in Demos)
        {
            messages.Add(new ChatMessage(ChatRole.User, demoInput?.ToString() ?? ""));
            messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(demoOutput)));
        }

        // Current input with optional retry feedback
        var userContent = input?.ToString() ?? "";
        if (lastError is not null)
            userContent += $"\n\nPrevious attempt failed: {lastError}. Try again.";

        messages.Add(new ChatMessage(ChatRole.User, userContent));

        return messages;
    }

    /// <inheritdoc />
    public PredictorState GetState()
    {
        var demoEntries = new List<DemoEntry>();
        foreach (var (demoInput, demoOutput) in Demos)
        {
            demoEntries.Add(new DemoEntry
            {
                Input = JsonElementFromObject(demoInput),
                Output = JsonElementFromObject(demoOutput)
            });
        }

        return new PredictorState
        {
            Instructions = Instructions,
            Demos = demoEntries,
            Config = null
        };
    }

    /// <inheritdoc />
    public void LoadState(PredictorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Instructions = state.Instructions;

        Demos.Clear();
        foreach (var entry in state.Demos)
        {
            var input = DeserializeFromDictionary<TInput>(entry.Input);
            var output = DeserializeFromDictionary<TOutput>(entry.Output);
            if (input is not null && output is not null)
                Demos.Add((input, output));
        }
    }

    /// <summary>
    /// Deserializes a value from a <see cref="DemoEntry"/> dictionary, handling the
    /// "value" wrapper that <see cref="JsonElementFromObject{T}"/> creates for non-object types.
    /// </summary>
    private static T? DeserializeFromDictionary<T>(Dictionary<string, JsonElement> dict)
    {
        // GetState wraps non-object types (string, int, etc.) as { "value": <element> }.
        // Unwrap single "value" keys back to the original value.
        if (dict.Count == 1 && dict.TryGetValue("value", out var valueElement))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(valueElement.GetRawText());
            }
            catch (JsonException)
            {
                // Fall through: "value" was a real object property, not a wrapper
            }
        }

        var json = JsonSerializer.Serialize(dict);
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <inheritdoc />
    public void AddDemo(object input, object output)
    {
        Demos.Add(((TInput)input, (TOutput)output));
    }

    /// <summary>
    /// Creates an independent copy of this predictor. The clone shares the same
    /// <see cref="IChatClient"/> binding but has its own <see cref="Demos"/> list
    /// and <see cref="Config"/> instance.
    /// </summary>
    /// <returns>A new <see cref="IPredictor"/> with independent learnable state.</returns>
    public virtual IPredictor Clone()
    {
        var clone = (Predictor<TInput, TOutput>)MemberwiseClone();
        clone.Demos = new List<(TInput Input, TOutput Output)>(Demos);
        clone.Config = new ChatOptions
        {
            Temperature = Config.Temperature,
            MaxOutputTokens = Config.MaxOutputTokens,
            TopP = Config.TopP,
            FrequencyPenalty = Config.FrequencyPenalty,
            PresencePenalty = Config.PresencePenalty,
            StopSequences = Config.StopSequences is { Count: > 0 }
                ? [.. Config.StopSequences]
                : null,
            ModelId = Config.ModelId,
        };
        return clone;
    }

    private static Dictionary<string, JsonElement> JsonElementFromObject<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }

        // Non-object types (string, int, etc.) wrapped as { "value": ... }
        return new Dictionary<string, JsonElement>
        {
            ["value"] = doc.RootElement.Clone()
        };
    }
}
