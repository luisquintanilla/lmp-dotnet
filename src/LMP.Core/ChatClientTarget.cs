using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Adapts an <see cref="IChatClient"/> as an <see cref="IOptimizationTarget"/>.
/// Exposes the system prompt (<see cref="StringValued"/>), temperature (<see cref="Continuous"/>),
/// and tool selection (<see cref="Subset"/>) as optimizable parameters.
/// </summary>
/// <remarks>
/// <para>
/// Construction is internal — use the
/// <see cref="ChatClientOptimizationExtensions.AsOptimizationTarget(IChatClient, Action{ChatClientTargetBuilder}?)"/>
/// extension on <see cref="IChatClient"/>:
/// <code>
/// var target = chatClient.AsOptimizationTarget(b =&gt; b
///     .WithSystemPrompt("Answer concisely.")
///     .WithTools([searchTool, calcTool]));
/// </code>
/// </para>
/// <para>
/// After optimization, retrieve the optimized state with <see cref="GetState"/>
/// and deploy it using <see cref="ChatClientOptimizationExtensions.UseOptimized(ChatClientBuilder, ChatClientState)"/>.
/// </para>
/// </remarks>
public sealed class ChatClientTarget : IOptimizationTarget
{
    private readonly IChatClient _client;
    private readonly IReadOnlyList<AITool> _allTools;
    private ChatClientState _state;

    private ChatClientTarget(IChatClient client, IReadOnlyList<AITool> allTools, ChatClientState state)
    {
        _client = client;
        _allTools = allTools;
        _state = state;
    }

    /// <summary>
    /// Internal factory invoked by <see cref="ChatClientOptimizationExtensions.AsOptimizationTarget(IChatClient, Action{ChatClientTargetBuilder}?)"/>.
    /// </summary>
    internal static ChatClientTarget Create(
        IChatClient client,
        string? systemPrompt,
        float? temperature,
        IReadOnlyList<AITool>? tools)
    {
        ArgumentNullException.ThrowIfNull(client);

        var allTools = tools ?? [];

        if (allTools.Count > 0)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in allTools)
                if (!names.Add(t.Name))
                    throw new ArgumentException($"Duplicate tool name '{t.Name}' in tools pool.", nameof(tools));
        }

        var state = new ChatClientState
        {
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            SelectedToolNames = allTools.Count > 0
                ? allTools.Select(t => t.Name).ToList()
                : null
        };

        return new ChatClientTarget(client, allTools, state);
    }

    /// <summary>
    /// The full tool pool this target was created with.
    /// Used by deployment extensions to reconstruct the selected tool subset at call time.
    /// </summary>
    public IReadOnlyList<AITool> AllTools => _allTools;

    /// <inheritdoc />
    public TargetShape Shape => TargetShape.SingleTurn;

    /// <inheritdoc />
    public async Task<(object Output, Trace Trace)> ExecuteAsync(
        object input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var snapshot = _state;
        var messages = BuildMessages(input, snapshot.SystemPrompt);
        var options = BuildOptions(snapshot);
        var trace = new Trace();
        var tracingClient = new LmpTraceMiddleware(_client, trace);
        var response = await tracingClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        return (response.Text ?? string.Empty, trace);
    }

    /// <inheritdoc />
    public TypedParameterSpace GetParameterSpace()
    {
        var space = TypedParameterSpace.Empty;

        if (_state.SystemPrompt is not null)
            space = space.Add("system_prompt", new StringValued(_state.SystemPrompt));

        if (_state.Temperature.HasValue)
            space = space.Add("temperature", new Continuous(0.0, 2.0));

        if (_allTools.Count > 0)
            space = space.Add("tools", new Subset([.. _allTools.Cast<object>()], 0, _allTools.Count));

        return space;
    }

    /// <inheritdoc />
    public TargetState GetState() => TargetState.From(_state);

    /// <inheritdoc />
    public void ApplyState(TargetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state.As<ChatClientState>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads <c>"system_prompt"</c> (<see cref="StringValued"/> → <c>string</c>),
    /// <c>"temperature"</c> (<see cref="Continuous"/> → <c>double</c>, converted to <c>float</c>),
    /// and <c>"tools"</c> (<see cref="Subset"/> → <c>IReadOnlyList&lt;object&gt;</c> of <see cref="AITool"/>).
    /// Parameters absent from the assignment keep their current values.
    /// </remarks>
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        var systemPrompt = assignment.TryGet<string>("system_prompt", out var sp)
            ? sp : _state.SystemPrompt;

        float? temperature = _state.Temperature;
        if (assignment.TryGet<double>("temperature", out var td))
            temperature = (float)td;
        else if (assignment.TryGet<float>("temperature", out var tf))
            temperature = tf;

        IReadOnlyList<string>? selectedNames = _state.SelectedToolNames;
        if (assignment.TryGet<IReadOnlyList<object>>("tools", out var toolList))
        {
            // Pool items may be AITool instances (from Subset) or string names from other samplers.
            var fromTools = toolList.OfType<AITool>().Select(t => t.Name).ToList();
            selectedNames = fromTools.Count > 0
                ? fromTools
                : toolList.OfType<string>().ToList();
        }

        var newState = new ChatClientState
        {
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            SelectedToolNames = selectedNames
        };

        return new ChatClientTarget(_client, _allTools, newState);
    }

    /// <inheritdoc />
    public TService? GetService<TService>() where TService : class
        => _client as TService;

    // ── Private helpers ──────────────────────────────────────────────────

    private static List<ChatMessage> BuildMessages(object input, string? systemPrompt)
    {
        var messages = new List<ChatMessage>();

        if (systemPrompt is not null)
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        switch (input)
        {
            case string text:
                messages.Add(new ChatMessage(ChatRole.User, text));
                break;
            case IEnumerable<ChatMessage> chatMessages:
                messages.AddRange(chatMessages);
                break;
            default:
                throw new ArgumentException(
                    $"ChatClientTarget requires string or IEnumerable<ChatMessage> input; got {input.GetType().Name}.",
                    nameof(input));
        }

        return messages;
    }

    private ChatOptions? BuildOptions(ChatClientState snapshot)
    {
        var selectedTools = _allTools.Count > 0 && snapshot.SelectedToolNames is not null
            ? _allTools.Where(t => snapshot.SelectedToolNames.Contains(t.Name)).ToList()
            : [];

        if (snapshot.Temperature is null && selectedTools.Count == 0)
            return null;

        var options = new ChatOptions();

        if (snapshot.Temperature.HasValue)
            options.Temperature = snapshot.Temperature.Value;

        if (selectedTools.Count > 0)
            options.Tools = [.. selectedTools];

        return options;
    }
}
