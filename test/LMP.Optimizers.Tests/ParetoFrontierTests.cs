using LMP.Optimizers;

namespace LMP.Tests;

public sealed class ParetoFrontierTests
{
    // Helper to create ExampleResult with just a score (dummy Example/Output)
    private static ExampleResult Score(float score) =>
        new(new Example<string, string>("input", "label"), "output", score);

    // ── Basic Operations ────────────────────────────────────────

    [Fact]
    public void Empty_Frontier_Count_IsZero()
    {
        var frontier = new ParetoFrontier<TestModule>();
        Assert.Equal(0, frontier.Count);
    }

    [Fact]
    public void Empty_Frontier_Best_Throws()
    {
        var frontier = new ParetoFrontier<TestModule>();
        Assert.Throws<InvalidOperationException>(() => frontier.Best);
    }

    [Fact]
    public void Add_SingleCandidate_CountIsOne()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var module = new TestModule();
        frontier.Add(module, [Score(0.5f), Score(0.7f)]);
        Assert.Equal(1, frontier.Count);
    }

    [Fact]
    public void Best_ReturnsHighestAverage()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var m1 = new TestModule { Tag = "low" };
        var m2 = new TestModule { Tag = "high" };

        frontier.Add(m1, [Score(0.3f), Score(0.4f)]);
        frontier.Add(m2, [Score(0.8f), Score(0.9f)]);

        Assert.Equal("high", ((TestModule)frontier.Best).Tag);
    }

    // ── Per-Instance Best Tracking ─────────────────────────────

    [Fact]
    public void Weak_Candidate_NotOnFrontier()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var strong = new TestModule { Tag = "strong" };
        var weak = new TestModule { Tag = "weak" };

        frontier.Add(strong, [Score(0.9f), Score(0.8f)]);
        frontier.Add(weak, [Score(0.5f), Score(0.4f)]);

        // Weak is not best on any instance → not on frontier
        Assert.Equal(1, frontier.Count);
        Assert.Equal(2, frontier.TotalCandidates);
        Assert.Equal("strong", ((TestModule)frontier.Frontier[0]).Tag);
    }

    [Fact]
    public void New_Better_Candidate_EvictsOld()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var weak = new TestModule { Tag = "weak" };
        var strong = new TestModule { Tag = "strong" };

        frontier.Add(weak, [Score(0.3f), Score(0.4f)]);
        Assert.Equal(1, frontier.Count);

        frontier.Add(strong, [Score(0.9f), Score(0.8f)]);
        // Strong is best on all instances → weak is no longer best on any
        Assert.Equal(1, frontier.Count);
        Assert.Equal("strong", ((TestModule)frontier.Frontier[0]).Tag);
    }

    [Fact]
    public void PerInstanceBest_BothSurvive()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var m1 = new TestModule { Tag = "good-at-0" };
        var m2 = new TestModule { Tag = "good-at-1" };

        // m1 is best on instance 0, m2 is best on instance 1
        frontier.Add(m1, [Score(0.9f), Score(0.3f)]);
        frontier.Add(m2, [Score(0.3f), Score(0.9f)]);

        Assert.Equal(2, frontier.Count);
    }

    [Fact]
    public void EqualScores_BothOnFrontier()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var m1 = new TestModule { Tag = "a" };
        var m2 = new TestModule { Tag = "b" };

        frontier.Add(m1, [Score(0.5f), Score(0.5f)]);
        frontier.Add(m2, [Score(0.5f), Score(0.5f)]);

        // Tied candidates are both in per-instance best sets
        Assert.Equal(2, frontier.Count);
    }

    // ── Pareto Front Score ──────────────────────────────────────

    [Fact]
    public void ParetoFrontScore_IsPerInstanceMaxAverage()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var m1 = new TestModule { Tag = "a" };
        var m2 = new TestModule { Tag = "b" };

        // m1: [0.9, 0.3], m2: [0.3, 0.9]
        // Per-instance max: [0.9, 0.9] → average = 0.9
        frontier.Add(m1, [Score(0.9f), Score(0.3f)]);
        frontier.Add(m2, [Score(0.3f), Score(0.9f)]);

        Assert.Equal(0.9f, frontier.ParetoFrontScore, 0.01f);
    }

    // ── Parent Selection ────────────────────────────────────────

    [Fact]
    public void SelectParents_TooFew_Throws()
    {
        var frontier = new ParetoFrontier<TestModule>();
        frontier.Add(new TestModule(), [Score(0.5f)]);

        Assert.Throws<InvalidOperationException>(() => frontier.SelectParents(new Random(42)));
    }

    [Fact]
    public void SelectParents_ReturnsTwoDifferentCandidates()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var m1 = new TestModule { Tag = "a" };
        var m2 = new TestModule { Tag = "b" };

        frontier.Add(m1, [Score(0.9f), Score(0.3f)]);
        frontier.Add(m2, [Score(0.3f), Score(0.9f)]);

        var (p1, _, p2, _) = frontier.SelectParents(new Random(42));
        Assert.NotSame(p1, p2);
    }

    // ── Multi-candidate Frontier ────────────────────────────────

    [Fact]
    public void ThreeCandidates_WeakNotOnFrontier()
    {
        var frontier = new ParetoFrontier<TestModule>();
        var good0 = new TestModule { Tag = "good0" };
        var good1 = new TestModule { Tag = "good1" };
        var weak = new TestModule { Tag = "weak" };

        frontier.Add(good0, [Score(0.9f), Score(0.3f)]);
        frontier.Add(good1, [Score(0.3f), Score(0.9f)]);
        frontier.Add(weak, [Score(0.2f), Score(0.2f)]);

        // Weak is not best on any instance → frontier has 2, total has 3
        Assert.Equal(2, frontier.Count);
        Assert.Equal(3, frontier.TotalCandidates);
    }
}

// Minimal test module for ParetoFrontier tests
file class TestModule : LmpModule
{
    public string Tag { get; set; } = "";

    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult(input);
}
