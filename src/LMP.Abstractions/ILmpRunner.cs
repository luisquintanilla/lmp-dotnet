namespace LMP;

/// <summary>
/// Entry point for CLI-driven optimization and evaluation.
/// Implement this interface in your project and the <c>dotnet lmp</c>
/// tool will discover it automatically via reflection.
/// </summary>
/// <remarks>
/// The CLI tool builds your project, scans the output assembly for an
/// <see cref="ILmpRunner"/> implementation, and uses it to create modules,
/// load datasets, and define metrics without requiring the CLI to know
/// your concrete types.
/// </remarks>
public interface ILmpRunner
{
    /// <summary>
    /// Creates the module instance, fully configured with all dependencies
    /// (IChatClient, tools, etc.). Called once per CLI invocation.
    /// </summary>
    /// <returns>A fully configured <see cref="LmpModule"/> instance.</returns>
    LmpModule CreateModule();

    /// <summary>
    /// Creates the metric function used to score module outputs.
    /// Returns a function mapping (example, output) → score in [0, 1].
    /// </summary>
    /// <returns>A metric function compatible with <see cref="IOptimizer.CompileAsync{TModule}"/>.</returns>
    Func<Example, object, float> CreateMetric();

    /// <summary>
    /// Loads a dataset (training or evaluation) from a JSONL file path.
    /// The implementation knows the concrete <c>TInput</c>/<c>TLabel</c> types
    /// to deserialize via <see cref="Example.LoadFromJsonl{TInput,TLabel}"/>.
    /// </summary>
    /// <param name="path">Path to the JSONL file.</param>
    /// <returns>A list of examples loaded from the file.</returns>
    IReadOnlyList<Example> LoadDataset(string path);

    /// <summary>
    /// Deserializes a JSON string into the module's input type.
    /// Used by the <c>dotnet lmp run</c> command to load a single input
    /// from a JSON file and pass it to <see cref="LmpModule.ForwardAsync"/>.
    /// </summary>
    /// <param name="json">The raw JSON string representing the input object.</param>
    /// <returns>The deserialized input object.</returns>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the JSON is invalid.</exception>
    object DeserializeInput(string json) =>
        throw new NotSupportedException(
            "This ILmpRunner does not support DeserializeInput. " +
            "Implement this method to use the 'dotnet lmp run' command.");
}
