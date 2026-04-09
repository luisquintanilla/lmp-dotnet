namespace LMP;

/// <summary>
/// Soft assertion — returns a boolean indicating success but never throws.
/// Useful for quality guardrails that should not block execution.
/// </summary>
public static class LmpSuggest
{
    /// <summary>
    /// Suggests that the result should satisfy the predicate.
    /// Returns <c>false</c> on failure but never throws.
    /// </summary>
    /// <typeparam name="T">The type of the result to validate.</typeparam>
    /// <param name="result">The result to validate.</param>
    /// <param name="predicate">The validation predicate. Returns <c>true</c> if the result is valid.</param>
    /// <param name="message">Optional message describing the suggestion.</param>
    /// <returns><c>true</c> if the predicate passes; <c>false</c> otherwise.</returns>
    public static bool That<T>(T result, Func<T, bool> predicate, string? message = null)
    {
        return predicate(result);
    }
}
