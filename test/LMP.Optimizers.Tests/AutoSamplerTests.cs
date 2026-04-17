#pragma warning disable CS0618 // tests obsolete AutoSampler.For(Dictionary) overload intentionally
using LMP.Optimizers;

namespace LMP.Tests;

public class AutoSamplerTests
{
    // ── TypedParameterSpace overload ──────────────────────────────────────

    [Fact]
    public void For_TypedSpace_NoBudget_ReturnsCategoricalTpeSampler()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(4));
        var strategy = AutoSampler.For(space);
        Assert.IsType<CategoricalTpeSampler>(strategy);
    }

    [Fact]
    public void For_TypedSpace_UnlimitedBudget_ReturnsCategoricalTpeSampler()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(4));
        var strategy = AutoSampler.For(space, CostBudget.Unlimited);
        Assert.IsType<CategoricalTpeSampler>(strategy);
    }

    [Fact]
    public void For_TypedSpace_MaxTokensBudget_ReturnsCostAwareSampler()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(4));
        var budget = new CostBudget.Builder().MaxTokens(100_000).Build();
        var strategy = AutoSampler.For(space, budget);
        Assert.IsType<CostAwareSampler>(strategy);
    }

    [Fact]
    public void For_TypedSpace_MaxTokensBudget_EmptySpace_ReturnsTpeSampler()
    {
        // When space has no categorical dims, fallback to TPE (CostAware needs cardinalities).
        var space = TypedParameterSpace.Empty.Add("instr", new StringValued("be helpful"));
        var budget = new CostBudget.Builder().MaxTokens(100_000).Build();
        var strategy = AutoSampler.For(space, budget);
        // Space has no categorical params → CostAwareSampler fallback (empty cardinalities → TPE)
        Assert.IsType<CategoricalTpeSampler>(strategy);
    }

    [Fact]
    public void For_TypedSpace_NullSpaceThrows()
        => Assert.Throws<ArgumentNullException>(() => AutoSampler.For((TypedParameterSpace)null!));

    [Fact]
    public void For_TypedSpace_WithSeed_IsReproducible()
    {
        var space = TypedParameterSpace.Empty.Add("a", new Categorical(5));
        var s1 = (CategoricalTpeSampler)AutoSampler.For(space, seed: 99);
        var s2 = (CategoricalTpeSampler)AutoSampler.For(space, seed: 99);

        var space2 = TypedParameterSpace.FromCategorical(new Dictionary<string, int> { ["a"] = 5 });
        var proposal1 = ((ISearchStrategy)s1).Propose(space2);
        var proposal2 = ((ISearchStrategy)s2).Propose(space2);

        Assert.Equal(proposal1.Get<int>("a"), proposal2.Get<int>("a"));
    }

    // ── Proposed assignment is usable ─────────────────────────────────────

    [Fact]
    public void For_TypedSpace_ProposalContainsExpectedParameters()
    {
        var space = TypedParameterSpace.Empty
            .Add("demo_count", new Categorical(4))
            .Add("instr_idx", new Categorical(3));
        var strategy = AutoSampler.For(space);

        var assignment = strategy.Propose(space);

        Assert.True(assignment.TryGet<int>("demo_count", out var demos));
        Assert.InRange(demos, 0, 3);
        Assert.True(assignment.TryGet<int>("instr_idx", out var instr));
        Assert.InRange(instr, 0, 2);
    }

    // ── Legacy Dictionary overload (deprecated) ───────────────────────────

    [Fact]
    public void For_Dictionary_ReturnsCategoricalTpeSampler()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = AutoSampler.For(cardinalities);
        Assert.IsType<CategoricalTpeSampler>(sampler);
    }

    [Fact]
    public void For_Dictionary_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => AutoSampler.For((Dictionary<string, int>)null!));
}
