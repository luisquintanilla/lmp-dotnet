namespace LMP;

/// <summary>
/// Hard assertion with retry/backtrack. If the predicate fails,
/// throws <see cref="LmpAssertionException"/> which triggers the predictor's
/// retry loop with the failure message in context.
/// </summary>
public static class LmpAssert
{
    /// <summary>
    /// Asserts that the result satisfies the predicate.
    /// Throws <see cref="LmpAssertionException"/> on failure, triggering retry.
    /// </summary>
    /// <typeparam name="T">The type of the result to validate.</typeparam>
    /// <param name="result">The result to validate.</param>
    /// <param name="predicate">The validation predicate. Returns <c>true</c> if the result is valid.</param>
    /// <param name="message">Optional message describing the assertion. Included in the retry prompt.</param>
    /// <exception cref="LmpAssertionException">Thrown when the predicate returns <c>false</c>.</exception>
    public static void That<T>(T result, Func<T, bool> predicate, string? message = null)
    {
        if (!predicate(result))
        {
            throw new LmpAssertionException(
                message ?? "LMP assertion failed.",
                result);
        }
    }
}
