namespace LMP;

/// <summary>
/// Records the result of a single optimization trial.
/// </summary>
/// <param name="Score">The metric score achieved in this trial (in [0, 1]).</param>
/// <param name="Cost">Multi-dimensional cost measurement for this trial.</param>
/// <param name="Notes">Optional human-readable description (e.g., sampler config).</param>
public sealed record Trial(float Score, TrialCost Cost, string? Notes = null);
