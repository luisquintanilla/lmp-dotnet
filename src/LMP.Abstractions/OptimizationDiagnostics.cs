namespace LMP;

/// <summary>
/// Typed container for diagnostic state shared across optimizer pipeline steps.
/// Replaces the untyped <c>OptimizationContext.Bag</c> with a narrower contract:
/// well-known fields are exposed as typed properties; opaque inter-step payloads
/// (e.g., posterior parameters published by one optimizer for another to consume)
/// flow through <see cref="Snapshots"/>.
/// </summary>
/// <remarks>
/// Optimizers should prefer typed properties when adding new diagnostic state.
/// <see cref="Snapshots"/> exists for cross-step communication that cannot yet
/// be modelled as a first-class concept (e.g., Z3 feasibility sets, contextual
/// bandit posteriors keyed by parameter name).
/// </remarks>
public sealed class OptimizationDiagnostics
{
    /// <summary>
    /// Pre-optimization baseline score recorded by the pipeline before the first step
    /// runs, so downstream steps can avoid re-evaluating it. <see langword="null"/>
    /// when no baseline has been recorded yet.
    /// </summary>
    public float? BaselineScore { get; set; }

    /// <summary>
    /// Opaque, namespaced payloads published by optimizer steps for downstream
    /// consumption. Keys are namespaced by convention (e.g., <c>"lmp.bandit:tools:best"</c>,
    /// <c>"lmp.z3:feasible:tools"</c>). Values are owned by the publisher.
    /// </summary>
    public IDictionary<string, object> Snapshots { get; } =
        new Dictionary<string, object>(StringComparer.Ordinal);
}
