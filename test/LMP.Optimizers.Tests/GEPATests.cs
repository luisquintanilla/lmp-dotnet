using System.Collections;
using LMP.Optimizers;
using Microsoft.Extensions.AI;
namespace LMP.Tests;

public sealed class GEPATests
{
    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GEPA(reflectionClient: null!));
    }

    [Fact]
    public void Constructor_ZeroIterations_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GEPA(new FakeReflectionClient(), maxIterations: 0));
    }

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var gepa = new GEPA(new FakeReflectionClient(), maxIterations: 10, miniBatchSize: 3);
        Assert.NotNull(gepa);
    }

    // ── IOptimizer Contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new GEPA(new FakeReflectionClient());
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task CompileAsync_NullModule_Throws()
    {
        var gepa = new GEPA(new FakeReflectionClient());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => gepa.CompileAsync<LmpModule>(null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullTrainSet_Throws()
    {
        var gepa = new GEPA(new FakeReflectionClient());
        var module = new GEPATestModule(new FakeTaskClient());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => gepa.CompileAsync(module, null!, (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainSet_ReturnsModule()
    {
        var gepa = new GEPA(new FakeReflectionClient());
        var module = new GEPATestModule(new FakeTaskClient());
        var result = await gepa.CompileAsync(module, [], (_, _) => 1f);
        Assert.Same(module, result);
    }

    // ── Optimization Behavior ───────────────────────────────────

    [Fact]
    public async Task CompileAsync_ImprovesInstructions()
    {
        // The reflection client always suggests "Improved: ..."
        var reflectionClient = new FakeReflectionClient();
        var taskClient = new FakeTaskClient();

        var gepa = new GEPA(
            reflectionClient,
            maxIterations: 5,
            miniBatchSize: 3,
            mergeEvery: 100, // no merge in this test
            seed: 42);

        var module = new GEPATestModule(taskClient);
        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"ticket {i}"),
                new GEPAOutput($"reply {i}")))
            .ToList();

        // Metric: improved outputs score higher (enables gate check to pass)
        Func<Example, object, float> metric = (ex, output) =>
            output is GEPAOutput o && o.Reply.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.8f : 0.3f;

        var result = await gepa.CompileAsync(module, trainSet, metric);
        Assert.NotNull(result);

        // After iterations with reflection, instructions should have been modified
        var predictors = result.GetPredictors();
        Assert.NotEmpty(predictors);

        // The best candidate should have improved instructions
        var instruction = predictors.First().Predictor.Instructions;
        Assert.Contains("Improved", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompileAsync_PerfectScores_NoReflection()
    {
        var reflectionClient = new FakeReflectionClient();
        var taskClient = new FakeTaskClient();

        var gepa = new GEPA(
            reflectionClient,
            maxIterations: 3,
            miniBatchSize: 3,
            seed: 42);

        var module = new GEPATestModule(taskClient);
        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"ticket {i}"),
                new GEPAOutput($"reply {i}")))
            .ToList();

        // Perfect scores → no reflection needed
        Func<Example, object, float> metric = (_, _) => 1.0f;

        var result = await gepa.CompileAsync(module, trainSet, metric);
        Assert.NotNull(result);

        // Original instruction should be preserved when nothing is failing
        var pred = result.GetPredictors().First();
        Assert.Equal("Process the input", pred.Predictor.Instructions);
    }

    [Fact]
    public async Task CompileAsync_WithMerge_CompletesSuccessfully()
    {
        var reflectionClient = new FakeReflectionClient();
        var taskClient = new FakeTaskClient();

        var gepa = new GEPA(
            reflectionClient,
            maxIterations: 12,
            miniBatchSize: 3,
            mergeEvery: 3,
            seed: 42);

        var module = new GEPATestModule(taskClient);
        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"ticket {i}"),
                new GEPAOutput($"reply {i}")))
            .ToList();

        // Instruction-sensitive metric so gate check can pass
        Func<Example, object, float> metric = (_, output) =>
            output is GEPAOutput o && o.Reply.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.8f : 0.3f;

        var result = await gepa.CompileAsync(module, trainSet, metric);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CompileAsync_Deterministic_WithSeed()
    {
        var trainSet = Enumerable.Range(0, 8)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"ticket {i}"),
                new GEPAOutput($"reply {i}")))
            .ToList();

        Func<Example, object, float> metric = (_, _) => 0.4f;

        async Task<string> RunOnce()
        {
            var gepa = new GEPA(
                new FakeReflectionClient(),
                maxIterations: 5,
                miniBatchSize: 3,
                seed: 99);

            var module = new GEPATestModule(new FakeTaskClient());
            var result = await gepa.CompileAsync(module, trainSet, metric);
            return result.GetPredictors().First().Predictor.Instructions;
        }

        var instr1 = await RunOnce();
        var instr2 = await RunOnce();
        Assert.Equal(instr1, instr2);
    }

    [Fact]
    public async Task CompileAsync_SupportsCancellation()
    {
        var gepa = new GEPA(
            new FakeReflectionClient(),
            maxIterations: 100,
            seed: 42);

        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"ticket {i}"),
                new GEPAOutput($"reply {i}")))
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gepa.CompileAsync(module, trainSet, (_, _) => 0.5f, options: CompileOptions.RuntimeOnly, cts.Token));
    }

    // ── Trajectory-Aware Tests (Phase L.1) ─────────────────────

    [Fact]
    public async Task OptimizeAsync_NoTrajectoryMetric_NoTrajectoryTrials()
    {
        var gepa = new GEPA(new FakeReflectionClient(), maxIterations: 2, seed: 42);
        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        var ctx = OptimizationContext.For(module, trainSet, (_, _) => 0.5f);

        await gepa.OptimizeAsync(ctx);

        var trajectoryTrials = ctx.TrialHistory.Trials
            .Where(t => t.Notes?.StartsWith("GEPA:trajectory") == true).ToList();
        Assert.Empty(trajectoryTrials);
    }

    [Fact]
    public async Task OptimizeAsync_WithTrajectoryMetric_AddsTrajectoryTrials()
    {
        var gepa = new GEPA(new FakeReflectionClient(), maxIterations: 2, seed: 42);
        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        var ctx = OptimizationContext.For(module, trainSet, (_, _) => 0.5f);
        ctx.TrajectoryMetric = new FakeTrajectoryMetric(0.7f);

        await gepa.OptimizeAsync(ctx);

        var trajectoryTrials = ctx.TrialHistory.Trials
            .Where(t => t.Notes?.StartsWith("GEPA:trajectory") == true).ToList();
        Assert.NotEmpty(trajectoryTrials);
        Assert.All(trajectoryTrials, t => Assert.Equal(0.7f, t.Score));
    }

    [Fact]
    public async Task OptimizeAsync_WithTrajectoryMetric_AddsObservationsToReflectionLog()
    {
        var gepa = new GEPA(new FakeReflectionClient(), maxIterations: 1, seed: 42);
        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        var reflectionLog = new ReflectionLog();
        var ctx = OptimizationContext.For(module, trainSet, (_, _) => 0.5f);
        ctx.TrajectoryMetric = new FakeTrajectoryMetric(0.8f);
        ctx.ReflectionLog = reflectionLog;

        await gepa.OptimizeAsync(ctx);

        // Trajectory observations should have been added to the reflection log.
        Assert.True(reflectionLog.Count > 0);
        Assert.Contains(reflectionLog.Entries, e => e.Source == nameof(GEPA));
    }
}

// ── Test Infrastructure ─────────────────────────────────────

file record GEPAInput(string Text);
file record GEPAOutput(string Reply);

/// <summary>
/// Trajectory metric that always returns a fixed score for testing.
/// </summary>
file sealed class FakeTrajectoryMetric(float score) : ITrajectoryMetric
{
    public ValueTask<float> ScoreAsync(Trajectory trajectory, CancellationToken ct = default)
        => ValueTask.FromResult(score);
}

/// <summary>
/// Reflection LLM that always proposes "Improved: {original instruction}".
/// </summary>
file sealed class FakeReflectionClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant,
                "Improved: Analyze the input carefully and produce the expected output.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Task LLM that returns a simple JSON output for the test module.
/// </summary>
file sealed class FakeTaskClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, """{"reply":"Generated reply"}""")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Simple module with one predictor for GEPA tests.
/// </summary>
file class GEPATestModule : LmpModule<GEPAInput, GEPAOutput>
{
    private readonly FakeGEPAPredictor _processor;

    public GEPATestModule(IChatClient client)
    {
        _processor = new FakeGEPAPredictor(client) { Name = "processor" };
    }

    private GEPATestModule(FakeGEPAPredictor processor)
    {
        _processor = processor;
    }

    public override async Task<GEPAOutput> ForwardAsync(GEPAInput input, CancellationToken ct = default)
    {
        var result = _processor.Predict(input, Trace);
        return await Task.FromResult((GEPAOutput)result);
    }

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [(Name: _processor.Name, Predictor: _processor)];

    protected override LmpModule CloneCore()
    {
        var clonedProcessor = (FakeGEPAPredictor)_processor.Clone();
        return new GEPATestModule(clonedProcessor);
    }
}

file sealed class FakeGEPAPredictor : IPredictor
{
    private readonly IChatClient _client;

    public FakeGEPAPredictor(IChatClient client) => _client = client;

    public string Name { get; set; } = "processor";
    public string Instructions { get; set; } = "Process the input";
    public List<(object Input, object Output)> TypedDemos { get; set; } = [];
    IList IPredictor.Demos => TypedDemos;
    public ChatOptions Config { get; set; } = new();

    public object Predict(object input, Trace? trace)
    {
        // Output varies based on instruction content — enables testing of gate check
        var reply = Instructions.Contains("Improved", StringComparison.OrdinalIgnoreCase)
            ? "Improved reply"
            : "Generated reply";
        var output = new GEPAOutput(reply);
        trace?.Record(Name, input, output);
        return output;
    }

    public void AddDemo(object input, object output) =>
        TypedDemos.Add((input, output));

    public PredictorState GetState() => new()
    {
        Instructions = Instructions,
        Demos = []
    };

    public void LoadState(PredictorState state) =>
        Instructions = state.Instructions;

    public IPredictor Clone()
    {
        var clone = new FakeGEPAPredictor(_client)
        {
            Name = Name,
            Instructions = Instructions,
            Config = Config
        };
        foreach (var demo in TypedDemos)
            clone.TypedDemos.Add(demo);
        return clone;
    }
}

