namespace LMP.Tests;

/// <summary>
/// T1 regression tests: verifies <see cref="LmpModule"/> directly implements
/// <see cref="IOptimizationTarget"/> (no adapter required).
/// </summary>
public sealed class LmpModuleAsTargetTests
{
    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore() => new EchoModule();
    }

    [Fact]
    public void LmpModule_IsAn_IOptimizationTarget()
    {
        var module = new EchoModule();
        Assert.IsAssignableFrom<IOptimizationTarget>(module);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsTraceAndReturnsForwardOutput()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var (output, trace) = await target.ExecuteAsync("hello");

        Assert.Equal("hello", output);
        Assert.NotNull(trace);
    }

    [Fact]
    public void GetState_ApplyState_RoundTrip()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var snapshot = target.GetState();
        target.ApplyState(snapshot);

        // No exception, and the underlying typed state still matches.
        var typed = module.GetModuleState();
        Assert.NotNull(typed);
    }

    [Fact]
    public void GetService_ReturnsSelfForLmpModule()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        Assert.Same(module, target.GetService<LmpModule>());
    }

    [Fact]
    public void WithParameters_Empty_ClonesUnderlyingModule()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var clone = target.WithParameters(ParameterAssignment.Empty);

        Assert.NotNull(clone);
        Assert.IsType<EchoModule>(clone);
        Assert.NotSame(module, clone);
    }

    [Fact]
    public void WithParameters_NonEmpty_ThrowsNotSupportedExceptionCitingT2()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;
        var assignment = ParameterAssignment.Empty.With("anything", "value");

        var ex = Assert.Throws<NotSupportedException>(() => target.WithParameters(assignment));
        Assert.Contains("T2", ex.Message);
        Assert.Contains("fractal predictor", ex.Message);
    }

    [Fact]
    public void GetParameterSpace_IsEmpty_UntilT2()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        Assert.True(target.GetParameterSpace().IsEmpty);
    }
}
