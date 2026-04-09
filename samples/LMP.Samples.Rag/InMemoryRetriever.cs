namespace LMP.Samples.Rag;

/// <summary>
/// A simple in-memory keyword-based retriever. Scores documents by counting
/// how many distinct query words appear in each document (case-insensitive),
/// then returns the top-K highest-scoring documents.
/// </summary>
public sealed class InMemoryRetriever : IRetriever
{
    private readonly List<string> _documents;

    /// <summary>
    /// Creates a retriever backed by the given document collection.
    /// </summary>
    /// <param name="documents">The documents to search over.</param>
    public InMemoryRetriever(IEnumerable<string> documents)
    {
        _documents = documents.ToList();
    }

    /// <inheritdoc />
    public Task<string[]> RetrieveAsync(
        string query,
        int k = 5,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var queryWords = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        var results = _documents
            .Select(doc => (
                Document: doc,
                Score: queryWords.Count(w =>
                    doc.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(k)
            .Select(x => x.Document)
            .ToArray();

        return Task.FromResult(results);
    }
}
