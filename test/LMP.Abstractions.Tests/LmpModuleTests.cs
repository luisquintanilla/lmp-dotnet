namespace LMP.Tests;

public class LmpModuleTests
{
    private sealed class TestModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input);
        }
    }

    private sealed class ModuleWithPredictors : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input);
        }

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [];
    }

    [Fact]
    public void Subclass_Compiles_AndOverridesForwardAsync()
    {
        var module = new TestModule();

        Assert.NotNull(module);
    }

    [Fact]
    public async Task ForwardAsync_ExecutesSubclassLogic()
    {
        var module = new TestModule();

        var result = await module.ForwardAsync("test");

        Assert.Equal("test", result);
    }

    [Fact]
    public void Trace_DefaultsToNull()
    {
        var module = new TestModule();

        Assert.Null(module.Trace);
    }

    [Fact]
    public void Trace_CanBeSet()
    {
        var module = new TestModule();
        var trace = new Trace();

        module.Trace = trace;

        Assert.Same(trace, module.Trace);
    }

    [Fact]
    public void GetPredictors_DefaultReturnsEmpty()
    {
        var module = new TestModule();

        var predictors = module.GetPredictors();

        Assert.Empty(predictors);
    }

    [Fact]
    public void GetPredictors_CanBeOverridden()
    {
        var module = new ModuleWithPredictors();

        var predictors = module.GetPredictors();

        Assert.Empty(predictors);
    }

    [Fact]
    public async Task SaveAsync_ThrowsNotImplemented()
    {
        var module = new TestModule();

        await Assert.ThrowsAsync<NotImplementedException>(
            () => module.SaveAsync("test.json"));
    }

    [Fact]
    public async Task LoadAsync_ThrowsNotImplemented()
    {
        var module = new TestModule();

        await Assert.ThrowsAsync<NotImplementedException>(
            () => module.LoadAsync("test.json"));
    }
}
