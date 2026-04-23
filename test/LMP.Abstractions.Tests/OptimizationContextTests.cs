namespace LMP.Tests;

public class OptimizationContextTests
{
    private static readonly IOptimizationTarget StubTarget = new StubOptimizationTarget();
    private static readonly IReadOnlyList<Example> OneExample =
        [new Example<string, string>("in", "out")];
    private static readonly Func<Example, object, float> AlwaysOne = (_, _) => 1.0f;

    [Fact]
    public void Construction_SetsRequiredProperties()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };

        Assert.Same(StubTarget, ctx.Target);
        Assert.Same(OneExample, ctx.TrainSet);
        Assert.Same(AlwaysOne, ctx.Metric);
    }

    [Fact]
    public void Construction_DefaultDevSet_IsEmpty()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };
        Assert.Empty(ctx.DevSet);
    }

    [Fact]
    public void Construction_WithDevSet_SetsDevSet()
    {
        var devSet = new List<Example> { new Example<string, string>("d", "d") };
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne, DevSet = devSet };
        Assert.Same(devSet, ctx.DevSet);
    }

    [Fact]
    public void Construction_DefaultBudget_IsUnlimited()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };
        Assert.Same(CostBudget.Unlimited, ctx.Budget);
    }

    [Fact]
    public void Construction_DefaultSearchSpace_IsEmpty()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };
        Assert.Same(TypedParameterSpace.Empty, ctx.SearchSpace);
    }

    [Fact]
    public void Construction_TrialHistory_IsNewInstance()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };
        Assert.NotNull(ctx.TrialHistory);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public void Target_SetNull_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => new OptimizationContext { Target = null!, TrainSet = OneExample, Metric = AlwaysOne });

    [Fact]
    public void TrainSet_SetNull_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => new OptimizationContext { Target = StubTarget, TrainSet = null!, Metric = AlwaysOne });

    [Fact]
    public void Metric_SetNull_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(()
            => new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = null! });

    [Fact]
    public void Diagnostics_SnapshotsCanStoreAndRetrieveArbitraryValues()
    {
        var ctx = new OptimizationContext { Target = StubTarget, TrainSet = OneExample, Metric = AlwaysOne };
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
