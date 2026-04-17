using LMP.Optimizers;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public sealed class LmpPipelinesTests
{
    // ── Null-guard ──────────────────────────────────────────────

    [Fact]
    public void Auto_NullModule_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LmpPipelines.Auto(null!, new FakePipelineClient()));
    }

    [Fact]
    public void Auto_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LmpPipelines.Auto(new PipelineTestModule(), null!));
    }

    // ── Returns correct type ────────────────────────────────────

    [Fact]
    public void Auto_ReturnsOptimizationPipeline()
    {
        var pipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient());
        Assert.IsType<OptimizationPipeline>(pipeline);
    }

    // ── Goal → stage sequences ──────────────────────────────────

    [Fact]
    public void Auto_Accuracy_HasThreeSteps()
    {
        var pipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient(), Goal.Accuracy);
        // BFS → GEPA → MIPROv2
        Assert.Equal(3, pipeline.Steps.Count);
        Assert.IsType<BootstrapFewShot>(pipeline.Steps[0]);
        Assert.IsType<GEPA>(pipeline.Steps[1]);
        Assert.IsType<MIPROv2>(pipeline.Steps[2]);
    }

    [Fact]
    public void Auto_Speed_HasSimba()
    {
        var pipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient(), Goal.Speed);
        // SIMBA only
        Assert.Single(pipeline.Steps);
        Assert.IsType<SIMBA>(pipeline.Steps[0]);
    }

    [Fact]
    public void Auto_Cost_HasBootstrapAndMIPROv2()
    {
        var pipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient(), Goal.Cost);
        // BFS → MIPROv2
        Assert.Equal(2, pipeline.Steps.Count);
        Assert.IsType<BootstrapFewShot>(pipeline.Steps[0]);
        Assert.IsType<MIPROv2>(pipeline.Steps[1]);
    }

    [Fact]
    public void Auto_Balanced_HasBootstrapAndGEPA()
    {
        var pipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient(), Goal.Balanced);
        // BFS → GEPA
        Assert.Equal(2, pipeline.Steps.Count);
        Assert.IsType<BootstrapFewShot>(pipeline.Steps[0]);
        Assert.IsType<GEPA>(pipeline.Steps[1]);
    }

    [Fact]
    public void Auto_DefaultGoal_IsAccuracy()
    {
        var defaultPipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient());
        var accuracyPipeline = LmpPipelines.Auto(new PipelineTestModule(), new FakePipelineClient(), Goal.Accuracy);
        Assert.Equal(defaultPipeline.Steps.Count, accuracyPipeline.Steps.Count);
        for (int i = 0; i < defaultPipeline.Steps.Count; i++)
            Assert.Equal(defaultPipeline.Steps[i].GetType(), accuracyPipeline.Steps[i].GetType());
    }

    // ── Invariant: pipelines are reproducible from Tier 2 code ─

    [Fact]
    public void Auto_Accuracy_PipelineReproducibleFromTier2()
    {
        // The Auto() one-liner MUST be literally equivalent to the Tier 2 .Use() chain.
        // This test asserts the invariant: no hidden steps, no private escape hatches.
        var module = new PipelineTestModule();
        var client = new FakePipelineClient();

        // Tier 4 (Auto façade)
        var tier4 = LmpPipelines.Auto(module, client, Goal.Accuracy);

        // Tier 2 (manual construction)
        var tier2 = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())
            .Use(new GEPA(client))
            .Use(new MIPROv2(client));

        Assert.Equal(tier4.Steps.Count, tier2.Steps.Count);
        for (int i = 0; i < tier4.Steps.Count; i++)
            Assert.Equal(tier4.Steps[i].GetType(), tier2.Steps[i].GetType());
    }
}

// ── Test Infrastructure ─────────────────────────────────────

file sealed class FakePipelineClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

file sealed class PipelineTestModule : LmpModule
{
    public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
        => Task.FromResult<object>("result");

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => [];

    protected override LmpModule CloneCore() => new PipelineTestModule();
}
