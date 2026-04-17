using LMP.Extensions.Z3;
using Microsoft.Extensions.AI;
using Xunit;

namespace LMP.Extensions.Z3.Tests;

/// <summary>
/// Tests for <see cref="Z3FeasibilityOptimizer"/>.
/// </summary>
public sealed class Z3FeasibilityOptimizerTests
{
    private static AITool MakeTool(string name) =>
        AIFunctionFactory.Create(() => name, name);

    private static OptimizationContext MakeCtx(TypedParameterSpace? space = null)
    {
        var target = DelegateTarget.For(_ => Task.FromResult<object>("ok"));
        var ctx = OptimizationContext.For(target, [], (_, _) => 1f);
        if (space is not null)
            ctx.SearchSpace = space;
        return ctx;
    }

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullParamName_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new Z3FeasibilityOptimizer(null!));
    }

    [Fact]
    public void Constructor_WhitespaceParamName_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new Z3FeasibilityOptimizer("   "));
    }

    // ── RequireAtLeastOne ─────────────────────────────────────────────────

    [Fact]
    public void RequireAtLeastOne_EmptyArray_Throws()
    {
        var opt = new Z3FeasibilityOptimizer();
        Assert.Throws<ArgumentException>(() => opt.RequireAtLeastOne());
    }

    [Fact]
    public void RequireAtLeastOne_NullArray_Throws()
    {
        var opt = new Z3FeasibilityOptimizer();
        Assert.Throws<ArgumentNullException>(() => opt.RequireAtLeastOne(null!));
    }

    // ── IsFeasible ────────────────────────────────────────────────────────

    [Fact]
    public void IsFeasible_NoConstraints_AlwaysTrue()
    {
        var opt = new Z3FeasibilityOptimizer();
        Assert.True(opt.IsFeasible(["search", "calc"]));
        Assert.True(opt.IsFeasible([]));
    }

    [Fact]
    public void IsFeasible_RequireAtLeastOne_SatisfiedReturnsFalse()
    {
        var opt = new Z3FeasibilityOptimizer()
            .RequireAtLeastOne("search", "lookup");

        Assert.True(opt.IsFeasible(["search"]));
        Assert.True(opt.IsFeasible(["lookup"]));
        Assert.True(opt.IsFeasible(["search", "calc"]));
        Assert.False(opt.IsFeasible(["calc"]));
        Assert.False(opt.IsFeasible([]));
    }

    [Fact]
    public void IsFeasible_ExcludeCombination_BothPresent_ReturnsFalse()
    {
        var opt = new Z3FeasibilityOptimizer()
            .ExcludeCombination("search", "web_search");

        Assert.True(opt.IsFeasible(["search"]));
        Assert.True(opt.IsFeasible(["web_search"]));
        Assert.False(opt.IsFeasible(["search", "web_search"]));
        Assert.True(opt.IsFeasible(["search", "calc"]));
    }

    [Fact]
    public void IsFeasible_MultipleConstraints_AllMustHold()
    {
        var opt = new Z3FeasibilityOptimizer()
            .RequireAtLeastOne("search", "lookup")
            .ExcludeCombination("search", "web_search");

        // Has search, no web_search → ✓
        Assert.True(opt.IsFeasible(["search"]));

        // Has lookup only → ✓ (RequireAtLeastOne satisfied)
        Assert.True(opt.IsFeasible(["lookup"]));

        // Has search AND web_search → ✗ (ExcludeCombination fails)
        Assert.False(opt.IsFeasible(["search", "web_search"]));

        // No required tool → ✗
        Assert.False(opt.IsFeasible(["calc"]));
    }

    // ── CanAppearInFeasibleSubset ─────────────────────────────────────────

    [Fact]
    public void CanAppearInFeasibleSubset_NoConstraints_AllToolsFeasible()
    {
        var opt = new Z3FeasibilityOptimizer();
        var pool = new[] { "a", "b", "c" }.ToList();

        Assert.True(opt.CanAppearInFeasibleSubset("a", pool, minSize: 1, maxSize: 3));
        Assert.True(opt.CanAppearInFeasibleSubset("b", pool, minSize: 1, maxSize: 3));
    }

    [Fact]
    public void CanAppearInFeasibleSubset_ToolNotInPool_ReturnsFalse()
    {
        var opt = new Z3FeasibilityOptimizer();
        var pool = new[] { "a", "b" }.ToList();

        Assert.False(opt.CanAppearInFeasibleSubset("z", pool, minSize: 1, maxSize: 2));
    }

    [Fact]
    public void CanAppearInFeasibleSubset_ExcludeCombination_BothStillFeasibleAlone()
    {
        // ExcludeCombination means they can't coexist, but each CAN appear in some subset
        var opt = new Z3FeasibilityOptimizer()
            .ExcludeCombination("search", "web_search");
        var pool = new[] { "search", "web_search", "calc" }.ToList();

        // "search" can appear without "web_search"
        Assert.True(opt.CanAppearInFeasibleSubset("search", pool, minSize: 1, maxSize: 2));
        // "web_search" can appear without "search"
        Assert.True(opt.CanAppearInFeasibleSubset("web_search", pool, minSize: 1, maxSize: 2));
    }

    [Fact]
    public void CanAppearInFeasibleSubset_RequireAtLeastOne_MissingTool_Infeasible()
    {
        // Pool only has "calc" (not in the required group); any selection must include
        // "search" or "lookup", but there are none in pool
        var opt = new Z3FeasibilityOptimizer()
            .RequireAtLeastOne("search", "lookup");
        var pool = new[] { "calc" }.ToList();

        Assert.False(opt.CanAppearInFeasibleSubset("calc", pool, minSize: 1, maxSize: 1));
    }

    // ── OptimizeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_NoSubsetParam_NoOp()
    {
        var opt = new Z3FeasibilityOptimizer("tools");
        var ctx = MakeCtx(); // SearchSpace is Empty
        var originalSpace = ctx.SearchSpace;

        await opt.OptimizeAsync(ctx);

        Assert.Same(originalSpace, ctx.SearchSpace);
    }

    [Fact]
    public async Task OptimizeAsync_NoConstraints_KeepsAllTools()
    {
        var tools = new[] { MakeTool("a"), MakeTool("b"), MakeTool("c") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools);
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("tools");

        await opt.OptimizeAsync(ctx);

        var subset = Assert.IsType<Subset>(ctx.SearchSpace.Parameters["tools"]);
        Assert.Equal(3, subset.Pool.Count);
    }

    [Fact]
    public async Task OptimizeAsync_StoresFeasibleToolsInBag()
    {
        var tools = new[] { MakeTool("search"), MakeTool("calc") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools);
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("tools");

        await opt.OptimizeAsync(ctx);

        Assert.True(ctx.Bag.ContainsKey("lmp.z3:feasible:tools"));
        var feasible = Assert.IsAssignableFrom<IReadOnlySet<string>>(
            ctx.Bag["lmp.z3:feasible:tools"]);
        Assert.Contains("search", feasible);
        Assert.Contains("calc", feasible);
    }

    [Fact]
    public async Task OptimizeAsync_RequireAtLeastOne_RemovesToolWithNoFeasibleSubset()
    {
        // Pool: ["calc"] only. Constraint requires "search" or "lookup".
        // "calc" can never be in a feasible subset → gets pruned.
        var tools = new[] { MakeTool("calc") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools);
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("tools")
            .RequireAtLeastOne("search", "lookup");

        await opt.OptimizeAsync(ctx);

        // Parameter removed (no feasible tools remain)
        Assert.False(ctx.SearchSpace.Parameters.ContainsKey("tools"));
    }

    [Fact]
    public async Task OptimizeAsync_ExcludeCombination_BothToolsStayInPool()
    {
        // Both "search" and "web_search" can individually appear in valid subsets
        var tools = new[] { MakeTool("search"), MakeTool("web_search"), MakeTool("calc") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools);
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("tools")
            .ExcludeCombination("search", "web_search");

        await opt.OptimizeAsync(ctx);

        var subset = Assert.IsType<Subset>(ctx.SearchSpace.Parameters["tools"]);
        var names = subset.Pool.Cast<AITool>().Select(t => t.Name).ToHashSet();
        Assert.Contains("search", names);
        Assert.Contains("web_search", names);
        Assert.Contains("calc", names);
    }

    [Fact]
    public async Task OptimizeAsync_BagKey_UsesParamName()
    {
        var tools = new[] { MakeTool("x") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools, paramName: "my_tools");
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("my_tools");

        await opt.OptimizeAsync(ctx);

        Assert.True(ctx.Bag.ContainsKey("lmp.z3:feasible:my_tools"));
        Assert.False(ctx.Bag.ContainsKey("lmp.z3:feasible:tools"));
    }

    [Fact]
    public async Task OptimizeAsync_NullCtx_Throws()
    {
        var opt = new Z3FeasibilityOptimizer();
        await Assert.ThrowsAsync<ArgumentNullException>(() => opt.OptimizeAsync(null!));
    }

    [Fact]
    public async Task OptimizeAsync_RequireAtLeastOne_FeasibleToolsPreserved()
    {
        // Pool: search, lookup, calc. Requires at least one of {search, lookup}.
        // "calc" alone can't satisfy the constraint, BUT calc can appear WITH search or lookup.
        // So "calc" IS individually feasible.
        var tools = new[] { MakeTool("search"), MakeTool("lookup"), MakeTool("calc") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools, minSize: 1, maxSize: 3);
        var ctx = MakeCtx(space);
        var opt = new Z3FeasibilityOptimizer("tools")
            .RequireAtLeastOne("search", "lookup");

        await opt.OptimizeAsync(ctx);

        var subset = Assert.IsType<Subset>(ctx.SearchSpace.Parameters["tools"]);
        var names = subset.Pool.Cast<AITool>().Select(t => t.Name).ToHashSet();
        // All three tools can appear in feasible subsets
        Assert.Contains("search", names);
        Assert.Contains("lookup", names);
        Assert.Contains("calc", names);
    }
}
