namespace LMP;

/// <summary>
/// Multi-dimensional cost measurement for a single optimization trial.
/// Captures token usage, latency, and API call count so that cost-aware
/// samplers (e.g., <c>CostAwareSampler</c>) can balance quality vs. expense.
/// </summary>
/// <param name="TotalTokens">Total token count (input + output) across all LM calls in the trial.</param>
/// <param name="InputTokens">Total input (prompt) tokens across all LM calls in the trial.</param>
/// <param name="OutputTokens">Total output (completion) tokens across all LM calls in the trial.</param>
/// <param name="ElapsedMilliseconds">Wall-clock duration of the trial in milliseconds.</param>
/// <param name="ApiCalls">Number of LM API calls made during the trial.</param>
public sealed record TrialCost(
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long ElapsedMilliseconds,
    int ApiCalls);
