namespace LMP.Tests;

public class IOptimizerTests
{
    private sealed class StubOptimizer : IOptimizer
    {
        public Task<TModule> CompileAsync<TModule, TInput, TLabel>(
            TModule module,
            IReadOnlyList<Example<TInput, TLabel>> trainSet,
            Func<TLabel, object, float> metric,
            CancellationToken cancellationToken = default)
            where TModule : LmpModule
        {
            return Task.FromResult(module);
        }
    }

    private sealed class TestModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(input);
        }
    }

    [Fact]
    public async Task StubImplementsInterface()
    {
        IOptimizer optimizer = new StubOptimizer();
        var module = new TestModule();
        var trainSet = new List<Example<string, string>>
        {
            new("input", "label")
        };

        var result = await optimizer.CompileAsync(
            module, trainSet, (label, output) => 1.0f);

        Assert.Same(module, result);
    }
}
