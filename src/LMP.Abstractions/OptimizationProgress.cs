namespace LMP;

/// <summary>
/// Progress report emitted by the optimization pipeline to <see cref="IProgress{T}"/> subscribers.
/// </summary>
/// <param name="OptimizerName">Name of the optimizer step currently running.</param>
/// <param name="TrialNumber">Current trial number within the active optimizer step.</param>
/// <param name="TotalTrials">Total trials expected for the active optimizer step (0 if unknown).</param>
/// <param name="CurrentBestScore">Best score recorded in <see cref="TrialHistory"/> so far.</param>
/// <param name="BaselineScore">Score before any optimization began.</param>
public sealed record OptimizationProgress(
    string OptimizerName,
    int TrialNumber,
    int TotalTrials,
    float CurrentBestScore,
    float BaselineScore);
