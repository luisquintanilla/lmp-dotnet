using LMP;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests the chaining behavior exposed publicly via <see cref="OptimizationTargetExtensions.Then"/>
/// and <see cref="Pipeline{TIn, TOut}"/>. The underlying <c>ChainTarget</c> is an internal
/// composition primitive — tests exercise it through the public façade.
/// </summary>
public sealed class ChainTargetTests
{
    // ── Composition guards ────────────────────────────────────────────────

    [Fact]
    public void Then_NullFirst_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => OptimizationTargetExtensions.Then(null!, new StubTarget("a")));

    [Fact]
    public void Then_NullNext_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => new StubTarget("a").Then(null!));

    [Fact]
    public void Pipeline_AddNull_Throws()
    {
        var pipeline = new Pipeline<object, object>();
        Assert.Throws<ArgumentNullException>(() => pipeline.Add(null!));
    }

    // ── Shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_AllSingleTurn_IsSingleTurn()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b"));
        Assert.Equal(TargetShape.SingleTurn, chain.Shape);
    }

    [Fact]
    public void Shape_AnyMultiTurn_IsMultiTurn()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b", shape: TargetShape.MultiTurn));
        Assert.Equal(TargetShape.MultiTurn, chain.Shape);
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TwoTargets_OutputOfFirstIsInputOfSecond()
    {
        var first = new StubTarget("intermediate");
        var second = new CaptureInputTarget("final");
        var chain = first.Then(second);

        var (output, _) = await chain.ExecuteAsync("original");

        Assert.Equal("intermediate", second.ReceivedInput);
        Assert.Equal("final", output);
    }

    [Fact]
    public async Task ExecuteAsync_SingleStagePipeline_PassesThroughOutput()
    {
        var pipeline = new Pipeline<object, object> { new StubTarget("result") };

        var (output, _) = await pipeline.ExecuteAsync("input");

        Assert.Equal("result", output);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeTargets_ChainsProperly()
    {
        var t1 = new TransformTarget(s => $"{s}-A");
        var t2 = new TransformTarget(s => $"{s}-B");
        var t3 = new TransformTarget(s => $"{s}-C");
        var chain = t1.Then(t2).Then(t3);

        var (output, _) = await chain.ExecuteAsync("start");

        Assert.Equal("start-A-B-C", output);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var pipeline = new Pipeline<object, object> { new StubTarget("x") };
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ExecuteAsync(null!));
    }

    // ── Trace merging ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CombinesTraceEntries()
    {
        var usage1 = new UsageDetails { TotalTokenCount = 10 };
        var usage2 = new UsageDetails { TotalTokenCount = 20 };
        var t1 = new TracingTarget("pred1", "out1", usage1);
        var t2 = new TracingTarget("pred2", "out2", usage2);
        var chain = t1.Then(t2);

        var (_, trace) = await chain.ExecuteAsync("input");

        Assert.Equal(2, trace.Entries.Count);
        Assert.Equal("pred1", trace.Entries[0].PredictorName);
        Assert.Equal("pred2", trace.Entries[1].PredictorName);
        Assert.Equal(30, trace.TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyChildTraces_MergedTraceIsEmpty()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b"));
        var (_, trace) = await chain.ExecuteAsync("in");
        Assert.Empty(trace.Entries);
    }

    // ── Then flattens ─────────────────────────────────────────────────────

    [Fact]
    public void Then_FlattensExistingChain()
    {
        var t1 = new StubTarget("a");
        var t2 = new StubTarget("b");
        var t3 = new StubTarget("c");
        var chain = (ChainTarget)t1.Then(t2).Then(t3);

        // 3 sequential children, not nested
        Assert.Equal(3, chain.Targets.Count);
    }

    // ── GetParameterSpace ─────────────────────────────────────────────────

    [Fact]
    public void GetParameterSpace_NoChildHasParams_ReturnsEmpty()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b"));
        Assert.True(chain.GetParameterSpace().IsEmpty);
    }

    [Fact]
    public void GetParameterSpace_NamespacesChildParameters()
    {
        var t1 = new ParameterizedTarget(("temp", new Continuous(0.0, 2.0)));
        var t2 = new ParameterizedTarget(("model", new Categorical(3)));
        var chain = t1.Then(t2);

        var space = chain.GetParameterSpace();

        Assert.True(space.Parameters.ContainsKey("child_0.temp"));
        Assert.True(space.Parameters.ContainsKey("child_1.model"));
        Assert.IsType<Continuous>(space.Parameters["child_0.temp"]);
        Assert.IsType<Categorical>(space.Parameters["child_1.model"]);
    }

    [Fact]
    public void GetParameterSpace_SameChildParamNameInTwoChildren_BothNamespaced()
    {
        var t1 = new ParameterizedTarget(("prompt", new StringValued("a")));
        var t2 = new ParameterizedTarget(("prompt", new StringValued("b")));
        var chain = t1.Then(t2);

        var space = chain.GetParameterSpace();

        Assert.Equal(2, space.Parameters.Count);
        Assert.True(space.Parameters.ContainsKey("child_0.prompt"));
        Assert.True(space.Parameters.ContainsKey("child_1.prompt"));
    }

    // ── GetState / ApplyState ─────────────────────────────────────────────

    [Fact]
    public void GetState_ReturnsStateArrayOfCorrectLength()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b")).Then(new StubTarget("c"));
        var states = chain.GetState().As<TargetState[]>();
        Assert.Equal(3, states.Length);
    }

    [Fact]
    public void ApplyState_WrongLength_Throws()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b"));
        var badState = TargetState.From(new TargetState[] { new(new object(), typeof(object)) });
        Assert.Throws<ArgumentException>(() => chain.ApplyState(badState));
    }

    [Fact]
    public void ApplyState_NullState_Throws()
    {
        var pipeline = new Pipeline<object, object> { new StubTarget("a") };
        Assert.Throws<ArgumentNullException>(() => pipeline.ApplyState(null!));
    }

    [Fact]
    public void GetState_ApplyState_RoundTrip_RoutesToCorrectChild()
    {
        var stateful1 = new StatefulTarget("original-1");
        var stateful2 = new StatefulTarget("original-2");
        var chain = stateful1.Then(stateful2);

        // Modify both children
        stateful1.SetValue("modified-1");
        stateful2.SetValue("modified-2");

        // Round-trip via chain state
        var saved = chain.GetState();
        stateful1.SetValue("overwritten");
        stateful2.SetValue("overwritten");
        chain.ApplyState(saved);

        Assert.Equal("modified-1", stateful1.CurrentValue);
        Assert.Equal("modified-2", stateful2.CurrentValue);
    }

    // ── WithParameters ────────────────────────────────────────────────────

    [Fact]
    public void WithParameters_Null_Throws()
    {
        var pipeline = new Pipeline<object, object> { new StubTarget("a") };
        Assert.Throws<ArgumentNullException>(() => pipeline.WithParameters(null!));
    }

    [Fact]
    public void WithParameters_Empty_ClonesWithSameState()
    {
        var stateful = new StatefulTarget("value");
        var chain = (ChainTarget)stateful.Then(new StubTarget("trailing"));

        var cloned = (ChainTarget)chain.WithParameters(ParameterAssignment.Empty);

        Assert.NotSame(chain, cloned);
        Assert.Equal("value", ((StatefulTarget)cloned.Targets[0]).CurrentValue);
    }

    [Fact]
    public void WithParameters_RoutesParamToCorrectChild()
    {
        var t1 = new RecordingParameterTarget();
        var t2 = new RecordingParameterTarget();
        var chain = t1.Then(t2);

        var assignment = ParameterAssignment.Empty
            .With("child_0.foo", "val0")
            .With("child_1.bar", "val1");

        chain.WithParameters(assignment);

        Assert.True(t1.ReceivedAssignment.TryGet<string>("foo", out var v0));
        Assert.Equal("val0", v0);

        Assert.True(t2.ReceivedAssignment.TryGet<string>("bar", out var v1));
        Assert.Equal("val1", v1);
    }

    [Fact]
    public void WithParameters_UnrelatedKeys_Ignored()
    {
        var t = new RecordingParameterTarget();
        var pipeline = new Pipeline<object, object> { t };

        pipeline.WithParameters(ParameterAssignment.Empty.With("unrelated.key", "x"));

        Assert.True(t.ReceivedAssignment.IsEmpty);
    }

    // ── GetService ────────────────────────────────────────────────────────

    [Fact]
    public void GetService_ReturnsFirstMatchingService()
    {
        var svc = new MyService();
        var t1 = new ServiceTarget<MyService>(null);
        var t2 = new ServiceTarget<MyService>(svc);
        var chain = t1.Then(t2);

        Assert.Same(svc, chain.GetService<MyService>());
    }

    [Fact]
    public void GetService_NothingMatches_ReturnsNull()
    {
        var chain = new StubTarget("a").Then(new StubTarget("b"));
        Assert.Null(chain.GetService<MyService>());
    }

    // ── Targets property ──────────────────────────────────────────────────

    [Fact]
    public void Targets_ReturnsCorrectCount()
    {
        var chain = (ChainTarget)new StubTarget("a")
            .Then(new StubTarget("b"))
            .Then(new StubTarget("c"));
        Assert.Equal(3, chain.Targets.Count);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────

file sealed class StubTarget : IOptimizationTarget
{
    private readonly string _output;
    public TargetShape Shape { get; }

    public StubTarget(string output, TargetShape shape = TargetShape.SingleTurn)
    {
        _output = output;
        Shape = shape;
    }

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Task.FromResult((_output as object, new Trace()));

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class CaptureInputTarget : IOptimizationTarget
{
    private readonly string _output;
    public object? ReceivedInput { get; private set; }
    public TargetShape Shape => TargetShape.SingleTurn;

    public CaptureInputTarget(string output) => _output = output;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
    {
        ReceivedInput = input;
        return Task.FromResult((_output as object, new Trace()));
    }

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class TransformTarget : IOptimizationTarget
{
    private readonly Func<string, string> _transform;
    public TargetShape Shape => TargetShape.SingleTurn;

    public TransformTarget(Func<string, string> transform) => _transform = transform;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
    {
        var result = _transform(input.ToString()!);
        return Task.FromResult((result as object, new Trace()));
    }

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        => new TransformTarget(_transform);
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class TracingTarget : IOptimizationTarget
{
    private readonly string _predictorName;
    private readonly string _output;
    private readonly UsageDetails _usage;
    public TargetShape Shape => TargetShape.SingleTurn;

    public TracingTarget(string predictorName, string output, UsageDetails usage)
    {
        _predictorName = predictorName;
        _output = output;
        _usage = usage;
    }

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
    {
        var trace = new Trace();
        trace.Record(_predictorName, input, _output, _usage);
        return Task.FromResult((_output as object, trace));
    }

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class ParameterizedTarget : IOptimizationTarget
{
    private readonly (string Name, ParameterKind Kind)[] _params;
    public TargetShape Shape => TargetShape.SingleTurn;

    public ParameterizedTarget(params (string Name, ParameterKind Kind)[] parameters)
        => _params = parameters;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Task.FromResult(("out" as object, new Trace()));

    public TypedParameterSpace GetParameterSpace()
    {
        var space = TypedParameterSpace.Empty;
        foreach (var (name, kind) in _params)
            space = space.Add(name, kind);
        return space;
    }

    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class StatefulTarget : IOptimizationTarget
{
    private string _value;
    public TargetShape Shape => TargetShape.SingleTurn;
    public string CurrentValue => _value;

    public StatefulTarget(string initialValue) => _value = initialValue;
    public void SetValue(string value) => _value = value;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Task.FromResult((_value as object, new Trace()));

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(_value);
    public void ApplyState(TargetState state) => _value = state.As<string>();
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        => new StatefulTarget(_value);
    public TService? GetService<TService>() where TService : class => null;
}

file sealed class RecordingParameterTarget : IOptimizationTarget
{
    public ParameterAssignment ReceivedAssignment { get; private set; } = ParameterAssignment.Empty;
    public TargetShape Shape => TargetShape.SingleTurn;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Task.FromResult(("out" as object, new Trace()));

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }

    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        ReceivedAssignment = assignment;
        return this;
    }

    public TService? GetService<TService>() where TService : class => null;
}

file sealed class MyService { }

file sealed class ServiceTarget<T> : IOptimizationTarget where T : class
{
    private readonly T? _service;
    public TargetShape Shape => TargetShape.SingleTurn;

    public ServiceTarget(T? service) => _service = service;

    public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        => Task.FromResult(("out" as object, new Trace()));

    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
    public TargetState GetState() => TargetState.From(new object());
    public void ApplyState(TargetState state) { }
    public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
    public TService? GetService<TService>() where TService : class => _service as TService;
}
