using System.Collections;
using LMP.Optimizers;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public sealed class SimbaTests
{
    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SIMBA(reflectionClient: null!));
    }

    [Fact]
    public void Constructor_ZeroIterations_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 0));
    }

    [Fact]
    public void Constructor_ZeroMiniBatchSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SIMBA(new FakeSimbaReflectionClient(), miniBatchSize: 0));
    }

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 10, miniBatchSize: 3);
        Assert.NotNull(simba);
    }

    // ── IOptimizer Contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new SIMBA(new FakeSimbaReflectionClient());
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => simba.OptimizeAsync(null!));
    }

    [Fact]
    public async Task OptimizeAsync_EmptyTrainSet_IsNoOp()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 5);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var ctx = OptimizationContext.For(ModuleTarget.For(module), [], (_, _) => 1f);
        var originalInstruction = module.GetPredictors().First().Predictor.Instructions;

        await simba.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
        Assert.Equal(originalInstruction, module.GetPredictors().First().Predictor.Instructions);
    }

    [Fact]
    public async Task OptimizeAsync_ZeroPredictors_IsNoOp()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 5);
        var module = new SimbaEmptyModule();
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("a")))
            .ToList();
        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, (_, _) => 1f);

        await simba.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    // ── Optimization Behavior ───────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_LogsTrialsToCtx()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 4, miniBatchSize: 2, seed: 1);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        // Use a metric that rewards "Improved" in output so acceptance can happen.
        Func<Example, object, float> metric = (_, output) =>
            output is SimbaOutput o && o.Answer.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, metric);

        await simba.OptimizeAsync(ctx);

        // One trial logged per iteration.
        Assert.Equal(4, ctx.TrialHistory.Count);
        Assert.All(ctx.TrialHistory.Trials, t => Assert.InRange(t.Score, 0f, 1f));
        Assert.All(ctx.TrialHistory.Trials, t => Assert.Contains("SIMBA iter", t.Notes));
    }

    [Fact]
    public async Task OptimizeAsync_ImprovesInstructions()
    {
        var reflectionClient = new FakeSimbaReflectionClient();
        var taskClient = new FakeSimbaTaskClient();
        var simba = new SIMBA(reflectionClient, maxIterations: 8, miniBatchSize: 2, seed: 7);

        var module = new SimbaTestModule(taskClient);
        var trainSet = Enumerable.Range(0, 8)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        Func<Example, object, float> metric = (_, output) =>
            output is SimbaOutput o && o.Answer.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, metric);

        await simba.OptimizeAsync(ctx);

        // The module state should have been updated with the best state found.
        var finalState = ctx.Target.GetState();
        Assert.NotNull(finalState);

        // Re-load state onto the module and verify instructions changed.
        module.ApplyState(finalState.As<ModuleState>());
        var instruction = module.GetPredictors().First().Predictor.Instructions;
        Assert.Contains("Improved", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizeAsync_UsesPipelineBaselineFromBag()
    {
        // If ctx.Bag["baseline"] is set, SIMBA must not re-evaluate — the score it logs
        // initially equals the injected baseline.
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 1, miniBatchSize: 2, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, (_, _) => 0.5f);
        ctx.Bag["baseline"] = (object)0.5f;

        await simba.OptimizeAsync(ctx);

        Assert.Equal(1, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_BudgetExhausted_StopsEarly()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 100, miniBatchSize: 2, seed: 42);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, (_, _) => 0.5f);
        ctx.Budget = new CostBudget.Builder().MaxTurns(3).Build();

        await simba.OptimizeAsync(ctx);

        // Should stop at or just after budget is exhausted (≤ maxTurns + 1 is acceptable due to
        // budget being checked at iteration start, not at the end of the update step).
        Assert.True(ctx.TrialHistory.Count <= 4,
            $"Expected ≤4 trials but got {ctx.TrialHistory.Count}");
    }

    [Fact]
    public async Task OptimizeAsync_Cancellation_Throws()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 100, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, (_, _) => 0.5f);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => simba.OptimizeAsync(ctx, cts.Token));
    }

    [Fact]
    public async Task OptimizeAsync_ReportsProgress()
    {
        var reports = new List<SimbaProgressReport>();
        var progress = new Progress<SimbaProgressReport>(r => reports.Add(r));

        var simba = new SIMBA(
            new FakeSimbaReflectionClient(),
            maxIterations: 3,
            miniBatchSize: 2,
            seed: 0,
            progress: progress);

        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, (_, _) => 0.5f);

        await simba.OptimizeAsync(ctx);

        // Progress.Report is async-fire — give it a tick to flush.
        await Task.Delay(50);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.InRange(r.Iteration, 1, 3));
        Assert.All(reports, r => Assert.Equal(3, r.MaxIterations));
    }

    [Fact]
    public async Task OptimizeAsync_AppliesOptimizedStateToTarget()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 5, miniBatchSize: 2, seed: 3);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        Func<Example, object, float> metric = (_, output) =>
            output is SimbaOutput o && o.Answer.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.95f : 0.1f;

        var ctx = OptimizationContext.For(ModuleTarget.For(module), trainSet, metric);
        var target = ctx.Target;

        await simba.OptimizeAsync(ctx);

        // The target must have state applied (not just left as baseline module).
        var state = target.GetState();
        Assert.NotNull(state);
    }

    // ── Backward Compat via CompileAsync extension ──────────────

    [Fact]
    public async Task CompileAsync_Extension_Works()
    {
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 3, miniBatchSize: 2, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

#pragma warning disable CS0618
        var result = await simba.CompileAsync(module, trainSet, (_, _) => 0.5f);
#pragma warning restore CS0618
        Assert.NotNull(result);
        Assert.IsAssignableFrom<SimbaTestModule>(result);
    }
}

// ── Test Infrastructure ─────────────────────────────────────

file record SimbaInput(string Question);
file record SimbaOutput(string Answer);

/// <summary>
/// Reflection LLM that always proposes "Improved: {original instruction}".
/// </summary>
file sealed class FakeSimbaReflectionClient : IChatClient
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
                "Improved: Answer the question accurately.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Task LLM that returns output sensitive to the predictor's instruction content.
/// When instructions contain "Improved", returns an improved answer.
/// </summary>
file sealed class FakeSimbaTaskClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var hasImproved = messages.Any(m =>
            m.Text?.Contains("Improved", StringComparison.OrdinalIgnoreCase) == true);

        var json = hasImproved
            ? """{"answer":"Improved answer"}"""
            : """{"answer":"Basic answer"}""";

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Module with one predictor for SIMBA tests.
/// </summary>
file class SimbaTestModule : LmpModule<SimbaInput, SimbaOutput>
{
    private readonly FakeSimbaPredictor _predictor;

    public SimbaTestModule(IChatClient client)
    {
        _predictor = new FakeSimbaPredictor(client) { Name = "answerer" };
    }

    private SimbaTestModule(FakeSimbaPredictor predictor)
    {
        _predictor = predictor;
    }

    public override Task<SimbaOutput> ForwardAsync(SimbaInput input, CancellationToken ct = default)
    {
        var result = (SimbaOutput)_predictor.Predict(input, Trace);
        return Task.FromResult(result);
    }

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [(Name: _predictor.Name, Predictor: _predictor)];

    protected override LmpModule CloneCore()
    {
        var cloned = (FakeSimbaPredictor)_predictor.Clone();
        return new SimbaTestModule(cloned);
    }
}

/// <summary>
/// Module with NO predictors — used to test the zero-predictor guard.
/// </summary>
file class SimbaEmptyModule : LmpModule<SimbaInput, SimbaOutput>
{
    public override Task<SimbaOutput> ForwardAsync(SimbaInput input, CancellationToken ct = default)
        => Task.FromResult(new SimbaOutput("empty"));

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => [];

    protected override LmpModule CloneCore() => new SimbaEmptyModule();
}

file sealed class FakeSimbaPredictor : IPredictor
{
    private readonly IChatClient _client;

    public FakeSimbaPredictor(IChatClient client) => _client = client;

    public string Name { get; set; } = "answerer";
    public string Instructions { get; set; } = "Answer the question";
    public List<(object Input, object Output)> TypedDemos { get; set; } = [];
    IList IPredictor.Demos => TypedDemos;
    public ChatOptions Config { get; set; } = new();

    public object Predict(object input, Trace? trace)
    {
        var improved = Instructions.Contains("Improved", StringComparison.OrdinalIgnoreCase);
        var output = new SimbaOutput(improved ? "Improved answer" : "Basic answer");
        trace?.Record(Name, input, output);
        return output;
    }

    public void AddDemo(object input, object output) => TypedDemos.Add((input, output));

    public PredictorState GetState() => new() { Instructions = Instructions, Demos = [] };

    public void LoadState(PredictorState state) => Instructions = state.Instructions;

    public IPredictor Clone()
    {
        var clone = new FakeSimbaPredictor(_client)
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
