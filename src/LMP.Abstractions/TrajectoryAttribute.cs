namespace LMP;

/// <summary>
/// Marks an <see cref="LmpModule"/> subclass as producing a multi-turn <see cref="Trajectory"/>
/// for optimization and evaluation purposes.
/// </summary>
/// <remarks>
/// This attribute is a stub. Source-gen support — per-turn typed schema and
/// <see cref="ITrajectoryMetric"/> hooks — is deferred to a future phase.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TrajectoryAttribute : Attribute;
