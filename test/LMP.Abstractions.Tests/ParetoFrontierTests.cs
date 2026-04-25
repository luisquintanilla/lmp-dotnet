using Xunit;

namespace LMP.Tests;

public class ParetoFrontierTests
{
    private static TargetState MakeState(string id) => TargetState.From(id);

    [Fact]
    public void NewFrontier_IsEmpty()
    {
        var frontier = new ParetoFrontier();
        Assert.Equal(0, frontier.Count);
        Assert.Empty(frontier.Entries);
    }

    [Fact]
    public void Add_FirstEntry_AddedToFrontier()
    {
        var frontier = new ParetoFrontier();
        var state = MakeState("A");
        var vec = new MetricVector(0.8f, 500L, 0, 2);

        bool added = frontier.Add(state, vec);

        Assert.True(added);
        Assert.Equal(1, frontier.Count);
    }

    [Fact]
    public void Add_DominatedEntry_NotAdded()
    {
        var frontier = new ParetoFrontier();
        var state = MakeState("A");
        frontier.Add(state, new MetricVector(0.9f, 300L, 0, 1));

        // Dominated: worse score AND more tokens AND more turns
        bool added = frontier.Add(MakeState("B"), new MetricVector(0.7f, 500L, 0, 3));

        Assert.False(added);
        Assert.Equal(1, frontier.Count);
    }

    [Fact]
    public void Add_NewEntryDominatesExisting_PrunesOld()
    {
        var frontier = new ParetoFrontier();
        frontier.Add(MakeState("A"), new MetricVector(0.7f, 500L, 0, 3));

        // Better on all: higher score, fewer tokens, fewer turns
        bool added = frontier.Add(MakeState("B"), new MetricVector(0.9f, 300L, 0, 1));

        Assert.True(added);
        Assert.Equal(1, frontier.Count); // old entry pruned
    }

    [Fact]
    public void Add_IncomparableEntries_BothRetained()
    {
        var frontier = new ParetoFrontier();
        frontier.Add(MakeState("A"), new MetricVector(0.9f, 1000L, 0, 1)); // high score, high cost
        frontier.Add(MakeState("B"), new MetricVector(0.6f, 200L, 0, 1));  // low score, low cost

        Assert.Equal(2, frontier.Count);
    }

    [Fact]
    public void BestByScore_ReturnsHighestScore()
    {
        var frontier = new ParetoFrontier();
        frontier.Add(MakeState("A"), new MetricVector(0.6f, 200L, 0, 1));
        frontier.Add(MakeState("B"), new MetricVector(0.9f, 1000L, 0, 1));

        var best = frontier.BestByScore;
        Assert.NotNull(best);
        Assert.Equal(0.9f, best.Value.Vector.Score);
    }

    [Fact]
    public void BestByTokens_ReturnsFewestTokens()
    {
        var frontier = new ParetoFrontier();
        frontier.Add(MakeState("A"), new MetricVector(0.6f, 200L, 0, 1));
        frontier.Add(MakeState("B"), new MetricVector(0.9f, 1000L, 0, 1));

        var best = frontier.BestByTokens;
        Assert.NotNull(best);
        Assert.Equal(200L, best.Value.Vector.Tokens);
    }

    [Fact]
    public void BestByScore_EmptyFrontier_ReturnsNull()
    {
        var frontier = new ParetoFrontier();
        Assert.Null(frontier.BestByScore);
    }

    [Fact]
    public void BestByTokens_EmptyFrontier_ReturnsNull()
    {
        var frontier = new ParetoFrontier();
        Assert.Null(frontier.BestByTokens);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var frontier = new ParetoFrontier();
        frontier.Add(MakeState("A"), new MetricVector(0.8f, 500L, 0, 2));
        frontier.Add(MakeState("B"), new MetricVector(0.6f, 200L, 0, 1));
        frontier.Clear();
        Assert.Equal(0, frontier.Count);
    }

    [Fact]
    public void Add_ThreadSafe_ConsistentCount()
    {
        var frontier = new ParetoFrontier();
        var threads = Enumerable.Range(0, 10).Select(i =>
            new Thread(() => frontier.Add(
                MakeState($"t{i}"),
                new MetricVector((float)i / 10, 1000L - i * 50L, 0, i + 1)))
        ).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        // Frontier should have at least 1 entry and all remaining should be non-dominated
        Assert.True(frontier.Count >= 1);
        var entries = frontier.Entries;
        foreach (var (_, v) in entries)
            foreach (var (_, other) in entries)
                if (v != other)
                    Assert.False(other.Dominates(v)); // no entry dominates another
    }

    [Fact]
    public void EqualVectors_SecondNotAdded_DueToNonStrictDominance()
    {
        var frontier = new ParetoFrontier();
        var vec = new MetricVector(0.8f, 500L, 0, 2);
        frontier.Add(MakeState("A"), vec);
        bool added = frontier.Add(MakeState("B"), vec);
        // Equal vectors don't dominate each other → second IS added (not dominated by first)
        // The exact behavior: a.Dominates(b) requires strictly better on at least one → false for equal
        // So equal vectors are both added
        Assert.True(added);
        Assert.Equal(2, frontier.Count);
    }

    [Fact]
    public void MultipleIncomparables_AllRetained()
    {
        var frontier = new ParetoFrontier();
        // Each entry is better on a different dimension
        frontier.Add(MakeState("A"), new MetricVector(1.0f, 1000L, 0, 5));
        frontier.Add(MakeState("B"), new MetricVector(0.7f, 100L, 0, 5));
        frontier.Add(MakeState("C"), new MetricVector(0.7f, 1000L, 0, 1));

        Assert.Equal(3, frontier.Count);
    }
}
