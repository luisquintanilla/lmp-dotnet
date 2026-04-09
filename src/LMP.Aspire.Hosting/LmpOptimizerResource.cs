using Aspire.Hosting.ApplicationModel;

namespace LMP.Aspire.Hosting;

/// <summary>
/// An Aspire resource representing an LMP optimization run.
/// Shows up in the Aspire dashboard with telemetry for optimization progress,
/// trial scores, and evaluation metrics.
/// </summary>
/// <param name="name">The name of the resource in the Aspire app model.</param>
/// <param name="moduleType">The <see cref="LmpModule"/> type to optimize.</param>
public sealed class LmpOptimizerResource(string name, Type moduleType)
    : Resource(name)
{
    /// <summary>
    /// The concrete <see cref="LmpModule"/> type that this resource optimizes.
    /// </summary>
    public Type ModuleType { get; } = moduleType;

    /// <summary>
    /// Path to the training data JSONL file.
    /// </summary>
    public string? TrainDataPath { get; set; }

    /// <summary>
    /// Path to the development/validation data JSONL file.
    /// </summary>
    public string? DevDataPath { get; set; }

    /// <summary>
    /// The <see cref="IOptimizer"/> type to use. Defaults to <c>null</c> (user must configure).
    /// </summary>
    public Type? OptimizerType { get; set; }

    /// <summary>
    /// Path where the optimized artifact JSON will be written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Maximum number of concurrent evaluations during optimization.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;
}
