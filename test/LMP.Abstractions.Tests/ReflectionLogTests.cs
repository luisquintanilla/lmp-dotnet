using Xunit;

namespace LMP.Tests;

public class ReflectionLogTests
{
    [Fact]
    public void NewLog_IsEmpty()
    {
        var log = new ReflectionLog();
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Add_AppendsEntry()
    {
        var log = new ReflectionLog();
        log.Add("Good observation", source: "Test");
        Assert.Equal(1, log.Count);
        Assert.Equal("Good observation", log.Entries[0].Text);
        Assert.Equal("Test", log.Entries[0].Source);
    }

    [Fact]
    public void Add_DefaultScope_IsGlobal()
    {
        var log = new ReflectionLog();
        log.Add("observation");
        Assert.Equal(ReflectionScope.Global, log.Entries[0].Scope);
    }

    [Fact]
    public void Add_PredictorScope_SetsNameAndScope()
    {
        var log = new ReflectionLog();
        log.Add("pred observation", predictorPath: "Answer", scope: ReflectionScope.Predictor, score: 0.4f);
        var entry = log.Entries[0];
        Assert.Equal(ReflectionScope.Predictor, entry.Scope);
        Assert.Equal("Answer", entry.PredictorName);
        Assert.Equal(0.4f, entry.Score);
    }

    [Fact]
    public void Entries_ReturnsSnapshot_IsolatedFromFutureMutations()
    {
        var log = new ReflectionLog();
        log.Add("first");
        var snapshot = log.Entries;
        log.Add("second");
        Assert.Single(snapshot);                // snapshot unchanged
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Empty_IsReadOnly_ThrowsOnAdd()
    {
        Assert.Throws<InvalidOperationException>(
            () => ReflectionLog.Empty.Add("should throw"));
    }

    [Fact]
    public void Empty_IsActuallyEmpty()
    {
        Assert.Equal(0, ReflectionLog.Empty.Count);
        Assert.Empty(ReflectionLog.Empty.Entries);
    }

    [Fact]
    public void GetEntries_FiltersByScope()
    {
        var log = new ReflectionLog();
        log.Add("global one", scope: ReflectionScope.Global);
        log.Add("predictor one", predictorPath: "A", scope: ReflectionScope.Predictor);
        log.Add("global two", scope: ReflectionScope.Global);

        var globals = log.GetEntries(ReflectionScope.Global);
        var predictors = log.GetEntries(ReflectionScope.Predictor);

        Assert.Equal(2, globals.Count);
        Assert.Single(predictors);
    }

    [Fact]
    public void GetEntriesForPredictor_FiltersByName()
    {
        var log = new ReflectionLog();
        log.Add("a obs", predictorPath: "A", scope: ReflectionScope.Predictor);
        log.Add("b obs", predictorPath: "B", scope: ReflectionScope.Predictor);
        log.Add("global", scope: ReflectionScope.Global);

        var forA = log.GetEntriesForPredictor("A");
        Assert.Single(forA);
        Assert.Equal("a obs", forA[0].Text);
    }

    [Fact]
    public void GetEntriesForPredictor_NullName_ThrowsArgumentNull()
    {
        var log = new ReflectionLog();
        Assert.Throws<ArgumentNullException>(() => log.GetEntriesForPredictor(null!));
    }

    [Fact]
    public void Add_ThreadSafe_AllEntriesPresent()
    {
        var log = new ReflectionLog();
        var threads = Enumerable.Range(0, 20).Select(i =>
            new Thread(() => log.Add($"entry {i}"))
        ).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(20, log.Count);
    }

    [Fact]
    public void CreatedAt_IsSet()
    {
        var before = DateTimeOffset.UtcNow;
        var log = new ReflectionLog();
        log.Add("timestamped");
        var after = DateTimeOffset.UtcNow;
        var ts = log.Entries[0].CreatedAt;
        Assert.True(ts >= before && ts <= after);
    }
}
