namespace LMP.Tests;

public class SkillPoolExtensionsTests
{
    private static readonly SkillManifest Search = SkillManifest.For("search", "Web search");
    private static readonly SkillManifest Calc = SkillManifest.For("calc", "Math");
    private static readonly SkillManifest Mail = SkillManifest.For("mail", "Email");

    // ── AddSkillPool on TypedParameterSpace ─────────────────────────────────

    [Fact]
    public void AddSkillPool_AddsSubsetParameter()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search, Calc]);
        Assert.True(space.HasSubset);
        Assert.True(space.Parameters.ContainsKey("skills"));
    }

    [Fact]
    public void AddSkillPool_DefaultParamName_IsSkills()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search]);
        Assert.True(space.Parameters.ContainsKey("skills"));
    }

    [Fact]
    public void AddSkillPool_CustomParamName()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search], paramName: "capabilities");
        Assert.True(space.Parameters.ContainsKey("capabilities"));
    }

    [Fact]
    public void AddSkillPool_PoolContainsAllSkills()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search, Calc, Mail]);
        var subset = (Subset)space.Parameters["skills"];
        Assert.Equal(3, subset.Pool.Count);
    }

    [Fact]
    public void AddSkillPool_DefaultMinSize_IsOne()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search, Calc]);
        var subset = (Subset)space.Parameters["skills"];
        Assert.Equal(1, subset.MinSize);
    }

    [Fact]
    public void AddSkillPool_DefaultMaxSize_IsUnbounded()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search, Calc]);
        var subset = (Subset)space.Parameters["skills"];
        Assert.Equal(-1, subset.MaxSize);
    }

    [Fact]
    public void AddSkillPool_CustomMinMaxSize()
    {
        var space = TypedParameterSpace.Empty.AddSkillPool([Search, Calc, Mail], minSize: 2, maxSize: 3);
        var subset = (Subset)space.Parameters["skills"];
        Assert.Equal(2, subset.MinSize);
        Assert.Equal(3, subset.MaxSize);
    }

    [Fact]
    public void AddSkillPool_NullSpace_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => ((TypedParameterSpace)null!).AddSkillPool([Search]));

    [Fact]
    public void AddSkillPool_NullSkills_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => TypedParameterSpace.Empty.AddSkillPool((IReadOnlyList<SkillManifest>)null!));

    [Fact]
    public void AddSkillPool_NegativeMinSize_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(()
            => TypedParameterSpace.Empty.AddSkillPool([Search], minSize: -1));

    // ── WithSkillPool on OptimizationContext ────────────────────────────────

    private static OptimizationContext MakeContext()
    {
        var target = new StubTarget();
        return new OptimizationContext { Target = target, TrainSet = [new Example<string, string>("q", "a")], Metric = (_, _) => 1f };
    }

    [Fact]
    public void WithSkillPool_AddsToSearchSpace()
    {
        var ctx = MakeContext().WithSkillPool([Search, Calc]);
        Assert.True(ctx.SearchSpace.HasSubset);
    }

    [Fact]
    public void WithSkillPool_ReturnsSameContext()
    {
        var ctx = MakeContext();
        var returned = ctx.WithSkillPool([Search]);
        Assert.Same(ctx, returned);
    }

    [Fact]
    public void WithSkillPool_NullContext_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => ((OptimizationContext)null!).WithSkillPool([Search]));

    [Fact]
    public void WithSkillPool_NullSkills_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => MakeContext().WithSkillPool((IReadOnlyList<SkillManifest>)null!));

    // ── Stub target ─────────────────────────────────────────────────────────

    private sealed class StubTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult(((object)"ok", new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From(0);
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }
}
