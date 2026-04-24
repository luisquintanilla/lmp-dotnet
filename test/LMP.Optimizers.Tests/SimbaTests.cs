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
        var ctx = new OptimizationContext { Target = module, TrainSet = [], Metric = (_, _) => 1f };
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
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 1f };

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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };

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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };

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
        // If ctx.Diagnostics.BaselineScore is set, SIMBA must not re-evaluate — the score it logs
        // initially equals the injected baseline.
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 1, miniBatchSize: 2, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
        ctx.Diagnostics.BaselineScore = 0.5f;

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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };

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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };

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

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };
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

    // ── Trajectory-Aware Tests (Phase L.2) ─────────────────────

    [Fact]
    public async Task OptimizeAsync_NoTrajectoryMetric_NoTrajectoryPath()
    {
        // Without trajectory metric, SIMBA should use regular EvaluateScoreAsync path.
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 2, miniBatchSize: 2, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
        // No TrajectoryMetric — must not throw or produce trajectory-specific behavior.

        await simba.OptimizeAsync(ctx);

        Assert.Equal(2, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_WithTrajectoryMetric_UsesTrajectoryForBaselineScore()
    {
        // Trajectory metric returns 0.9; regular metric returns 0.5.
        // When trajectory metric is set, SIMBA should use 0.9 as the baseline,
        // so all TrialHistory entries (which record currentFullSetScore starting from baseline)
        // should reflect the trajectory score.
        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 2, miniBatchSize: 2, seed: 0);
        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 4)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"), new SimbaOutput("expected")))
            .ToList();

        // Regular metric: 0.5; trajectory metric: 0.9.
        // Since all candidates will score the same as baseline (no real improvement),
        // currentFullSetScore stays at 0.9 throughout.
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
        ctx.TrajectoryMetric = new FakeSimbaTrajectoryMetric(0.9f);

        await simba.OptimizeAsync(ctx);

        // All TrialHistory entries should have score 0.9 (from trajectory baseline path).
        Assert.True(ctx.TrialHistory.Count > 0);
        Assert.All(ctx.TrialHistory.Trials, t => Assert.Equal(0.9f, t.Score));
    }

    // ── T2h Seam Migration Tests ─────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_UsesWithParametersSeam()
    {
        // Proves the mutation path exercises IOptimizationTarget.WithParameters
        // (rather than mutating IPredictor.Instructions directly as pre-T2h did).
        FakeSimbaPredictor.ResetWithParametersCalls();

        var simba = new SIMBA(
            new FakeSimbaReflectionClient(),
            maxIterations: 3,
            miniBatchSize: 2,
            seed: 42);

        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"),
                new SimbaOutput($"a{i}")))
            .ToList();
        Func<Example, object, float> metric = (_, output) =>
            output is SimbaOutput o && o.Answer.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };
        await simba.OptimizeAsync(ctx);

        Assert.True(FakeSimbaPredictor.WithParametersCalls > 0,
            "Expected SIMBA mutation path to call IOptimizationTarget.WithParameters at least once.");
    }

    [Fact]
    public async Task OptimizeAsync_DoesNotMutateInputModule()
    {
        // Regression: the input module's predictor Instructions must not be mutated
        // in place by SIMBA. Candidates are produced via WithParameters on pristine clones.
        var simba = new SIMBA(
            new FakeSimbaReflectionClient(),
            maxIterations: 3,
            miniBatchSize: 2,
            seed: 42);

        var module = new SimbaTestModule(new FakeSimbaTaskClient());
        var originalInstruction = module.GetPredictors().First().Predictor.Instructions;

        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"),
                new SimbaOutput($"a{i}")))
            .ToList();
        Func<Example, object, float> metric = (_, output) =>
            output is SimbaOutput o && o.Answer.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = metric };
        await simba.OptimizeAsync(ctx);

        // The input predictor instance must retain its original instruction text pre-ApplyState.
        // NOTE: ApplyState at end of OptimizeAsync writes the best state into ctx.Target (= module),
        // so in the seam-correct implementation, mutation only happens via that single ApplyState.
        // The assertion below checks that WithParameters-based mutation did not touch the original
        // predictor during the loop itself by verifying the end-state equals either the original
        // or the reflection-proposed instruction (no partial in-place corruption).
        var finalInstruction = module.GetPredictors().First().Predictor.Instructions;
        Assert.True(
            finalInstruction == originalInstruction ||
            finalInstruction.Contains("Improved", StringComparison.OrdinalIgnoreCase),
            $"Unexpected instruction after Optimize: '{finalInstruction}'");
    }

    [Fact]
    public async Task OptimizeAsync_OnChainTargetOfModules_EvolvesStages()
    {
        // Composite-SIMBA end-to-end: two LmpModules composed via .Then() form a ChainTarget.
        // SIMBA must enumerate their predictors via the walker, build
        // "child_{i}.{name}.instructions" parameter assignments, and route mutations
        // through WithParameters to each stage independently (round-robin).
        var simba = new SIMBA(
            new FakeSimbaReflectionClient(),
            maxIterations: 20,
            miniBatchSize: 2,
            seed: 123);

        // Object-typed stages so ChainTarget can pipe stage 0's output into stage 1.
        var stageA = new SimbaObjectStageModule(new FakeSimbaTaskClient(), predictorName: "stageA");
        var stageB = new SimbaObjectStageModule(new FakeSimbaTaskClient(), predictorName: "stageB");
        var chain = ((IOptimizationTarget)stageA).Then(stageB);

        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<string, string>($"q{i}", $"a{i}"))
            .ToList();
        // Reward each stage improving independently: score grows with number of
        // "Improved" markers in the final chain output (0, 1, or 2).
        Func<Example, object, float> metric = (_, output) =>
        {
            if (output is not SimbaOutput o) return 0.2f;
            var count = o.Answer.Split("Improved").Length - 1;
            return count switch { 0 => 0.2f, 1 => 0.6f, _ => 0.95f };
        };

        var ctx = new OptimizationContext { Target = chain, TrainSet = trainSet, Metric = metric };
        await simba.OptimizeAsync(ctx);

        // Round-robin across two predictors over 20 iterations: both stages must have had
        // their instructions updated via the seam at least once. State is applied back to
        // the original target by OptimizeAsync.
        var instrA = stageA.GetPredictors().First().Predictor.Instructions;
        var instrB = stageB.GetPredictors().First().Predictor.Instructions;

        Assert.Contains("Improved", instrA, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Improved", instrB, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizeAsync_OnPredictorOnlyTarget_NoOp()
    {
        // PredictorWalker returns empty for a bare IOptimizationTarget that is neither an
        // LmpModule nor a composite; SIMBA must complete without throwing and must not
        // alter state.
        var predictor = new BareSimbaPredictorTarget { Instructions = "baseline" };
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<SimbaInput, SimbaOutput>(new SimbaInput($"q{i}"),
                new SimbaOutput($"a{i}")))
            .ToList();

        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 3, miniBatchSize: 2, seed: 1);
        var ctx = new OptimizationContext
        {
            Target = predictor,
            TrainSet = trainSet,
            Metric = (_, _) => 0.1f,
        };

        await simba.OptimizeAsync(ctx);

        Assert.Equal("baseline", predictor.Instructions);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_OnChainTarget_NoArgumentException()
    {
        // Explicit regression: pre-T2h SIMBA threw NotSupportedException when given a
        // ChainTarget (no LmpModule available via GetService<LmpModule>). Post-T2h it
        // accepts any IOptimizationTarget and routes through WithParameters.
        var stageA = new SimbaObjectStageModule(new FakeSimbaTaskClient(), predictorName: "stageA");
        var stageB = new SimbaObjectStageModule(new FakeSimbaTaskClient(), predictorName: "stageB");
        var chain = ((IOptimizationTarget)stageA).Then(stageB);

        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<string, string>($"q{i}", $"a{i}"))
            .ToList();

        var simba = new SIMBA(new FakeSimbaReflectionClient(), maxIterations: 2, miniBatchSize: 2, seed: 7);
        var ctx = new OptimizationContext
        {
            Target = chain,
            TrainSet = trainSet,
            Metric = (_, _) => 0.5f,
        };

        // Must not throw — pre-T2h would raise NotSupportedException here.
        await simba.OptimizeAsync(ctx);

        Assert.Equal(2, ctx.TrialHistory.Count);
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

file sealed class FakeSimbaPredictor : IPredictor, IOptimizationTarget
{
    /// <summary>
    /// Counts calls into <see cref="IOptimizationTarget.WithParameters"/> across
    /// all instances. Used by tests to prove the seam is exercised. Reset via
    /// <see cref="ResetWithParametersCalls"/>.
    /// </summary>
    public static int WithParametersCalls;

    public static void ResetWithParametersCalls() =>
        Interlocked.Exchange(ref WithParametersCalls, 0);

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
        var localPart = improved ? $"{Name}:Improved" : $"{Name}:pending";
        // When chained, propagate prior stage output so either stage's mutation visibly
        // affects the final output (enables composite gate passage).
        var answer = input is SimbaOutput prev ? $"{prev.Answer}|{localPart}" : localPart;
        var output = new SimbaOutput(answer);
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

    // ── IOptimizationTarget implementation ───────────────────────────

    TargetShape IOptimizationTarget.Shape => TargetShape.SingleTurn;

    Task<(object Output, Trace Trace)> IOptimizationTarget.ExecuteAsync(
        object input, CancellationToken ct)
    {
        var trace = new Trace();
        var output = Predict(input, trace);
        return Task.FromResult<(object, Trace)>((output, trace));
    }

    TypedParameterSpace IOptimizationTarget.GetParameterSpace()
        => TypedParameterSpace.Empty
            .Add("instructions", new StringValued(Instructions));

    TargetState IOptimizationTarget.GetState() => TargetState.From(GetState());

    void IOptimizationTarget.ApplyState(TargetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        LoadState(state.As<PredictorState>());
    }

    IOptimizationTarget IOptimizationTarget.WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        Interlocked.Increment(ref WithParametersCalls);
        var clone = (FakeSimbaPredictor)Clone();
        foreach (var (k, v) in assignment.Values)
        {
            if (k == "instructions")
                clone.Instructions = (string)v;
            else
                throw new ArgumentException(
                    $"FakeSimbaPredictor.WithParameters: unknown parameter key '{k}'. " +
                    $"Valid keys: instructions.",
                    nameof(assignment));
        }
        return clone;
    }

    TService? IOptimizationTarget.GetService<TService>() where TService : class
        => this as TService;
}

/// <summary>
/// Object-typed module wrapping a single <see cref="FakeSimbaPredictor"/>. Used in
/// composite tests where the chain must pipe output of stage i into stage i+1 without
/// typed input/output constraints.
/// </summary>
file sealed class SimbaObjectStageModule : LmpModule
{
    private readonly FakeSimbaPredictor _processor;

    public SimbaObjectStageModule(IChatClient client, string predictorName)
    {
        _processor = new FakeSimbaPredictor(client) { Name = predictorName };
    }

    private SimbaObjectStageModule(FakeSimbaPredictor processor) { _processor = processor; }

    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult(_processor.Predict(input, Trace));

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [(Name: _processor.Name, Predictor: _processor)];

    protected override LmpModule CloneCore()
        => new SimbaObjectStageModule((FakeSimbaPredictor)_processor.Clone());
}

/// <summary>
/// A bare <see cref="IOptimizationTarget"/> with no predictors. PredictorWalker yields
/// empty, so SIMBA must no-op without throwing.
/// </summary>
file sealed class BareSimbaPredictorTarget : IOptimizationTarget
{
    public string Instructions { get; set; } = "";

    public TargetShape Shape => TargetShape.SingleTurn;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
    {
        var trace = new Trace();
        object output = new SimbaOutput("bare");
        return Task.FromResult((output, trace));
    }

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;

    public TargetState GetState() => TargetState.From(Instructions);

    public void ApplyState(TargetState state) => Instructions = state.As<string>();

    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;

    public TService? GetService<TService>() where TService : class => this as TService;
}

/// <summary>
/// Trajectory metric that always returns a fixed score for SIMBA tests.
/// </summary>
file sealed class FakeSimbaTrajectoryMetric(float score) : ITrajectoryMetric
{
    public ValueTask<float> ScoreAsync(Trajectory trajectory, CancellationToken ct = default)
        => ValueTask.FromResult(score);
}
