namespace LMP;

/// <summary>
/// Retrieval abstraction for RAG pipelines. Implement this to connect
/// your vector store, search index, or any document source.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Retrieves the top-K most relevant passages for the given query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="k">Number of passages to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An array of retrieved passages.</returns>
    Task<string[]> RetrieveAsync(
        string query,
        int k = 5,
        CancellationToken cancellationToken = default);
}
