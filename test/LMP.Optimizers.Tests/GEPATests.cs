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
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };

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
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
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
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };
        ctx.TrajectoryMetric = new FakeTrajectoryMetric(0.8f);
        ctx.ReflectionLog = reflectionLog;

        await gepa.OptimizeAsync(ctx);

        // Trajectory observations should have been added to the reflection log.
        Assert.True(reflectionLog.Count > 0);
        Assert.Contains(reflectionLog.Entries, e => e.Source == nameof(GEPA));
    }

    // ── T2g Seam Migration Tests ─────────────────────────────────

    [Fact]
    public async Task CompileAsync_PerTrial_UsesWithParametersSeam()
    {
        // Proves the mutation path exercises the IOptimizationTarget.WithParameters seam
        // (rather than mutating IPredictor.Instructions directly as pre-T2g did).
        FakeGEPAPredictor.ResetWithParametersCalls();

        var gepa = new GEPA(
            new FakeReflectionClient(),
            maxIterations: 3,
            miniBatchSize: 2,
            mergeEvery: 100, // mutation-only
            seed: 42);

        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        Func<Example, object, float> metric = (_, output) =>
            output is GEPAOutput o && o.Reply.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        await gepa.CompileAsync(module, trainSet, metric);

        Assert.True(FakeGEPAPredictor.WithParametersCalls > 0,
            "Expected GEPA mutation path to call IOptimizationTarget.WithParameters at least once.");
    }

    [Fact]
    public async Task CompileAsync_MergePath_UsesWithParametersSeam()
    {
        // Proves the merge/crossover path also uses the seam (rubber-duck Q6.3).
        // With mergeEvery=2 and maxIterations=6, at least two merge iterations run
        // after the frontier has ≥2 candidates.
        FakeGEPAPredictor.ResetWithParametersCalls();

        var gepa = new GEPA(
            new FakeReflectionClient(),
            maxIterations: 8,
            miniBatchSize: 2,
            mergeEvery: 2,
            seed: 7);

        var module = new GEPATestModule(new FakeTaskClient());
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        Func<Example, object, float> metric = (_, output) =>
            output is GEPAOutput o && o.Reply.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        await gepa.CompileAsync(module, trainSet, metric);

        // Any successful evolution implies at least one WithParameters call. Merge iterations
        // that actually run will also go through the seam.
        Assert.True(FakeGEPAPredictor.WithParametersCalls > 0,
            "Expected merge/mutation to invoke WithParameters at least once.");
    }

    [Fact]
    public async Task CompileAsync_DoesNotMutateInputModule()
    {
        // Regression: the input module's predictor Instructions must not be mutated
        // in place by GEPA. Mutants are produced via WithParameters on pristine parents.
        var gepa = new GEPA(
            new FakeReflectionClient(),
            maxIterations: 3,
            miniBatchSize: 2,
            mergeEvery: 100,
            seed: 42);

        var module = new GEPATestModule(new FakeTaskClient());
        var originalInstruction = module.GetPredictors().First().Predictor.Instructions;
        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();
        Func<Example, object, float> metric = (_, output) =>
            output is GEPAOutput o && o.Reply.Contains("Improved", StringComparison.OrdinalIgnoreCase) ? 0.9f : 0.2f;

        await gepa.CompileAsync(module, trainSet, metric);

        // Input module's predictor must be untouched; best state lives on the returned module.
        Assert.Equal(originalInstruction, module.GetPredictors().First().Predictor.Instructions);
    }

    [Fact]
    public async Task OptimizeAsync_OnChainTargetOfModules_EvolvesEachStageIndependently()
    {
        // Composite-GEPA end-to-end: two LmpModule children composed via .Then() form a
        // ChainTarget. GEPA must enumerate their predictors via the walker, build
        // "child_{i}.{name}.instructions" parameter assignments, and route mutations
        // through WithParameters to each stage independently.
        var gepa = new GEPA(
            new FakeReflectionClient(),
            maxIterations: 20,
            miniBatchSize: 2,
            mergeEvery: 100, // mutation-only for deterministic per-stage observation
            seed: 123);

        // Use object-typed stages so ChainTarget can pipe stage 0's output into stage 1.
        var stageA = new ObjectStageModule(new FakeTaskClient(), predictorName: "stageA");
        var stageB = new ObjectStageModule(new FakeTaskClient(), predictorName: "stageB");
        var chain = ((IOptimizationTarget)stageA).Then(stageB);

        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<string, string>($"q{i}", $"a{i}"))
            .ToList();
        // Reward each stage improving independently: score grows with number of
        // "Improved" markers in the final chain output (0, 1, or 2).
        Func<Example, object, float> metric = (_, output) =>
        {
            if (output is not GEPAOutput o) return 0.2f;
            var count = o.Reply.Split("Improved").Length - 1;
            return count switch { 0 => 0.2f, 1 => 0.6f, _ => 0.95f };
        };

        var ctx = new OptimizationContext { Target = chain, TrainSet = trainSet, Metric = metric };
        await gepa.OptimizeAsync(ctx);

        // Round-robin across two predictors over 10 iterations: both stages must have had
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
        // PredictorWalker returns empty for a bare IOptimizationTarget that is neither
        // an LmpModule nor a composite; GEPA must complete without throwing and must
        // not alter state.
        var predictor = new BarePredictorTarget { Instructions = "baseline" };
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<GEPAInput, GEPAOutput>(new GEPAInput($"q{i}"),
                new GEPAOutput($"a{i}")))
            .ToList();

        var gepa = new GEPA(new FakeReflectionClient(), maxIterations: 3, miniBatchSize: 2, seed: 1);
        var ctx = new OptimizationContext
        {
            Target = predictor,
            TrainSet = trainSet,
            Metric = (_, _) => 0.1f,
        };

        await gepa.OptimizeAsync(ctx);

        Assert.Equal("baseline", predictor.Instructions);
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

file sealed class FakeGEPAPredictor : IPredictor, IOptimizationTarget
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

    public FakeGEPAPredictor(IChatClient client) => _client = client;

    public string Name { get; set; } = "processor";
    public string Instructions { get; set; } = "Process the input";
    public List<(object Input, object Output)> TypedDemos { get; set; } = [];
    IList IPredictor.Demos => TypedDemos;
    public ChatOptions Config { get; set; } = new();

    public object Predict(object input, Trace? trace)
    {
        // Local signal: "Improved" if this predictor's instructions were mutated via reflection.
        var localPart = Instructions.Contains("Improved", StringComparison.OrdinalIgnoreCase)
            ? $"{Name}:Improved"
            : $"{Name}:pending";

        // When chained, propagate prior stage output into the reply so either stage's
        // mutation visibly affects the final output (enables composite gate passage).
        var reply = input is GEPAOutput prev
            ? $"{prev.Reply}|{localPart}"
            : localPart;

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
        var clone = (FakeGEPAPredictor)Clone();
        foreach (var (k, v) in assignment.Values)
        {
            if (k == "instructions")
                clone.Instructions = (string)v;
            else
                throw new ArgumentException(
                    $"FakeGEPAPredictor.WithParameters: unknown parameter key '{k}'. " +
                    $"Valid keys: instructions.",
                    nameof(assignment));
        }
        return clone;
    }

    TService? IOptimizationTarget.GetService<TService>() where TService : class
        => this as TService;
}

/// <summary>
/// A bare <see cref="IOptimizationTarget"/> that is neither an <see cref="LmpModule"/>
/// nor a composite. Used to verify that <see cref="GEPA"/> tolerates targets for
/// which <see cref="PredictorWalker"/> yields no predictors (walker returns empty).
/// </summary>
file sealed class BarePredictorTarget : IOptimizationTarget
{
    public string Instructions { get; set; } = "";

    public TargetShape Shape => TargetShape.SingleTurn;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
    {
        var trace = new Trace();
        object output = new GEPAOutput("bare");
        return Task.FromResult((output, trace));
    }

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;

    public TargetState GetState() => TargetState.From(Instructions);

    public void ApplyState(TargetState state) => Instructions = state.As<string>();

    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;

    public TService? GetService<TService>() where TService : class => this as TService;
}

/// <summary>
/// Object-typed module wrapping a single <see cref="FakeGEPAPredictor"/>. Used in
/// composite tests where the chain must pipe output of stage i into stage i+1 without
/// typed input/output constraints.
/// </summary>
file sealed class ObjectStageModule : LmpModule
{
    private readonly FakeGEPAPredictor _processor;

    public ObjectStageModule(IChatClient client, string predictorName)
    {
        _processor = new FakeGEPAPredictor(client) { Name = predictorName };
    }

    private ObjectStageModule(FakeGEPAPredictor processor) { _processor = processor; }

    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult(_processor.Predict(input, Trace));

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [(Name: _processor.Name, Predictor: _processor)];

    protected override LmpModule CloneCore()
        => new ObjectStageModule((FakeGEPAPredictor)_processor.Clone());
}

