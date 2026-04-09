namespace LMP.Optimizers;

/// <summary>
/// Aggregate result of evaluating a module against a dataset.
/// </summary>
/// <param name="PerExample">Individual results for each example.</param>
/// <param name="AverageScore">Mean score across all examples.</param>
/// <param name="MinScore">Lowest score in the dataset.</param>
/// <param name="MaxScore">Highest score in the dataset.</param>
/// <param name="Count">Total number of examples evaluated.</param>
public sealed record EvaluationResult(
    IReadOnlyList<ExampleResult> PerExample,
    float AverageScore,
    float MinScore,
    float MaxScore,
    int Count);

/// <summary>
/// Result of evaluating a single example: the example, module output, and metric score.
/// </summary>
/// <param name="Example">The example that was evaluated.</param>
/// <param name="Output">The output produced by the module.</param>
/// <param name="Score">The metric score for this example.</param>
public sealed record ExampleResult(
    Example Example,
    object Output,
    float Score);
