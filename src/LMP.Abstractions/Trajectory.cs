namespace LMP;

/// <summary>
/// An ordered sequence of <see cref="Turn"/> steps representing a complete multi-turn execution.
/// </summary>
/// <remarks>
/// For single-turn modules this typically contains a single turn.
/// For agent loops (e.g., ReAct Think→Act→Observe) each cycle produces one or more turns.
/// The turn's index is its position in <see cref="Turns"/> (0-based).
/// </remarks>
public sealed class Trajectory
{
    private readonly Turn[] _turns;

    /// <summary>
    /// Initializes a <see cref="Trajectory"/> from an ordered list of turns.
    /// </summary>
    /// <param name="turns">Ordered turns. Cannot be null; empty is allowed.</param>
    /// <param name="source">Optional dataset example that triggered this trajectory.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="turns"/> is null.</exception>
    public Trajectory(IReadOnlyList<Turn> turns, Example? source = null)
    {
        ArgumentNullException.ThrowIfNull(turns);
        _turns = turns.ToArray(); // defensive copy; callers cannot mutate
        Source = source;
    }

    /// <summary>All turns in execution order. The turn's index equals its position in this list.</summary>
    public IReadOnlyList<Turn> Turns => _turns;

    /// <summary>Optional dataset example that triggered this trajectory.</summary>
    public Example? Source { get; }

    /// <summary>Number of turns in the trajectory.</summary>
    public int TurnCount => _turns.Length;

    /// <summary>
    /// Sum of all per-step rewards. Turns with a <c>null</c> reward contribute zero.
    /// Returns 0 for empty trajectories.
    /// </summary>
    public float TotalReward
    {
        get
        {
            float total = 0f;
            foreach (var t in _turns)
                if (t.Reward.HasValue) total += t.Reward.Value;
            return total;
        }
    }

    /// <summary>
    /// Average of all non-null per-step rewards. Only turns with a non-null reward
    /// contribute to the denominator. Returns 0 when no turns have rewards.
    /// </summary>
    public float AverageReward
    {
        get
        {
            int count = 0;
            float total = 0f;
            foreach (var t in _turns)
            {
                if (t.Reward.HasValue)
                {
                    total += t.Reward.Value;
                    count++;
                }
            }
            return count == 0 ? 0f : total / count;
        }
    }

    /// <summary>
    /// The last turn in the trajectory, or <c>null</c> when the trajectory is empty.
    /// </summary>
    public Turn? LastTurn => _turns.Length == 0 ? null : _turns[_turns.Length - 1];

    /// <summary>A trajectory with no turns.</summary>
    public static Trajectory Empty { get; } = new([]);

    /// <summary>
    /// Creates a <see cref="Trajectory"/> from a <see cref="Trace"/>,
    /// mapping each <see cref="TraceEntry"/> to a <see cref="Turn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a lossy compatibility adapter.</b>
    /// A <see cref="TraceEntry"/> represents a predictor invocation — not a full
    /// conversation turn. For example, <c>ReActAgent</c> records only its final input/output
    /// pair, so the resulting trajectory will be a single-turn view of the agent's execution.
    /// Use this bridge when detailed per-turn data is unavailable.
    /// </para>
    /// <para>
    /// All turns produced by this method have <see cref="TurnKind.Message"/> kind
    /// and use <see cref="TraceEntry.PredictorName"/> as <see cref="Turn.Attribution"/>.
    /// </para>
    /// </remarks>
    /// <param name="trace">Source trace. Cannot be null.</param>
    /// <param name="source">Optional dataset example that triggered the trace.</param>
    public static Trajectory FromTrace(Trace trace, Example? source = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var entries = trace.Entries;
        var turns = new Turn[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            turns[i] = new Turn(
                Kind: TurnKind.Message,
                Input: e.Input,
                Output: e.Output,
                Usage: e.Usage,
                Attribution: e.PredictorName);
        }
        return new Trajectory(turns, source);
    }
}
