namespace LMP.Tests;

public class IOptimizerTests
{
    private sealed class StubOptimizer : IOptimizer
    {
        public Task<TModule> CompileAsync<TModule>(
            TModule module,
            IReadOnlyList<Example> trainSet,
            Func<Example, object, float> metric,
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
        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "label")
        };

        var result = await optimizer.CompileAsync(
            module, trainSet, (example, output) => 1.0f);

        Assert.Same(module, result);
    }

    [Fact]
    public async Task MetricReceivesExampleAndOutput()
    {
        Example? capturedExample = null;
        object? capturedOutput = null;

        var optimizer = new StubOptimizerWithMetricInvocation();
        var module = new TestModule();
        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "expected")
        };

        await optimizer.CompileAsync(
            module, trainSet, (example, output) =>
            {
                capturedExample = example;
                capturedOutput = output;
                return 1.0f;
            });

        Assert.NotNull(capturedExample);
        Assert.Equal("input", capturedExample!.WithInputs());
        Assert.Equal("expected", capturedExample.GetLabel());
    }

    private sealed class StubOptimizerWithMetricInvocation : IOptimizer
    {
        public async Task<TModule> CompileAsync<TModule>(
            TModule module,
            IReadOnlyList<Example> trainSet,
            Func<Example, object, float> metric,
            CancellationToken cancellationToken = default)
            where TModule : LmpModule
        {
            foreach (var example in trainSet)
            {
                var output = await module.ForwardAsync(example.WithInputs(), cancellationToken);
                metric(example, output);
            }

            return module;
        }
    }
}
