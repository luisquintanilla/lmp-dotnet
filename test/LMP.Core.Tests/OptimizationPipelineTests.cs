namespace LMP.Tests;

public class OptimizationPipelineTests
{
    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore() => new EchoModule();
    }

    /// <summary>
    /// Records whether <see cref="OptimizeAsync"/> was called and can inspect the context.
    /// </summary>
    private sealed class SpyOptimizer : IOptimizer
    {
        public bool WasCalled { get; private set; }
        public OptimizationContext? ReceivedContext { get; private set; }

        public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
        {
            WasCalled = true;
            ReceivedContext = ctx;
            return Task.CompletedTask;
        }
    }

    private static readonly IReadOnlyList<Example> OneExample =
        [new Example<string, string>("in", "out")];
    private static readonly Func<Example, object, float> AlwaysOne = (_, _) => 1.0f;

    // ── Construction ─────────────────────────────────────────────────────

    [Fact]
    public void For_Module_CreatesEmptyPipeline()
    {
        var pipeline = OptimizationPipeline.For(new EchoModule());
        Assert.Empty(pipeline.Steps);
    }

    [Fact]
    public void For_NullModule_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => OptimizationPipeline.For((LmpModule)null!));

    [Fact]
    public void For_NullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => OptimizationPipeline.For((IOptimizationTarget)null!));

    // ── Fluent builder ───────────────────────────────────────────────────

    [Fact]
    public void Use_AddsStepToSteps()
    {
        var step = new SpyOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(step);
        Assert.Single(pipeline.Steps);
        Assert.Same(step, pipeline.Steps[0]);
    }

    [Fact]
    public void Use_MultipleSteps_PreservesOrder()
    {
        var step1 = new SpyOptimizer();
        var step2 = new SpyOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(step1).Use(step2);
        Assert.Equal([step1, step2], pipeline.Steps);
    }

    [Fact]
    public void Use_NullStep_ThrowsArgumentNullException()
    {
        var pipeline = OptimizationPipeline.For(new EchoModule());
        Assert.Throws<ArgumentNullException>(() => pipeline.Use(null!));
    }

    [Fact]
    public void Steps_IsReadOnly()
    {
        var pipeline = OptimizationPipeline.For(new EchoModule());
        Assert.IsAssignableFrom<IReadOnlyList<IOptimizer>>(pipeline.Steps);
    }

    // ── OptimizeAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_EmptyPipeline_ReturnsZeroScores()
    {
        // EchoModule returns input object, which is not equal to "out" label → score = 0
        var pipeline = OptimizationPipeline.For(new EchoModule());

        var result = await pipeline.OptimizeAsync(OneExample, null, (_, _) => 0.5f);

        Assert.Equal(0.5f, result.BaselineScore);
        Assert.Equal(0.5f, result.OptimizedScore);
    }

    [Fact]
    public async Task OptimizeAsync_WithStep_CallsStepOptimizeAsync()
    {
        var spy = new SpyOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(spy);

        await pipeline.OptimizeAsync(OneExample, null, AlwaysOne);

        Assert.True(spy.WasCalled);
    }

    [Fact]
    public async Task OptimizeAsync_StepReceivesSharedContext()
    {
        var spy = new SpyOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(spy);

        await pipeline.OptimizeAsync(OneExample, null, AlwaysOne);

        Assert.NotNull(spy.ReceivedContext);
        Assert.Same(OneExample, spy.ReceivedContext!.TrainSet);
    }

    [Fact]
    public async Task OptimizeAsync_BaselineAndOptimized_OnSameEvalSet()
    {
        // Step mutates nothing → both scores from same eval set should equal baseline
        var spy = new SpyOptimizer();
        var module = new EchoModule();
        var pipeline = OptimizationPipeline.For(module).Use(spy);
        float score = 0.75f;

        var result = await pipeline.OptimizeAsync(OneExample, null, (_, _) => score);

        Assert.Equal(result.BaselineScore, result.OptimizedScore);
    }

    [Fact]
    public async Task OptimizeAsync_WithDevSet_UsesDevSetForEvaluation()
    {
        var spy = new SpyOptimizer();
        var devSet = (IReadOnlyList<Example>)[new Example<string, string>("d", "d")];
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(spy);

        // The DevSet should be passed through the context
        var result = await pipeline.OptimizeAsync(OneExample, devSet, AlwaysOne);

        Assert.NotNull(spy.ReceivedContext);
        Assert.Same(devSet, spy.ReceivedContext!.DevSet);
    }

    [Fact]
    public async Task OptimizeAsync_BudgetExceeded_SkipsRemainingSteps()
    {
        var spy1 = new SpyOptimizer();
        var spy2 = new SpyOptimizer();

        // Budget of 0 tokens — immediately exceeded
        var pipeline = OptimizationPipeline
            .For(new EchoModule())
            .WithBudget(b => b.MaxTokens(0))
            .Use(spy1)
            .Use(spy2);

        await pipeline.OptimizeAsync(OneExample, null, AlwaysOne);

        // Both steps skipped because budget of 0 is already exceeded at pipeline start
        Assert.False(spy1.WasCalled);
        Assert.False(spy2.WasCalled);
    }

    [Fact]
    public async Task OptimizeAsync_Result_ContainsTarget()
    {
        var pipeline = OptimizationPipeline.For(new EchoModule());
        var result = await pipeline.OptimizeAsync(OneExample, null, AlwaysOne);
        Assert.NotNull(result.Target);
    }

    [Fact]
    public async Task OptimizeAsync_Result_TrialsFromContext()
    {
        var trialAdder = new TrialAddingOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(trialAdder);

        var result = await pipeline.OptimizeAsync(OneExample, null, AlwaysOne);

        Assert.Single(result.Trials);
    }

    // ── IOptimizer (pipeline nesting) ────────────────────────────────────

    [Fact]
    public void Pipeline_ImplementsIOptimizer()
    {
        IOptimizer optimizer = OptimizationPipeline.For(new EchoModule());
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task PipelineAsStep_CanBeNestedInsideAnother()
    {
        var inner = OptimizationPipeline.For(new EchoModule());
        var spy = new SpyOptimizer();
        inner.Use(spy);

        var outer = OptimizationPipeline.For(new EchoModule()).Use(inner);
        var outerCtx = new OptimizationContext
        {
            Target = new EchoModule(),
            TrainSet = OneExample,
            Metric = AlwaysOne
        };

        await outer.OptimizeAsync(outerCtx);

        Assert.True(spy.WasCalled);
    }

    // ── Extension ────────────────────────────────────────────────────────

    [Fact]
    public void AsOptimizationPipeline_ReturnsPipelineWithEmptySteps()
    {
        var module = new EchoModule();
        var pipeline = module.AsOptimizationPipeline();
        Assert.Empty(pipeline.Steps);
    }

    [Fact]
    public void Module_IsItsOwnOptimizationTarget()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;
        Assert.Same(module, target.GetService<LmpModule>());
    }

    [Fact]
    public void AsOptimizationContext_SetsRequiredFields()
    {
        var module = new EchoModule();
        var ctx = module.AsOptimizationContext(OneExample, AlwaysOne);
        Assert.Same(OneExample, ctx.TrainSet);
        Assert.Same(AlwaysOne, ctx.Metric);
    }

    // ── Invariant proof ──────────────────────────────────────────────────

    [Fact]
    public void InvariantTest_PipelineSteps_AreInspectable()
    {
        // Tier 4 "Auto" façade builds a pipeline whose steps are visible at Tier 2.
        // Here we verify the invariant: any pipeline can be expressed as .Use() calls.
        var step1 = new SpyOptimizer();
        var step2 = new SpyOptimizer();
        var pipeline = OptimizationPipeline.For(new EchoModule()).Use(step1).Use(step2);

        // A user can inspect Steps and reproduce this pipeline from Tier 2 code
        Assert.Equal(2, pipeline.Steps.Count);
        Assert.Same(step1, pipeline.Steps[0]);
        Assert.Same(step2, pipeline.Steps[1]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class TrialAddingOptimizer : IOptimizer
    {
        public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
        {
            ctx.TrialHistory.Add(new Trial(0.5f, new TrialCost(100, 50, 50, 10, 1)));
            return Task.CompletedTask;
        }
    }
}
