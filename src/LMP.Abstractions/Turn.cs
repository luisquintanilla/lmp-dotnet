using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// A single step within a <see cref="Trajectory"/>.
/// Captures the role, optional input and output, an optional per-step reward,
/// token usage, and an attribution label identifying which component produced this step.
/// </summary>
/// <remarks>
/// Index is not stored on the turn itself — the turn's position in
/// <see cref="Trajectory.Turns"/> is the canonical index.
/// </remarks>
/// <param name="Kind">The role this turn plays in the trajectory (default: <see cref="TurnKind.Message"/>).</param>
/// <param name="Input">Optional input at this step (user message, tool result, etc.).</param>
/// <param name="Output">Optional output at this step (model response, tool call request, etc.).</param>
/// <param name="Reward">
/// Optional per-step reward signal. <c>null</c> means no signal was recorded at this step.
/// Conventionally in [0, 1], though values outside that range are allowed for flexibility.
/// </param>
/// <param name="Usage">Optional token usage for the LM call made at this step.</param>
/// <param name="Attribution">
/// Optional label identifying the component that produced this step
/// (e.g., predictor name, tool name, module class name).
/// </param>
public sealed record Turn(
    TurnKind Kind = TurnKind.Message,
    object? Input = null,
    object? Output = null,
    float? Reward = null,
    UsageDetails? Usage = null,
    string? Attribution = null);
