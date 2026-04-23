namespace LMP.Tests;

public class OptimizationContextTests
{
    private static readonly IOptimizationTarget StubTarget = new StubOptimizationTarget();
    private static readonly IReadOnlyList<Example> OneExample =
        [new Example<string, string>("in", "out")];
    private static readonly Func<Example, object, float> AlwaysOne = (_, _) => 1.0f;

    [Fact]
    public void For_SetsRequiredProperties()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);

        Assert.Same(StubTarget, ctx.Target);
        Assert.Same(OneExample, ctx.TrainSet);
        Assert.Same(AlwaysOne, ctx.Metric);
    }

    [Fact]
    public void For_DefaultDevSet_IsEmpty()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);
        Assert.Empty(ctx.DevSet);
    }

    [Fact]
    public void For_WithDevSet_SetsDevSet()
    {
        var devSet = new List<Example> { new Example<string, string>("d", "d") };
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne, devSet);
        Assert.Same(devSet, ctx.DevSet);
    }

    [Fact]
    public void For_DefaultBudget_IsUnlimited()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);
        Assert.Same(CostBudget.Unlimited, ctx.Budget);
    }

    [Fact]
    public void For_DefaultSearchSpace_IsEmpty()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);
        Assert.Same(TypedParameterSpace.Empty, ctx.SearchSpace);
    }

    [Fact]
    public void For_TrialHistory_IsNewInstance()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);
        Assert.NotNull(ctx.TrialHistory);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public void For_NullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => OptimizationContext.For(null!, OneExample, AlwaysOne));

    [Fact]
    public void For_NullTrainSet_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => OptimizationContext.For(StubTarget, null!, AlwaysOne));

    [Fact]
    public void For_NullMetric_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => OptimizationContext.For(StubTarget, OneExample, null!));

    [Fact]
    public void Diagnostics_SnapshotsCanStoreAndRetrieveArbitraryValues()
    {
        var ctx = OptimizationContext.For(StubTarget, OneExample, AlwaysOne);
        ctx.Diagnostics.Snapshots["test:key"] = 42;
        Assert.Equal(42, ctx.Diagnostics.Snapshots["test:key"]);
    }

    // ── Minimal stub target ───────────────────────────────────────────────

    private sealed class StubOptimizationTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult((input, new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From(0);
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }
}
