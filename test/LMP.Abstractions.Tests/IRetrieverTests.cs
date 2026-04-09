namespace LMP.Tests;

public class IRetrieverTests
{
    private sealed class StubRetriever : IRetriever
    {
        public Task<string[]> RetrieveAsync(
            string query, int k = 5, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new[] { $"Result for: {query}" });
        }
    }

    [Fact]
    public async Task StubImplementsInterface()
    {
        IRetriever retriever = new StubRetriever();

        var results = await retriever.RetrieveAsync("test query");

        Assert.Single(results);
        Assert.Equal("Result for: test query", results[0]);
    }

    [Fact]
    public async Task StubRespectsDefaultK()
    {
        IRetriever retriever = new StubRetriever();

        var results = await retriever.RetrieveAsync("test", k: 3);

        Assert.NotNull(results);
    }
}
