using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests for <see cref="ToolPoolExtensions"/>.
/// </summary>
public class ToolPoolExtensionsTests
{
    private static AITool MakeTool(string name) =>
        AIFunctionFactory.Create(() => name, name);

    // ── AddToolPool (TypedParameterSpace extension) ───────────────────────

    [Fact]
    public void AddToolPool_AddsSubsetParameter()
    {
        var tool = MakeTool("search");
        var space = TypedParameterSpace.Empty.AddToolPool([tool]);

        Assert.True(space.Parameters.ContainsKey("tools"));
        var subset = Assert.IsAssignableFrom<Subset>(space.Parameters["tools"]);
        Assert.Single(subset.Pool);
    }

    [Fact]
    public void AddToolPool_DefaultParamName_IsTools()
    {
        var tool = MakeTool("search");
        var space = TypedParameterSpace.Empty.AddToolPool([tool]);
        Assert.True(space.Parameters.ContainsKey("tools"));
    }

    [Fact]
    public void AddToolPool_CustomParamName()
    {
        var tool = MakeTool("calc");
        var space = TypedParameterSpace.Empty.AddToolPool([tool], paramName: "my_tools");
        Assert.True(space.Parameters.ContainsKey("my_tools"));
        Assert.False(space.Parameters.ContainsKey("tools"));
    }

    [Fact]
    public void AddToolPool_MinAndMaxSize_AreRespected()
    {
        var tools = new[] { MakeTool("a"), MakeTool("b"), MakeTool("c") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools, minSize: 2, maxSize: 3);

        var subset = Assert.IsAssignableFrom<Subset>(space.Parameters["tools"]);
        Assert.Equal(2, subset.MinSize);
        Assert.Equal(3, subset.MaxSize);
    }

    [Fact]
    public void AddToolPool_DefaultMaxSize_IsUnbounded()
    {
        var tool = MakeTool("x");
        var space = TypedParameterSpace.Empty.AddToolPool([tool]);
        var subset = Assert.IsAssignableFrom<Subset>(space.Parameters["tools"]);
        Assert.Equal(-1, subset.MaxSize);
    }

    [Fact]
    public void AddToolPool_EmptyPool_StillAddsParameter()
    {
        var space = TypedParameterSpace.Empty.AddToolPool([], minSize: 0);
        Assert.True(space.Parameters.ContainsKey("tools"));
    }

    [Fact]
    public void AddToolPool_MultipleTools_AllInPool()
    {
        var tools = new[] { MakeTool("a"), MakeTool("b"), MakeTool("c") };
        var space = TypedParameterSpace.Empty.AddToolPool(tools);

        var subset = Assert.IsAssignableFrom<Subset>(space.Parameters["tools"]);
        Assert.Equal(3, subset.Pool.Count);
        Assert.All(subset.Pool, item => Assert.IsAssignableFrom<AITool>(item));
    }

    [Fact]
    public void AddToolPool_NullSpace_Throws()
    {
        TypedParameterSpace? space = null;
        Assert.Throws<ArgumentNullException>(() =>
            space!.AddToolPool([MakeTool("x")]));
    }

    [Fact]
    public void AddToolPool_NullTools_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TypedParameterSpace.Empty.AddToolPool(null!));
    }

    [Fact]
    public void AddToolPool_NegativeMinSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TypedParameterSpace.Empty.AddToolPool([MakeTool("x")], minSize: -1));
    }

    // ── WithToolPool (OptimizationContext extension) ──────────────────────

    [Fact]
    public void WithToolPool_UpdatesSearchSpace()
    {
        var tool = MakeTool("search");
        var target = DelegateTarget.For(_ => Task.FromResult<object>("ok"));
        var ctx = OptimizationContext.For(target, [], (_, _) => 1f);

        ctx.WithToolPool([tool]);

        Assert.True(ctx.SearchSpace.Parameters.ContainsKey("tools"));
    }

    [Fact]
    public void WithToolPool_ReturnsSameContext()
    {
        var tool = MakeTool("search");
        var target = DelegateTarget.For(_ => Task.FromResult<object>("ok"));
        var ctx = OptimizationContext.For(target, [], (_, _) => 1f);

        var returned = ctx.WithToolPool([tool]);

        Assert.Same(ctx, returned);
    }

    [Fact]
    public void WithToolPool_NullContext_Throws()
    {
        OptimizationContext? ctx = null;
        Assert.Throws<ArgumentNullException>(() =>
            ctx!.WithToolPool([MakeTool("x")]));
    }

    [Fact]
    public void WithToolPool_CustomParamName()
    {
        var tool = MakeTool("calc");
        var target = DelegateTarget.For(_ => Task.FromResult<object>("ok"));
        var ctx = OptimizationContext.For(target, [], (_, _) => 1f);

        ctx.WithToolPool([tool], paramName: "calc_tools");

        Assert.True(ctx.SearchSpace.Parameters.ContainsKey("calc_tools"));
    }

    // ── HasSubset reflected correctly ─────────────────────────────────────

    [Fact]
    public void AddToolPool_SpaceHasSubset_IsTrue()
    {
        var space = TypedParameterSpace.Empty.AddToolPool([MakeTool("x")]);
        Assert.True(space.HasSubset);
    }
}
