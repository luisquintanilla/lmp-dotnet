using System.Collections;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// T2b tests: verifies <see cref="LmpModule"/> delegates fractally to its child
/// predictors for <see cref="IOptimizationTarget.GetParameterSpace"/> and
/// <see cref="IOptimizationTarget.WithParameters"/>.
/// </summary>
public sealed class LmpModuleAsTargetTests
{
    // ── Test fixtures ──────────────────────────────────────────────────

    /// <summary>Module with no predictors (matches T1 baseline).</summary>
    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore() => new EchoModule();
    }

    /// <summary>
    /// Minimal IPredictor + IOptimizationTarget stub used to exercise fractal
    /// delegation without pulling <c>LMP.Core.Predictor&lt;TIn,TOut&gt;</c> into
    /// this test project.
    /// </summary>
    private sealed class StubOptimizablePredictor : IPredictor, IOptimizationTarget
    {
        private readonly List<object> _demos = [];

        public string Name { get; set; } = "";
        public string Instructions { get; set; } = "";
        public IList Demos => _demos;
        public ChatOptions Config { get; set; } = new();

        public PredictorState GetState() => new()
        {
            Instructions = Instructions,
            Demos = [],
            Config = null
        };

        public void LoadState(PredictorState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            Instructions = state.Instructions;
        }

        public void AddDemo(object input, object output) => _demos.Add((input, output));

        public IPredictor Clone() => new StubOptimizablePredictor
        {
            Name = Name,
            Instructions = Instructions,
        };

        TargetShape IOptimizationTarget.Shape => TargetShape.SingleTurn;

        Task<(object Output, Trace Trace)> IOptimizationTarget.ExecuteAsync(
            object input, CancellationToken ct)
            => Task.FromResult<(object, Trace)>((input, new Trace()));

        TypedParameterSpace IOptimizationTarget.GetParameterSpace()
            => TypedParameterSpace.Empty
                .Add("instructions", new StringValued(Instructions))
                .Add("demos", new Subset([], 0, 0));

        TargetState IOptimizationTarget.GetState() => TargetState.From(GetState());

        void IOptimizationTarget.ApplyState(TargetState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            LoadState(state.As<PredictorState>());
        }

        IOptimizationTarget IOptimizationTarget.WithParameters(ParameterAssignment assignment)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            var clone = (StubOptimizablePredictor)Clone();
            foreach (var (k, v) in assignment.Values)
            {
                switch (k)
                {
                    case "instructions":
                        clone.Instructions = (string)v;
                        break;
                    case "demos":
                        // no-op in the stub
                        break;
                    default:
                        throw new ArgumentException(
                            $"StubOptimizablePredictor.WithParameters: unknown parameter key '{k}'. "
                          + $"Valid keys: instructions, demos.",
                            nameof(assignment));
                }
            }
            return clone;
        }

        TService? IOptimizationTarget.GetService<TService>() where TService : class
            => this as TService;
    }

    /// <summary>
    /// <see cref="IPredictor"/> that deliberately does NOT implement
    /// <see cref="IOptimizationTarget"/>. Used to exercise the opt-out branch.
    /// </summary>
    private sealed class PlainPredictor : IPredictor
    {
        private readonly List<object> _demos = [];

        public string Name { get; set; } = "";
        public string Instructions { get; set; } = "";
        public IList Demos => _demos;
        public ChatOptions Config { get; set; } = new();

        public PredictorState GetState() => new()
        {
            Instructions = Instructions,
            Demos = [],
            Config = null,
        };

        public void LoadState(PredictorState state)
            => Instructions = state.Instructions;

        public void AddDemo(object input, object output) => _demos.Add((input, output));

        public IPredictor Clone() => new PlainPredictor { Name = Name, Instructions = Instructions };
    }

    /// <summary>Module with two optimizable predictors ("first" and "second").</summary>
    private sealed class TwoPredictorModule : LmpModule
    {
        public StubOptimizablePredictor First { get; }
        public StubOptimizablePredictor Second { get; }

        public TwoPredictorModule()
            : this(new StubOptimizablePredictor { Name = "first" },
                   new StubOptimizablePredictor { Name = "second" })
        {
        }

        private TwoPredictorModule(StubOptimizablePredictor first, StubOptimizablePredictor second)
        {
            First = first;
            Second = second;
        }

        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [("first", First), ("second", Second)];

        protected override LmpModule CloneCore()
            => new TwoPredictorModule(
                (StubOptimizablePredictor)First.Clone(),
                (StubOptimizablePredictor)Second.Clone());
    }

    /// <summary>Module with one <see cref="PlainPredictor"/> (no IOptimizationTarget).</summary>
    private sealed class PlainPredictorModule : LmpModule
    {
        public PlainPredictor Stubby { get; }

        public PlainPredictorModule() : this(new PlainPredictor { Name = "stubby" }) { }

        private PlainPredictorModule(PlainPredictor stubby) => Stubby = stubby;

        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult(input);

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [("stubby", Stubby)];

        protected override LmpModule CloneCore()
            => new PlainPredictorModule((PlainPredictor)Stubby.Clone());
    }

    // ── Baseline tests (T1 regressions) ────────────────────────────────

    [Fact]
    public void LmpModule_IsAn_IOptimizationTarget()
    {
        var module = new EchoModule();
        Assert.IsAssignableFrom<IOptimizationTarget>(module);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsTraceAndReturnsForwardOutput()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var (output, trace) = await target.ExecuteAsync("hello");

        Assert.Equal("hello", output);
        Assert.NotNull(trace);
    }

    [Fact]
    public void GetState_ApplyState_RoundTrip()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var snapshot = target.GetState();
        target.ApplyState(snapshot);

        var typed = module.GetModuleState();
        Assert.NotNull(typed);
    }

    [Fact]
    public void GetService_ReturnsSelfForLmpModule()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        Assert.Same(module, target.GetService<LmpModule>());
    }

    [Fact]
    public void WithParameters_Empty_ClonesUnderlyingModule()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        var clone = target.WithParameters(ParameterAssignment.Empty);

        Assert.NotNull(clone);
        Assert.IsType<EchoModule>(clone);
        Assert.NotSame(module, clone);
    }

    // ── T2b fractal delegation tests ───────────────────────────────────

    [Fact]
    public void GetParameterSpace_ModuleWithNoPredictors_IsEmpty()
    {
        var module = new EchoModule();
        IOptimizationTarget target = module;

        Assert.True(target.GetParameterSpace().IsEmpty);
    }

    [Fact]
    public void GetParameterSpace_ModuleWithPredictors_EmitsPrefixedParameters()
    {
        var module = new TwoPredictorModule();
        module.First.Instructions = "SeedFirst";
        module.Second.Instructions = "SeedSecond";
        IOptimizationTarget target = module;

        var space = target.GetParameterSpace();

        Assert.Equal(4, space.Parameters.Count);
        Assert.True(space.Parameters.ContainsKey("first.instructions"));
        Assert.True(space.Parameters.ContainsKey("first.demos"));
        Assert.True(space.Parameters.ContainsKey("second.instructions"));
        Assert.True(space.Parameters.ContainsKey("second.demos"));

        var firstInstr = Assert.IsType<StringValued>(space.Parameters["first.instructions"]);
        Assert.Equal("SeedFirst", firstInstr.InitialValue);
    }

    [Fact]
    public void WithParameters_RoutesPrefixedAssignment_ToCorrectPredictor()
    {
        var module = new TwoPredictorModule();
        module.First.Instructions = "OrigFirst";
        module.Second.Instructions = "OrigSecond";
        IOptimizationTarget target = module;

        var assignment = ParameterAssignment.Empty.With("first.instructions", "Updated");
        var clone = Assert.IsType<TwoPredictorModule>(target.WithParameters(assignment));

        Assert.NotSame(module, clone);
        Assert.Equal("Updated", clone.First.Instructions);
        Assert.Equal("OrigSecond", clone.Second.Instructions);
        // Original untouched.
        Assert.Equal("OrigFirst", module.First.Instructions);
        Assert.Equal("OrigSecond", module.Second.Instructions);
    }

    [Fact]
    public void WithParameters_UnknownPrefix_IgnoredLikeChainTarget()
    {
        var module = new TwoPredictorModule();
        module.First.Instructions = "OrigFirst";
        module.Second.Instructions = "OrigSecond";
        IOptimizationTarget target = module;

        var assignment = ParameterAssignment.Empty.With("nonexistent.foo", "bar");
        var clone = Assert.IsType<TwoPredictorModule>(target.WithParameters(assignment));

        Assert.NotSame(module, clone);
        Assert.Equal("OrigFirst", clone.First.Instructions);
        Assert.Equal("OrigSecond", clone.Second.Instructions);
    }

    [Fact]
    public void WithParameters_UnknownKeyWithValidPrefix_ThrowsArgumentException()
    {
        var module = new TwoPredictorModule();
        IOptimizationTarget target = module;

        var assignment = ParameterAssignment.Empty.With("first.temperature", 0.5);

        var ex = Assert.Throws<ArgumentException>(() => target.WithParameters(assignment));
        Assert.Contains("temperature", ex.Message);
        Assert.Contains("instructions", ex.Message);
        Assert.Contains("demos", ex.Message);
    }

    [Fact]
    public void WithParameters_CustomIPredictorWithoutIOptimizationTarget_ThrowsNotSupportedException()
    {
        var module = new PlainPredictorModule();
        IOptimizationTarget target = module;

        var assignment = ParameterAssignment.Empty.With("stubby.instructions", "X");

        var ex = Assert.Throws<NotSupportedException>(() => target.WithParameters(assignment));
        Assert.Contains("stubby", ex.Message);
        Assert.Contains("IOptimizationTarget", ex.Message);
    }
}
