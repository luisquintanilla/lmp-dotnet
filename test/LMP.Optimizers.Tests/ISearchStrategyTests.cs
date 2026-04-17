#pragma warning disable CS0618 // tests ISampler/ISearchStrategy bridge — obsolete ISampler is intentional
using LMP.Optimizers;

namespace LMP.Tests;

public class ISearchStrategyTests
{
    // ── LegacyCategoricalAdapter ─────────────────────────────────────────

    [Fact]
    public void LegacyCategoricalAdapter_NullInnerThrows()
        => Assert.Throws<ArgumentNullException>(() => new LegacyCategoricalAdapter(null!));

    [Fact]
    public void LegacyCategoricalAdapter_ProposeNullSpaceThrows()
    {
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 3 }, seed: 0);
        var adapter = new LegacyCategoricalAdapter(sampler);
        Assert.Throws<ArgumentNullException>(() => adapter.Propose(null!));
    }

    [Fact]
    public void LegacyCategoricalAdapter_Propose_ReturnsCategoricalAssignment()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 4, ["b"] = 3 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 1);
        var adapter = new LegacyCategoricalAdapter(sampler);
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = adapter.Propose(space);

        Assert.False(assignment.IsEmpty);
        Assert.True(assignment.TryGet<int>("a", out var aVal));
        Assert.InRange(aVal, 0, 3);
        Assert.True(assignment.TryGet<int>("b", out var bVal));
        Assert.InRange(bVal, 0, 2);
    }

    [Fact]
    public void LegacyCategoricalAdapter_Update_IncrementsTrialCount()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 5 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 42);
        var adapter = new LegacyCategoricalAdapter(sampler);
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = adapter.Propose(space);
        adapter.Update(assignment, 0.8f, new TrialCost(100, 50, 50, 200, 1));

        Assert.Equal(1, adapter.TrialCount);
    }

    [Fact]
    public void LegacyCategoricalAdapter_Update_NullAssignmentThrows()
    {
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 2 }, seed: 0);
        var adapter = new LegacyCategoricalAdapter(sampler);
        Assert.Throws<ArgumentNullException>(
            () => adapter.Update(null!, 0.5f, new TrialCost(0, 0, 0, 0, 1)));
    }

    // ── CategoricalTpeSampler as ISearchStrategy ─────────────────────────

    [Fact]
    public void CategoricalTpe_AsISearchStrategy_Propose_ReturnsAssignment()
    {
        var cardinalities = new Dictionary<string, int> { ["p"] = 6 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 7);
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = ((ISearchStrategy)sampler).Propose(space);

        Assert.False(assignment.IsEmpty);
        Assert.True(assignment.TryGet<int>("p", out var v));
        Assert.InRange(v, 0, 5);
    }

    [Fact]
    public void CategoricalTpe_AsISearchStrategy_Update_IncrementsTrialCount()
    {
        var cardinalities = new Dictionary<string, int> { ["p"] = 4 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 99);
        ISearchStrategy strategy = sampler;
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = strategy.Propose(space);
        strategy.Update(assignment, 0.9f, new TrialCost(0, 0, 0, 0, 1));

        Assert.Equal(1, sampler.TrialCount);
    }

    [Fact]
    public void CategoricalTpe_AsISearchStrategy_ProposeNullThrows()
    {
        var sampler = new CategoricalTpeSampler(new Dictionary<string, int> { ["x"] = 2 }, seed: 0);
        Assert.Throws<ArgumentNullException>(
            () => ((ISearchStrategy)sampler).Propose(null!));
    }

    // ── CostAwareSampler as ISearchStrategy ──────────────────────────────

    [Fact]
    public void CostAware_AsISearchStrategy_Propose_ReturnsAssignment()
    {
        var cardinalities = new Dictionary<string, int> { ["q"] = 3 };
        var sampler = new CostAwareSampler(cardinalities, seed: 5);
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = ((ISearchStrategy)sampler).Propose(space);

        Assert.False(assignment.IsEmpty);
        Assert.True(assignment.TryGet<int>("q", out var v));
        Assert.InRange(v, 0, 2);
    }

    [Fact]
    public void CostAware_AsISearchStrategy_Update_IncrementsTrialCount()
    {
        var cardinalities = new Dictionary<string, int> { ["q"] = 3 };
        var sampler = new CostAwareSampler(cardinalities, seed: 5);
        ISearchStrategy strategy = sampler;
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = strategy.Propose(space);
        strategy.Update(assignment, 0.75f, new TrialCost(500, 300, 200, 100, 2));

        Assert.Equal(1, sampler.TrialCount);
    }

    // ── SmacSampler as ISearchStrategy ───────────────────────────────────

    [Fact]
    public void Smac_AsISearchStrategy_Propose_ReturnsAssignment()
    {
        var cardinalities = new Dictionary<string, int> { ["r"] = 5 };
        var sampler = new SmacSampler(cardinalities, seed: 11);
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        var assignment = ((ISearchStrategy)sampler).Propose(space);

        Assert.False(assignment.IsEmpty);
        Assert.True(assignment.TryGet<int>("r", out var v));
        Assert.InRange(v, 0, 4);
    }

    // ── Round-trip: propose → update → propose adapts ────────────────────

    [Fact]
    public void CategoricalTpe_AsISearchStrategy_ProposeAdaptsAfterUpdate()
    {
        var cardinalities = new Dictionary<string, int> { ["x"] = 3 };
        var sampler = new CategoricalTpeSampler(cardinalities, seed: 77);
        ISearchStrategy strategy = sampler;
        var space = TypedParameterSpace.FromCategorical(cardinalities);

        // Provide several high-scoring updates to bias TPE toward a particular value.
        for (int i = 0; i < 10; i++)
        {
            strategy.Update(
                ParameterAssignment.FromCategorical(new Dictionary<string, int> { ["x"] = 0 }),
                1.0f,
                new TrialCost(0, 0, 0, 0, 1));
        }

        // After many updates favouring x=0, sampler should have enough history to work.
        var assignment = strategy.Propose(space);
        Assert.False(assignment.IsEmpty);
    }
}
