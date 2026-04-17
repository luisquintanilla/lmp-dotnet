namespace LMP;

/// <summary>
/// Multi-dimensional cost budget for an optimization run.
/// Any non-null limit that is exceeded causes the pipeline to stop adding new steps.
/// Dollar amounts can be expressed via <see cref="Custom"/>:
/// <code>Custom(c =&gt; c.InputTokens * 0.01/1000 + c.OutputTokens * 0.06/1000 &gt; maxDollars)</code>
/// </summary>
public sealed record CostBudget
{
    /// <summary>Unlimited budget (all limits null).</summary>
    public static CostBudget Unlimited { get; } = new();

    /// <summary>Maximum total token count across all trials. Maps to <see cref="TrialCost.TotalTokens"/>.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Maximum total API calls across all trials. Maps to <see cref="TrialCost.ApiCalls"/>.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Maximum wall-clock duration. Maps to cumulative <see cref="TrialCost.ElapsedMilliseconds"/>.</summary>
    public TimeSpan? MaxWallClock { get; init; }

    /// <summary>Custom exit condition evaluated per trial. Return <c>true</c> to stop optimization.</summary>
    public Func<TrialCost, bool>? Custom { get; init; }

    /// <summary>
    /// Returns <c>true</c> if the accumulated trial history is within all configured limits.
    /// </summary>
    public bool IsWithinBudget(TrialHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (MaxTokens.HasValue && history.TotalTokens >= MaxTokens.Value)
            return false;
        if (MaxTurns.HasValue && history.TotalApiCalls >= MaxTurns.Value)
            return false;
        return true;
    }

    /// <summary>Fluent builder for <see cref="CostBudget"/>.</summary>
    public sealed class Builder
    {
        private long? _maxTokens;
        private int? _maxTurns;
        private TimeSpan? _maxWallClock;
        private Func<TrialCost, bool>? _custom;

        /// <summary>Sets a maximum token budget.</summary>
        public Builder MaxTokens(long n) { _maxTokens = n; return this; }

        /// <summary>Sets a maximum API-call budget.</summary>
        public Builder MaxTurns(int n) { _maxTurns = n; return this; }

        /// <summary>Sets a maximum wall-clock duration in seconds.</summary>
        public Builder MaxSeconds(double s) { _maxWallClock = TimeSpan.FromSeconds(s); return this; }

        /// <summary>Sets a custom exit predicate.</summary>
        public Builder Custom(Func<TrialCost, bool> predicate) { _custom = predicate; return this; }

        /// <summary>Builds the configured <see cref="CostBudget"/>.</summary>
        public CostBudget Build() => new()
        {
            MaxTokens = _maxTokens,
            MaxTurns = _maxTurns,
            MaxWallClock = _maxWallClock,
            Custom = _custom
        };
    }
}
