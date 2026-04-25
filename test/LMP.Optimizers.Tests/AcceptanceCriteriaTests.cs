using LMP.Optimizers;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Acceptance criteria tests verifying the core invariants from the unified optimization design.
///
/// These tests use fake clients and targets — no real LLM calls are made.
/// </summary>
public sealed class AcceptanceCriteriaTests
{
    // ── 1. Invariant: Auto() is reproducible from Tier 2 .Use() calls ──────────

    [Fact]
    public void Invariant_AutoAccuracy_StepTypesMatchManualConstruction()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var client = new AcceptanceFakeClient();

        // Tier 4 one-liner
        var autoPipeline = LmpPipelines.Auto(module, client, Goal.Accuracy);

        // Tier 2 manual construction — exactly the same steps
        var manualPipeline = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())
            .Use(new GEPA(client))
            .Use(new MIPROv2(client))
            .Use(new BayesianCalibration());

        // Step count must match
        Assert.Equal(manualPipeline.Steps.Count, autoPipeline.Steps.Count);

        // Step types must match in order
        for (int i = 0; i < autoPipeline.Steps.Count; i++)
            Assert.Equal(manualPipeline.Steps[i].GetType(), autoPipeline.Steps[i].GetType());
    }

    [Fact]
    public void Invariant_AutoBalanced_StepTypesMatchManualConstruction()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var client = new AcceptanceFakeClient();

        var autoPipeline = LmpPipelines.Auto(module, client, Goal.Balanced);
        var manualPipeline = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())
            .Use(new GEPA(client))
            .Use(new BayesianCalibration());

        Assert.Equal(manualPipeline.Steps.Count, autoPipeline.Steps.Count);
        for (int i = 0; i < autoPipeline.Steps.Count; i++)
            Assert.Equal(manualPipeline.Steps[i].GetType(), autoPipeline.Steps[i].GetType());
    }

    // ── 2. Novice: empty train set is a safe no-op ─────────────────────────────

    [Fact]
    public async Task Novice_EmptyTrainSet_DoesNotThrow()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var pipeline = LmpPipelines.Auto(module, new AcceptanceFakeClient(), Goal.Accuracy);

        // Empty train set — pipeline must be invocable without throwing
        var result = await pipeline.OptimizeAsync([], [], (_, _) => 1f);

        Assert.NotNull(result);
    }

    // ── 3. Practitioner: swapping SIMBA for MIPROv2 requires no other changes ──

    [Fact]
    public async Task Practitioner_SwapMIPROv2ForSIMBA_BuildsAndRuns()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var client = new AcceptanceFakeClient();
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<AccInput, AccOutput>(
                new AccInput($"q{i}"), new AccOutput($"a{i}")))
            .ToList();

        // Original: BFS → MIPROv2
        var withMIPROv2 = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())
            .Use(new MIPROv2(client));

        // Swapped: BFS → SIMBA — no changes needed to BFS step or pipeline builder
        var withSIMBA = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())
            .Use(new SIMBA(client));

        // Both must run without error
        await withMIPROv2.OptimizeAsync(trainSet, null, (_, _) => 0.5f);
        await withSIMBA.OptimizeAsync(trainSet, null, (_, _) => 0.5f);
    }

    // ── 4. Researcher: custom IOptimizer drops into any pipeline ──────────────

    [Fact]
    public async Task Researcher_CustomOptimizer_IsCalledDuringOptimizeAsync()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var counter = new CountingOptimizer();

        var pipeline = module.AsOptimizationPipeline()
            .Use(counter);

        await pipeline.OptimizeAsync([], [], (_, _) => 1f);

        Assert.Equal(1, counter.CallCount);
    }

    [Fact]
    public async Task Researcher_CustomOptimizer_CanBeChainedWithBuiltIn()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var counter = new CountingOptimizer();
        var trainSet = Enumerable.Range(0, 2)
            .Select(i => (Example)new Example<AccInput, AccOutput>(
                new AccInput($"q{i}"), new AccOutput($"a{i}")))
            .ToList();

        var pipeline = module.AsOptimizationPipeline()
            .Use(new BootstrapFewShot())  // built-in
            .Use(counter);               // custom

        await pipeline.OptimizeAsync(trainSet, null, (_, _) => 0.5f);

        Assert.Equal(1, counter.CallCount);
    }

    // ── 5. BayesianCalibration: no-op for ModuleTarget ───────────────────────

    [Fact]
    public async Task BayesianCalibration_ModuleTarget_LeavesTrialHistoryEmpty()
    {
        var module = new AcceptanceTestModule(new AcceptanceFakeClient());
        var bc = new BayesianCalibration();
        var trainSet = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<AccInput, AccOutput>(
                new AccInput($"q{i}"), new AccOutput($"a{i}")))
            .ToList();
        var ctx = new OptimizationContext { Target = module, TrainSet = trainSet, Metric = (_, _) => 0.5f };

        await bc.OptimizeAsync(ctx);

        // ModuleTarget has empty TypedParameterSpace — BayesianCalibration adds no trials
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    // ── 6. UseLmpTrace: MEAI middleware wires trace into client ──────────────

    [Fact]
    public void UseLmpTrace_Builder_RegistersMiddleware()
    {
        var trace = new Trace();
        var fakeClient = new AcceptanceFakeClient();
        var builder = new ChatClientBuilder(fakeClient);

        var augmented = builder.UseLmpTrace(trace);

        // The method must return the builder (for chaining) — fluent API guarantee
        Assert.Same(builder, augmented);
    }

    [Fact]
    public void UseLmpTrace_NullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((ChatClientBuilder)null!).UseLmpTrace(new Trace()));
    }

    [Fact]
    public void UseLmpTrace_NullTrace_Throws()
    {
        var builder = new ChatClientBuilder(new AcceptanceFakeClient());
        Assert.Throws<ArgumentNullException>(() =>
            builder.UseLmpTrace(null!));
    }
}

// ── Test Infrastructure ─────────────────────────────────────

file record AccInput(string Question);
file record AccOutput(string Answer);

/// <summary>
/// Simple module for acceptance tests.
/// </summary>
file class AcceptanceTestModule : LmpModule<AccInput, AccOutput>
{
    private readonly AcceptanceFakePredictor _predictor;

    public AcceptanceTestModule(IChatClient client)
    {
        _predictor = new AcceptanceFakePredictor(client) { Name = "answerer" };
    }

    private AcceptanceTestModule(AcceptanceFakePredictor predictor)
    {
        _predictor = predictor;
    }

    public override Task<AccOutput> ForwardAsync(AccInput input, CancellationToken ct = default)
    {
        var output = (AccOutput)_predictor.Predict(input, Trace);
        return Task.FromResult(output);
    }

    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [(Name: _predictor.Name, Predictor: _predictor)];

    protected override LmpModule CloneCore()
    {
        var cloned = (AcceptanceFakePredictor)_predictor.Clone();
        return new AcceptanceTestModule(cloned);
    }
}

file sealed class AcceptanceFakePredictor : IPredictor, IOptimizationTarget
{
    private readonly IChatClient _client;

    public AcceptanceFakePredictor(IChatClient client) => _client = client;

    public string Name { get; set; } = "answerer";
    public string Instructions { get; set; } = "Answer accurately.";
    public List<(object Input, object Output)> TypedDemos { get; set; } = [];
    System.Collections.IList IPredictor.Demos => TypedDemos;
    public ChatOptions Config { get; set; } = new();

    public object Predict(object input, Trace? trace)
    {
        var output = new AccOutput("answer");
        trace?.Record(Name, input, output);
        return output;
    }

    public void AddDemo(object input, object output) => TypedDemos.Add((input, output));
    public PredictorState GetState() => new() { Instructions = Instructions, Demos = [] };
    public void LoadState(PredictorState state) => Instructions = state.Instructions;

    public IPredictor Clone()
    {
        var c = new AcceptanceFakePredictor(_client) { Name = Name, Instructions = Instructions, Config = Config };
        foreach (var d in TypedDemos) c.TypedDemos.Add(d);
        return c;
    }

    // ── IOptimizationTarget implementation (required for MIPROv2 seam) ─────

    TargetShape IOptimizationTarget.Shape => TargetShape.SingleTurn;

    Task<(object Output, Trace Trace)> IOptimizationTarget.ExecuteAsync(
        object input, CancellationToken ct)
    {
        var trace = new Trace();
        var output = new AccOutput("answer");
        trace.Record(Name, input, output);
        return Task.FromResult<(object, Trace)>((output, trace));
    }

    TypedParameterSpace IOptimizationTarget.GetParameterSpace()
        => TypedParameterSpace.Empty
            .Add("instructions", new StringValued(Instructions))
            .Add("demos", new Subset([.. TypedDemos.Cast<object>()], 0, TypedDemos.Count));

    TargetState IOptimizationTarget.GetState() => TargetState.From(GetState());

    void IOptimizationTarget.ApplyState(TargetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        LoadState(state.As<PredictorState>());
    }

    IOptimizationTarget IOptimizationTarget.WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var clone = (AcceptanceFakePredictor)Clone();
        foreach (var (k, v) in assignment.Values)
        {
            switch (k)
            {
                case "instructions":
                    clone.Instructions = (string)v;
                    break;
                case "demos":
                    if (v is not IReadOnlyList<object> list)
                        throw new ArgumentException(
                            $"AcceptanceFakePredictor.WithParameters: 'demos' expected IReadOnlyList<object>.",
                            nameof(assignment));
                    clone.TypedDemos.Clear();
                    foreach (var item in list)
                    {
                        if (item is ValueTuple<object, object> erased)
                            clone.TypedDemos.Add((erased.Item1, erased.Item2));
                    }
                    break;
                default:
                    throw new ArgumentException(
                        $"AcceptanceFakePredictor.WithParameters: unknown key '{k}'.",
                        nameof(assignment));
            }
        }
        return clone;
    }

    TService? IOptimizationTarget.GetService<TService>() where TService : class
        => this as TService;
}

/// <summary>
/// Fake chat client for acceptance tests — returns constant JSON responses.
/// </summary>
file sealed class AcceptanceFakeClient : IChatClient
{
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant,
                "Improved: Answer accurately and concisely.")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Minimal custom IOptimizer that simply increments a counter — the "researcher" test seam.
/// Shows any 10-line IOptimizer implementation drops into any pipeline without framework changes.
/// </summary>
file sealed class CountingOptimizer : IOptimizer
{
    public int CallCount { get; private set; }

    public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}
