using LMP.Extensions.Z3;
using Xunit;

namespace LMP.Extensions.Z3.Tests;

public sealed class Z3ConstrainedDemoSelectorTests
{
    private readonly Func<object, string> _categoryExtractor =
        input => input is string s ? s.Split(':')[0] : "unknown";

    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_NullExtractor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Z3ConstrainedDemoSelector(categoryExtractor: null!));
    }

    [Fact]
    public void Constructor_ZeroMaxDemos_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 0));
    }

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var selector = new Z3ConstrainedDemoSelector(
            _categoryExtractor, maxDemos: 4, metricThreshold: 0.5f);
        Assert.NotNull(selector);
    }

    // ── SolveConstrainedSelection Tests ─────────────────────────

    [Fact]
    public void Solve_PoolSmallerThanMax_ReturnsAll()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 4);
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:ticket1", "reply1", 0.9f),
            new("technical:ticket2", "reply2", 0.8f),
        };

        var selected = selector.SolveConstrainedSelection(pool);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void Solve_ExactlyMaxDemos_Selected()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 3);
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:t1", "r1", 0.9f),
            new("billing:t2", "r2", 0.8f),
            new("technical:t3", "r3", 0.7f),
            new("technical:t4", "r4", 0.6f),
            new("account:t5", "r5", 0.5f),
        };

        var selected = selector.SolveConstrainedSelection(pool);
        Assert.Equal(3, selected.Count);
    }

    [Fact]
    public void Solve_CategoryCoverage_AllCategoriesRepresented()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 3);
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:t1", "r1", 0.9f),
            new("billing:t2", "r2", 0.95f),
            new("billing:t3", "r3", 0.85f),
            new("technical:t4", "r4", 0.6f),
            new("account:t5", "r5", 0.5f),
        };

        var selected = selector.SolveConstrainedSelection(pool);
        var categories = selected.Select(d => _categoryExtractor(d.Input)).ToHashSet();

        // Must cover all 3 categories even though billing has the highest scores
        Assert.Contains("billing", categories);
        Assert.Contains("technical", categories);
        Assert.Contains("account", categories);
    }

    [Fact]
    public void Solve_MaximizesQuality_PrefersHighScores()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 2);
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:t1", "r1", 0.9f),
            new("billing:t2", "r2", 0.3f),
            new("technical:t3", "r3", 0.8f),
            new("technical:t4", "r4", 0.2f),
        };

        var selected = selector.SolveConstrainedSelection(pool);

        // Should pick the higher-scoring demo from each category
        Assert.Contains(selected, d => d.Score == 0.9f);
        Assert.Contains(selected, d => d.Score == 0.8f);
    }

    [Fact]
    public void Solve_WithTokenCounter_MinimizesTokens()
    {
        Func<object, int> tokenCounter = input =>
        {
            var s = (string)input;
            return s.Length; // simple: use string length as proxy for tokens
        };

        var selector = new Z3ConstrainedDemoSelector(
            _categoryExtractor,
            tokenCounter: tokenCounter,
            maxDemos: 2);

        // All same quality, but different "token" counts (string lengths)
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:short", "r1", 0.8f),
            new("billing:this-is-a-much-longer-input", "r2", 0.8f),
            new("technical:tiny", "r3", 0.8f),
            new("technical:this-is-also-a-very-long-input-text", "r4", 0.8f),
        };

        var selected = selector.SolveConstrainedSelection(pool);

        // Quality is equal, so token minimization should prefer shorter inputs
        var totalLength = selected.Sum(d => ((string)d.Input).Length);
        var shortestPossible = "billing:short".Length + "technical:tiny".Length;
        Assert.Equal(shortestPossible, totalLength);
    }

    [Fact]
    public void Solve_SingleCategory_SelectsTopScoring()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 2);
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>
        {
            new("billing:t1", "r1", 0.9f),
            new("billing:t2", "r2", 0.5f),
            new("billing:t3", "r3", 0.8f),
            new("billing:t4", "r4", 0.3f),
        };

        var selected = selector.SolveConstrainedSelection(pool);
        Assert.Equal(2, selected.Count);
        // Should pick the two highest scoring
        Assert.Contains(selected, d => d.Score == 0.9f);
        Assert.Contains(selected, d => d.Score == 0.8f);
    }

    [Fact]
    public void Solve_LargePool_CompletesInReasonableTime()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor, maxDemos: 5);
        var categories = new[] { "billing", "technical", "account", "security", "general" };
        var pool = new List<Z3ConstrainedDemoSelector.ScoredDemo>();

        var rng = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            var cat = categories[i % categories.Length];
            pool.Add(new($"{cat}:ticket{i}", $"reply{i}", (float)rng.NextDouble()));
        }

        var selected = selector.SolveConstrainedSelection(pool);
        Assert.Equal(5, selected.Count);

        // Must cover all 5 categories (maxDemos=5, 5 categories → exactly one each)
        var coveredCategories = selected.Select(d => _categoryExtractor(d.Input)).Distinct().Count();
        Assert.Equal(5, coveredCategories);
    }

    // ── IOptimizer Contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new Z3ConstrainedDemoSelector(_categoryExtractor);
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task CompileAsync_NullModule_Throws()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => selector.CompileAsync<LmpModule>(null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullTrainSet_Throws()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => selector.CompileAsync(new TestModule(), null!, (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainSet_ReturnsModule()
    {
        var selector = new Z3ConstrainedDemoSelector(_categoryExtractor);
        var module = new TestModule();
        var result = await selector.CompileAsync(module, [], (_, _) => 1f);
        Assert.Same(module, result);
    }
}

// Minimal module for null/empty tests
file class TestModule : LmpModule
{
    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult(input);
}
