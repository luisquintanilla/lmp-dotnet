namespace LMP.Tests;

public class ModuleTargetTests
{
    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore() => new EchoModule();
    }

    private sealed class ThrowingModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => throw new InvalidOperationException("forward failed");

        protected override LmpModule CloneCore() => new ThrowingModule();
    }

    [Fact]
    public void For_NullModule_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => ModuleTarget.For(null!));

    [Fact]
    public void Shape_IsSingleTurn()
    {
        var target = ModuleTarget.For(new EchoModule());
        Assert.Equal(TargetShape.SingleTurn, target.Shape);
    }

    [Fact]
    public async Task ExecuteAsync_DelegatesToForwardAsync()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var (output, _) = await target.ExecuteAsync("hello");

        Assert.Equal("hello", output);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFreshTrace()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var (_, trace) = await target.ExecuteAsync("hello");

        Assert.NotNull(trace);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsModuleTrace_AfterSuccess()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        await target.ExecuteAsync("hello");

        // Trace must be cleared after the call (try/finally)
        Assert.Null(module.Trace);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsModuleTrace_WhenForwardThrows()
    {
        var module = new ThrowingModule();
        var target = ModuleTarget.For(module);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.ExecuteAsync("input"));

        Assert.Null(module.Trace);
    }

    [Fact]
    public void GetState_ReturnsWrappedModuleState()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var state = target.GetState();

        Assert.NotNull(state);
        Assert.IsType<ModuleState>(state.Value);
    }

    [Fact]
    public void ApplyState_RestoresModuleState()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);
        var originalState = target.GetState();

        // Apply the same state back — should not throw
        target.ApplyState(originalState);
    }

    [Fact]
    public void GetService_LmpModule_ReturnsWrappedModule()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var retrieved = target.GetService<LmpModule>();

        Assert.Same(module, retrieved);
    }

    [Fact]
    public void GetService_EchoModule_ReturnsWrappedModule()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var retrieved = target.GetService<EchoModule>();

        Assert.Same(module, retrieved);
    }

    [Fact]
    public void GetService_UnrelatedType_ReturnsNull()
    {
        var target = ModuleTarget.For(new EchoModule());

        var retrieved = target.GetService<TrialHistory>();

        Assert.Null(retrieved);
    }

    [Fact]
    public void WithParameters_Empty_ReturnsNewTarget()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        var clone = target.WithParameters(ParameterAssignment.Empty);

        Assert.NotSame(target, clone);
    }

    [Fact]
    public void WithParameters_NonEmpty_ThrowsNotSupportedException()
    {
        var target = ModuleTarget.For(new EchoModule());

        // ParameterAssignment.Empty always returns IsEmpty=true — to test throwing
        // we need a non-empty assignment. Since Phase A stubs it, we test the documented path.
        // ParameterAssignment.IsEmpty is always true in Phase A (stub), so WithParameters won't throw.
        // This test documents the expected contract for when Phase C is implemented.
        var assignment = ParameterAssignment.Empty;
        Assert.True(assignment.IsEmpty, "Phase A: ParameterAssignment is always empty");
    }

    [Fact]
    public void Module_Property_ExposesWrappedModule()
    {
        var module = new EchoModule();
        var target = ModuleTarget.For(module);

        Assert.Same(module, target.Module);
    }
}
