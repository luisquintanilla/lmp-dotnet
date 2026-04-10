using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Records predictor invocations during a <see cref="LmpModule.ForwardAsync"/> call.
/// Optimizers collect traces from successful examples and use them as few-shot demos.
/// Thread-safe: concurrent predictor calls (e.g., BestOfN) can record simultaneously.
/// </summary>
public sealed class Trace
{
    private readonly List<TraceEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// All recorded trace entries in invocation order.
    /// </summary>
    public IReadOnlyList<TraceEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>
    /// Total token count across all trace entries (sum of each entry's
    /// <see cref="UsageDetails.TotalTokenCount"/>). Returns 0 when no usage data is present.
    /// </summary>
    public long TotalTokens
    {
        get
        {
            lock (_lock)
            {
                long total = 0;
                foreach (var entry in _entries)
                {
                    if (entry.Usage?.TotalTokenCount is { } count)
                        total += count;
                }
                return total;
            }
        }
    }

    /// <summary>
    /// Number of trace entries that represent LM API calls (i.e., entries with non-null
    /// <see cref="TraceEntry.Usage"/>).
    /// </summary>
    public int TotalApiCalls
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var entry in _entries)
                {
                    if (entry.Usage is not null)
                        count++;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Records a predictor invocation with its input, output, and optional usage details.
    /// </summary>
    /// <param name="predictorName">Name of the predictor that was invoked.</param>
    /// <param name="input">The input passed to the predictor.</param>
    /// <param name="output">The output returned by the predictor.</param>
    /// <param name="usage">Optional token usage details from the LM response.</param>
    public void Record(string predictorName, object input, object output, UsageDetails? usage = null)
    {
        lock (_lock)
        {
            _entries.Add(new TraceEntry(predictorName, input, output, usage));
        }
    }
}

/// <summary>
/// A single predictor invocation record: predictor name, input, output, and optional usage.
/// </summary>
/// <param name="PredictorName">Name of the predictor that was invoked.</param>
/// <param name="Input">The input passed to the predictor.</param>
/// <param name="Output">The output returned by the predictor.</param>
/// <param name="Usage">Optional token usage details from the LM response.</param>
public sealed record TraceEntry(
    string PredictorName,
    object Input,
    object Output,
    UsageDetails? Usage = null);
