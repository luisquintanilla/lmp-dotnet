namespace LMP;

/// <summary>
/// Optimization goal that drives automatic stage-sequence selection in
/// <c>OptimizationPipeline.Auto(module, goal)</c> and <c>Lmp.Optimize.AutoAsync()</c>.
/// </summary>
public enum Goal
{
    /// <summary>
    /// Maximize metric score.
    /// Stage sequence: Z3Feasibility → BootstrapFewShot → GEPA → MIPROv2 → BayesianCalibration
    /// </summary>
    Accuracy,

    /// <summary>
    /// Minimize latency.
    /// Stage sequence: BootstrapFewShot → RouteLLM → MultiFidelity
    /// </summary>
    Speed,

    /// <summary>
    /// Minimize token cost.
    /// Stage sequence: BootstrapFewShot → MIPROv2 (CostAwareSampler) → RouteLLM
    /// </summary>
    Cost,

    /// <summary>
    /// Balance accuracy, latency, and cost (Pareto-optimal).
    /// Stage sequence: Z3Feasibility → BootstrapFewShot → GEPA → RouteLLM (Pareto)
    /// </summary>
    Balanced
}
