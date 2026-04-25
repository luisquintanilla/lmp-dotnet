using Microsoft.Extensions.AI;
using Xunit;

namespace LMP.Tests;

public class IMetricTests
{
    private record Output(float Value);
    private record Label(float Value);

    [Fact]
    public async Task CreateVector_SyncFn_ReturnsVector()
    {
        var metric = Metric.CreateVector<Output, Label>(
            (predicted, expected, _) => new MetricVector(
                score: predicted.Value >= expected.Value ? 1f : 0f,
                tokens: 100));

        var example = new Example<string, Label>("q", new Label(0.5f));
        var ctx = new MetricContext(example, new Output(0.9f), new Trace(), 50.0);

        var result = await metric.EvaluateAsync(ctx);
        Assert.Equal(1f, result.Score);
        Assert.Equal(100L, result.Tokens);
    }

    [Fact]
    public async Task CreateVectorAsync_AsyncFn_ReturnsVector()
    {
        var metric = Metric.CreateVectorAsync<Output, Label>(
            async (predicted, _, _, ct) =>
            {
                await Task.Delay(1, ct);
                return MetricVector.FromScore(0.75f);
            });

        var example = new Example<string, Label>("q", new Label(0.5f));
        var ctx = new MetricContext(example, new Output(0.9f), new Trace(), 30.0);

        var result = await metric.EvaluateAsync(ctx);
        Assert.Equal(0.75f, result.Score);
    }

    [Fact]
    public async Task ToVectorMetric_WrapsScalarFn_PopulatesCostFromTrace()
    {
        var trace = new Trace();
        trace.Record("pred", "input", "output",
            new UsageDetails { InputTokenCount = 50, OutputTokenCount = 50, TotalTokenCount = 100 });

        var scalar = Metric.Create<Output, Label>((p, _) => p.Value);
        var vector = Metric.ToVectorMetric(scalar);

        var example = new Example<string, Label>("q", new Label(0.5f));
        var ctx = new MetricContext(example, new Output(0.8f), trace, 100.0);

        var result = await vector.EvaluateAsync(ctx);
        Assert.Equal(0.8f, result.Score);
        Assert.Equal(100L, result.Tokens);
        Assert.Equal(100.0, result.LatencyMs);
        Assert.Equal(1, result.Turns);
    }

    [Fact]
    public async Task CreateVector_NonNullContext_Succeeds()
    {
        var metric = Metric.CreateVector<Output, Label>((p, e, _) => MetricVector.FromScore(1f));
        var example = new Example<string, Label>("q", new Label(0.5f));
        var ctx = new MetricContext(example, new Output(1f), new Trace(), 0);

        var result = await metric.EvaluateAsync(ctx);
        Assert.Equal(1f, result.Score);
    }

    [Fact]
    public void CreateVector_NullFn_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => Metric.CreateVector<Output, Label>(null!));
    }

    [Fact]
    public void CreateVectorAsync_NullFn_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => Metric.CreateVectorAsync<Output, Label>(null!));
    }

    [Fact]
    public void ToVectorMetric_NullFn_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => Metric.ToVectorMetric(null!));
    }

    [Fact]
    public async Task CancellationToken_IsPassedThrough()
    {
        var cts = new CancellationTokenSource();
        var metric = Metric.CreateVectorAsync<Output, Label>(
            async (_, _, _, ct) =>
            {
                await Task.Delay(5000, ct);
                return MetricVector.FromScore(1f);
            });

        var example = new Example<string, Label>("q", new Label(0.5f));
        var ctx = new MetricContext(example, new Output(1f), new Trace(), 0);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => metric.EvaluateAsync(ctx, cts.Token).AsTask());
    }
}
