using System.Collections.Immutable;

namespace LMP;

/// <summary>
/// Multi-dimensional metric result combining quality score with cost dimensions.
/// Enables multi-objective optimization across accuracy, latency, and token cost.
/// </summary>
/// <param name="Score">Quality score in [0, 1].</param>
/// <param name="Tokens">Total tokens consumed (input + output).</param>
/// <param name="LatencyMs">Wall-clock latency in milliseconds.</param>
/// <param name="Turns">Number of LLM API calls (turns) used.</param>
/// <param name="Custom">Optional named custom dimensions.</param>
public readonly struct MetricVector : IEquatable<MetricVector>
{
    public float Score { get; init; }
    public long Tokens { get; init; }
    public double LatencyMs { get; init; }
    public int Turns { get; init; }
    public ImmutableDictionary<string, float> Custom { get; init; }

    /// <summary>
    /// Constructs a <see cref="MetricVector"/> with all fields.
    /// </summary>
    public MetricVector(
        float score = 0f,
        long tokens = 0,
        double latencyMs = 0,
        int turns = 0,
        ImmutableDictionary<string, float>? custom = null)
    {
        Score = score;
        Tokens = tokens;
        LatencyMs = latencyMs;
        Turns = turns;
        Custom = custom ?? ImmutableDictionary<string, float>.Empty;
    }

    /// <summary>
    /// Returns a <see cref="MetricVector"/> with only the quality score populated.
    /// </summary>
    public static MetricVector FromScore(float score) => new(score);

    /// <summary>
    /// Creates a <see cref="MetricVector"/> populated from a <see cref="Trace"/> and elapsed time.
    /// </summary>
    public static MetricVector FromTrace(float score, Trace trace, double elapsedMs = 0) =>
        new(score, trace.TotalTokens, elapsedMs, trace.TotalApiCalls);

    /// <summary>
    /// Returns <c>true</c> if this vector weakly Pareto-dominates <paramref name="other"/>:
    /// at least as good on all dimensions, strictly better on at least one.
    /// Dimensions compared: Score (higher is better), Tokens/Turns (lower is better).
    /// LatencyMs is excluded from dominance because it is a noisy measurement.
    /// </summary>
    public bool Dominates(MetricVector other)
    {
        // Must be weakly better on all dimensions
        bool scoreOk = Score >= other.Score;
        bool tokensOk = Tokens <= other.Tokens;
        bool turnsOk = Turns <= other.Turns;

        if (!(scoreOk && tokensOk && turnsOk)) return false;

        // Must be strictly better on at least one
        return Score > other.Score || Tokens < other.Tokens || Turns < other.Turns;
    }

    /// <inheritdoc/>
    public bool Equals(MetricVector other)
        => Score == other.Score && Tokens == other.Tokens &&
           LatencyMs == other.LatencyMs && Turns == other.Turns;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MetricVector v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Score, Tokens, LatencyMs, Turns);

    /// <inheritdoc/>
    public override string ToString() =>
        $"MetricVector {{ Score={Score:F3}, Tokens={Tokens}, LatencyMs={LatencyMs:F1}, Turns={Turns} }}";

    public static bool operator ==(MetricVector left, MetricVector right) => left.Equals(right);
    public static bool operator !=(MetricVector left, MetricVector right) => !left.Equals(right);
}
