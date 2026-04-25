namespace LMP.Tests;

public class IOptimizerTests
{
    private sealed class TestModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    /// <summary>Minimal IOptimizationTarget stub for unit tests.</summary>
    private sealed class StubTarget(TestModule module) : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;

        public async Task<(object Output, Trace Trace)> ExecuteAsync(
            object input, CancellationToken ct = default)
        {
            var output = await module.ForwardAsync(input, ct);
            return (output, new Trace());
        }

        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From(module.GetModuleState());
        public void ApplyState(TargetState state) => module.ApplyState(state.As<ModuleState>());
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => module as TService;
    }

    private sealed class StubOptimizer : IOptimizer
    {
        public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task StubImplementsInterface()
    {
        IOptimizer optimizer = new StubOptimizer();
        var module = new TestModule();
        var ctx = new OptimizationContext
        {
            Target = new StubTarget(module),
            TrainSet = [new Example<string, string>("input", "label")],
            Metric = (_, _) => 1.0f
        };

        await optimizer.OptimizeAsync(ctx);
        // No exception = interface contract satisfied
    }

    [Fact]
    public async Task MetricReceivesExampleAndOutput()
    {
        Example? capturedExample = null;
        object? capturedOutput = null;

        var optimizer = new StubOptimizerWithMetricInvocation();
        var module = new TestModule();
        var ctx = new OptimizationContext
        {
            Target = new StubTarget(module),
            TrainSet = [new Example<string, string>("input", "expected")],
            Metric = (example, output) =>
            {
                capturedExample = example;
                capturedOutput = output;
                return 1.0f;
            }
        };

        await optimizer.OptimizeAsync(ctx);

        Assert.NotNull(capturedExample);
        Assert.Equal("input", capturedExample!.WithInputs());
        Assert.Equal("expected", capturedExample.GetLabel());
        Assert.NotNull(capturedOutput);
    }

    private sealed class StubOptimizerWithMetricInvocation : IOptimizer
    {
        public async Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
        {
            var module = ctx.Target.GetService<LmpModule>()!;
            foreach (var example in ctx.TrainSet)
            {
                var output = await module.ForwardAsync(example.WithInputs(), ct);
                ctx.Metric(example, output);
            }
        }
    }
}
