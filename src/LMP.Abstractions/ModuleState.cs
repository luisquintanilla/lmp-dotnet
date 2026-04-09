using System.Text.Json;

namespace LMP;

/// <summary>
/// Serializable state of a saved LM program.
/// One entry per predictor — instructions, demos, and config.
/// </summary>
public sealed record ModuleState
{
    /// <summary>Schema version. Currently <c>"1.0"</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Name of the <see cref="LmpModule"/> subclass.</summary>
    public required string Module { get; init; }

    /// <summary>Map of predictor name to predictor state.</summary>
    public required Dictionary<string, PredictorState> Predictors { get; init; }
}

/// <summary>
/// Serializable state of a single predictor — instructions, demos, and optional config.
/// </summary>
public sealed record PredictorState
{
    /// <summary>The instruction text for this predictor.</summary>
    public required string Instructions { get; init; }

    /// <summary>Few-shot examples. Each entry has input and output dictionaries.</summary>
    public required List<DemoEntry> Demos { get; init; }

    /// <summary>Predictor-level LM configuration. Null means use defaults.</summary>
    public Dictionary<string, JsonElement>? Config { get; init; }
}

/// <summary>
/// A single demo entry with input and output as JSON element dictionaries.
/// </summary>
public sealed record DemoEntry
{
    /// <summary>Input field values as JSON elements.</summary>
    public required Dictionary<string, JsonElement> Input { get; init; }

    /// <summary>Output field values as JSON elements.</summary>
    public required Dictionary<string, JsonElement> Output { get; init; }
}
