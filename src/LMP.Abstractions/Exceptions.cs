namespace LMP;

/// <summary>
/// Thrown when an <see cref="LmpAssert.That{T}"/> predicate fails.
/// The message is fed back into the retry prompt to help the LM self-correct.
/// </summary>
public class LmpAssertionException : Exception
{
    /// <summary>
    /// Creates a new assertion exception with the given message and failed result.
    /// </summary>
    /// <param name="message">Description of the assertion failure.</param>
    /// <param name="failedResult">The result that failed validation.</param>
    public LmpAssertionException(string message, object? failedResult)
        : base(message)
    {
        FailedResult = failedResult;
    }

    /// <summary>
    /// The result value that failed the assertion predicate.
    /// </summary>
    public object? FailedResult { get; }
}

/// <summary>
/// Thrown when a predictor exhausts its retry budget after repeated
/// <see cref="LmpAssertionException"/> failures.
/// </summary>
public class LmpMaxRetriesExceededException : Exception
{
    /// <summary>
    /// Creates a new max-retries exception for the given predictor.
    /// </summary>
    /// <param name="predictorName">Name of the predictor that exhausted retries.</param>
    /// <param name="maxRetries">The retry limit that was exceeded.</param>
    public LmpMaxRetriesExceededException(string predictorName, int maxRetries)
        : base($"Predictor '{predictorName}' exceeded {maxRetries} retries.")
    {
        PredictorName = predictorName;
        MaxRetries = maxRetries;
    }

    /// <summary>
    /// Name of the predictor that exhausted its retry budget.
    /// </summary>
    public string PredictorName { get; }

    /// <summary>
    /// The maximum number of retries that was exceeded.
    /// </summary>
    public int MaxRetries { get; }
}
