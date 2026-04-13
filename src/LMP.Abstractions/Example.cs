using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP;

/// <summary>
/// Non-generic base class for training/validation examples.
/// Optimizers and evaluators work with this type to remain agnostic of
/// concrete TInput/TLabel types.
/// </summary>
public abstract record Example
{
    /// <summary>
    /// Default <see cref="JsonSerializerOptions"/> used by <see cref="LoadFromJsonl{TInput,TLabel}(string,JsonSerializerOptions?)"/>
    /// when no options are provided. Uses case-insensitive property matching and string enum conversion.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "LoadFromJsonl callers can pass source-gen options. Default includes enum converter for convenience.")]
    private static readonly JsonSerializerOptions s_defaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Returns the input portion of this example as an untyped object.
    /// Used by optimizers to feed inputs into <see cref="LmpModule.ForwardAsync"/>.
    /// </summary>
    public abstract object WithInputs();

    /// <summary>
    /// Returns the label (ground truth) portion of this example as an untyped object.
    /// Used by metric functions to compare against module output.
    /// </summary>
    public abstract object GetLabel();

    /// <summary>
    /// Loads typed examples from a JSONL (JSON Lines) file.
    /// Each line must be a JSON object with <c>"input"</c> and <c>"label"</c> properties.
    /// </summary>
    /// <typeparam name="TInput">The type to deserialize each line's <c>"input"</c> into.</typeparam>
    /// <typeparam name="TLabel">The type to deserialize each line's <c>"label"</c> into.</typeparam>
    /// <param name="path">Path to the JSONL file.</param>
    /// <param name="options">
    /// Optional <see cref="JsonSerializerOptions"/> for deserializing the inner <c>"input"</c> and <c>"label"</c> objects.
    /// When <c>null</c>, uses case-insensitive property matching.
    /// Pass options with a source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for AOT-safe deserialization.
    /// </param>
    /// <returns>A read-only list of typed examples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
    /// <exception cref="FileNotFoundException">The file at <paramref name="path"/> does not exist.</exception>
    /// <exception cref="FormatException">A line is not a valid JSON object, or is missing required properties.</exception>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Callers can pass a source-gen JsonSerializerContext via options for AOT-safe deserialization.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Callers can pass a source-gen JsonSerializerContext via options for AOT-safe deserialization.")]
    public static IReadOnlyList<Example<TInput, TLabel>> LoadFromJsonl<TInput, TLabel>(
        string path,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"JSONL file not found: {path}", path);
        }

        var jsonOptions = options ?? s_defaultJsonOptions;
        var examples = new List<Example<TInput, TLabel>>();
        int lineNumber = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException(
                    $"Line {lineNumber}: Expected a JSON object but found {root.ValueKind}.");
            }

            var inputElement = GetRequiredProperty(root, "input", lineNumber);
            var labelElement = GetRequiredProperty(root, "label", lineNumber);

            var input = inputElement.Deserialize<TInput>(jsonOptions)
                ?? throw new FormatException(
                    $"Line {lineNumber}: 'input' deserialized to null.");
            var label = labelElement.Deserialize<TLabel>(jsonOptions)
                ?? throw new FormatException(
                    $"Line {lineNumber}: 'label' deserialized to null.");

            examples.Add(new Example<TInput, TLabel>(input, label));
        }

        return examples;
    }

    /// <summary>
    /// Gets a required property from a <see cref="JsonElement"/>, checking both camelCase and PascalCase.
    /// </summary>
    private static JsonElement GetRequiredProperty(JsonElement root, string camelCaseName, int lineNumber)
    {
        if (root.TryGetProperty(camelCaseName, out var element))
            return element;

        var pascalCaseName = char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];
        if (root.TryGetProperty(pascalCaseName, out element))
            return element;

        throw new FormatException(
            $"Line {lineNumber}: Missing required property '{camelCaseName}'.");
    }
}

/// <summary>
/// A single training/validation example pairing an input with its expected label.
/// </summary>
/// <typeparam name="TInput">The module's input type.</typeparam>
/// <typeparam name="TLabel">The expected output type (ground truth).</typeparam>
/// <param name="Input">The input data for the example.</param>
/// <param name="Label">The expected output (ground truth) for the example.</param>
public sealed record Example<TInput, TLabel>(TInput Input, TLabel Label) : Example
{
    /// <inheritdoc/>
    public override object WithInputs() => Input!;

    /// <inheritdoc/>
    public override object GetLabel() => Label!;
}
