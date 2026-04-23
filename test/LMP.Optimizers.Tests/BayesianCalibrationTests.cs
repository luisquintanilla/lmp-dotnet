using LMP.Optimizers;

namespace LMP.Tests;

public sealed class BayesianCalibrationTests
{
    // ── Constructor validation ───────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroRefinements_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new BayesianCalibration(numRefinements: 0));

    [Fact]
    public void Constructor_OneStep_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new BayesianCalibration(continuousSteps: 1));

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var bc = new BayesianCalibration(numRefinements: 5, continuousSteps: 4, seed: 1);
        Assert.NotNull(bc);
    }

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer o = new BayesianCalibration();
        Assert.NotNull(o);
    }

    // ── Null-guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var bc = new BayesianCalibration();
        await Assert.ThrowsAsync<ArgumentNullException>(() => bc.OptimizeAsync(null!));
    }

    // ── Safe no-ops ──────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_EmptyParameterSpace_IsNoOp()
    {
        // ModuleTarget always returns TypedParameterSpace.Empty → no trials.
        var bc = new BayesianCalibration(numRefinements: 5, seed: 0);
        var module = new BcFakeModule();
        var ctx = OptimizationContext.For(
            module,
            [MakeExample()],
            (_, _) => 1f);

        await bc.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_OnlyStringValuedAndSubset_IsNoOp()
    {
        // A target with only StringValued + Subset → calibrationSpace is empty → no-op.
        var bc = new BayesianCalibration(numRefinements: 5, seed: 0);
        var space = TypedParameterSpace.Empty
            .Add("instr", new StringValued("initial instruction"))
            .Add("tools", new Subset(new List<object> { "tool1" }, 1, -1));

        var target = new StaticSpaceTarget(space, score: 0.5f);
        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 0.5f);

        await bc.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_EmptyEvalSet_IsNoOp()
    {
        // trainSet empty + devSet empty → evalSet empty → no-op.
        var bc = new BayesianCalibration(numRefinements: 5, seed: 0);
        var target = new TempTarget(_ => 0.5f);
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [],
            DevSet = [],
            Metric = (_, _) => 0.5f
        };

        await bc.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    // ── Trial count ──────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_AddsExactlyNumRefinementsTrials_WhenNoImprovement()
    {
        // Incumbent always scores 1.0 → no candidate can beat it → no confirmation.
        int n = 7;
        var bc = new BayesianCalibration(numRefinements: n, seed: 0);
        var target = new TempTarget(_ => 1.0f);
        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 1.0f);

        await bc.OptimizeAsync(ctx);

        Assert.Equal(n, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_AddsConfirmationTrial_WhenImprovedOnScreening()
    {
        // All clones score 1.0f; incumbent (original) scores 0.0f.
        // Confirmation also scores 1.0f → succeeds → total = n + 1 trials.
        int n = 3;
        var bc = new BayesianCalibration(numRefinements: n, seed: 0);
        var target = new ControlledTarget(incumbentScore: 0.0f, candidateScore: 1.0f);
        var ctx = OptimizationContext.For(
            target, [MakeExample()], (_, out_) => out_ is float f ? f : 0f);

        await bc.OptimizeAsync(ctx);

        Assert.Equal(n + 1, ctx.TrialHistory.Count);
        Assert.Contains(ctx.TrialHistory.Trials,
            t => t.Notes == "BayesianCalibration:confirmation");
    }

    [Fact]
    public async Task OptimizeAsync_AllScreeningTrials_HaveCorrectNotes()
    {
        // No improvement → no confirmation trial → all trials are "BayesianCalibration".
        int n = 4;
        var bc = new BayesianCalibration(numRefinements: n, seed: 1);
        var target = new TempTarget(_ => 0.5f);
        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 0.5f);

        await bc.OptimizeAsync(ctx);

        foreach (var trial in ctx.TrialHistory.Trials)
            Assert.Equal("BayesianCalibration", trial.Notes);
    }

    // ── State management ─────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_AppliesState_WhenCandidateBeatsIncumbentAndConfirms()
    {
        var bc = new BayesianCalibration(numRefinements: 3, seed: 0);
        var target = new ControlledTarget(incumbentScore: 0.0f, candidateScore: 1.0f);
        var ctx = OptimizationContext.For(
            target, [MakeExample()], (_, out_) => out_ is float f ? f : 0f);

        await bc.OptimizeAsync(ctx);

        Assert.True(target.ApplyStateCalled);
    }

    [Fact]
    public async Task OptimizeAsync_DoesNotApplyState_WhenAllCandidatesScoreLower()
    {
        // Incumbent scores 1.0 → no candidate can beat it → ApplyState never called.
        var bc = new BayesianCalibration(numRefinements: 3, seed: 0);
        var target = new ControlledTarget(incumbentScore: 1.0f, candidateScore: 0.0f);
        var ctx = OptimizationContext.For(
            target, [MakeExample()], (_, out_) => out_ is float f ? f : 0f);

        await bc.OptimizeAsync(ctx);

        Assert.False(target.ApplyStateCalled);
    }

    [Fact]
    public async Task OptimizeAsync_DoesNotApplyState_WhenConfirmationFails()
    {
        // Clones score 1.0f on trainSet input but 0.0f on devSet input.
        // → candidate beats incumbent on sampleSet, but fails confirmation on evalSet.
        var bc = new BayesianCalibration(numRefinements: 3, seed: 0);
        var target = new ConfirmFailTarget();

        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [MakeExample("train")],  // sampleSet screening
            DevSet = [MakeExample("dev")],       // evalSet for confirmation
            Metric = (_, out_) => out_ is float f ? f : 0f
        };

        await bc.OptimizeAsync(ctx);

        Assert.False(target.ApplyStateCalled);
        // A confirmation trial IS added even when it fails
        Assert.Contains(ctx.TrialHistory.Trials,
            t => t.Notes == "BayesianCalibration:confirmation");
    }

    // ── DevSet / TrainSet selection ──────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_UsesDevSet_ForIncumbentEvaluation()
    {
        // DevSet provided → first ExecuteAsync call uses devSet example's input.
        var bc = new BayesianCalibration(numRefinements: 1, seed: 0);
        var target = new InputTrackingTarget();

        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [MakeExample("train")],
            DevSet = [MakeExample("dev")],
            Metric = (_, _) => 0.5f
        };

        await bc.OptimizeAsync(ctx);

        Assert.Equal("dev", target.FirstInput);
    }

    [Fact]
    public async Task OptimizeAsync_UsesTrain_WhenDevSetEmpty()
    {
        // No DevSet → first ExecuteAsync uses TrainSet example.
        var bc = new BayesianCalibration(numRefinements: 1, seed: 0);
        var target = new InputTrackingTarget();

        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [MakeExample("train")],
            DevSet = [],
            Metric = (_, _) => 0.5f
        };

        await bc.OptimizeAsync(ctx);

        Assert.Equal("train", target.FirstInput);
    }

    // ── Parameter decode correctness ─────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_ContinuousParam_DecodesDouble()
    {
        var bc = new BayesianCalibration(numRefinements: 3, continuousSteps: 4, seed: 42);
        var target = new ParameterCapturingTarget(
            TypedParameterSpace.Empty.Add("temperature", new Continuous(0.0, 2.0)));

        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 0.5f);
        await bc.OptimizeAsync(ctx);

        Assert.True(target.CapturedAssignments.Count > 0);
        foreach (var assignment in target.CapturedAssignments)
        {
            if (assignment.TryGet<double>("temperature", out var dval))
            {
                Assert.InRange(dval, 0.0, 2.0);
                return;
            }
        }
        Assert.Fail("No double assignment captured for 'temperature'");
    }

    [Fact]
    public async Task OptimizeAsync_IntegerParam_DecodesInt()
    {
        var bc = new BayesianCalibration(numRefinements: 3, seed: 7);
        var target = new ParameterCapturingTarget(
            TypedParameterSpace.Empty.Add("numShots", new Integer(1, 10)));

        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 0.5f);
        await bc.OptimizeAsync(ctx);

        Assert.True(target.CapturedAssignments.Count > 0);
        foreach (var assignment in target.CapturedAssignments)
        {
            if (assignment.TryGet<int>("numShots", out var ival))
            {
                Assert.InRange(ival, 1, 10);
                return;
            }
        }
        Assert.Fail("No int assignment captured for 'numShots'");
    }

    // ── Sample pool fallback ─────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_SamplePool_FallsBackToEvalSet_WhenTrainSetEmpty()
    {
        // TrainSet empty + DevSet provided → samplePool = evalSet.
        // BayesianCalibration should still run numRefinements iterations.
        var bc = new BayesianCalibration(numRefinements: 2, seed: 0);
        var target = new TempTarget(_ => 0.5f);

        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [],
            DevSet = [MakeExample()],
            Metric = (_, _) => 0.5f
        };

        await bc.OptimizeAsync(ctx);

        Assert.Equal(2, ctx.TrialHistory.Count);
    }

    // ── Trial cost accumulation ──────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_TrialCost_IsNonNegative()
    {
        var bc = new BayesianCalibration(numRefinements: 2, seed: 0);
        var target = new TempTarget(_ => 0.5f);
        var ctx = OptimizationContext.For(target, [MakeExample()], (_, _) => 0.5f);

        await bc.OptimizeAsync(ctx);

        foreach (var trial in ctx.TrialHistory.Trials)
        {
            Assert.True(trial.Cost.TotalTokens >= 0);
            Assert.True(trial.Cost.ElapsedMilliseconds >= 0);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Example MakeExample(string input = "q")
        => new Example<string, string>(input, "a");

    // ── Fake Implementations ─────────────────────────────────────────────

    private sealed class BcFakeModule : LmpModule
    {
        public override Task<object> ForwardAsync(object input, CancellationToken ct = default)
            => Task.FromResult<object>("output");
        protected override LmpModule CloneCore() => new BcFakeModule();
    }

    /// <summary>Target whose GetParameterSpace returns the provided space; ExecuteAsync returns fixed score.</summary>
    private sealed class StaticSpaceTarget : IOptimizationTarget
    {
        private readonly TypedParameterSpace _space;
        private readonly float _score;
        public StaticSpaceTarget(TypedParameterSpace space, float score) { _space = space; _score = score; }
        public TargetShape Shape => TargetShape.SingleTurn;
        public TypedParameterSpace GetParameterSpace() => _space;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>((_score, new Trace()));
        public TargetState GetState() => TargetState.From(BcStateBox.From(_score));
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>Target with Continuous("temperature") space; score = scoreFunc(temperature).</summary>
    private sealed class TempTarget : IOptimizationTarget
    {
        private readonly Func<double, float> _scoreFunc;
        private double _temp = 1.0;
        public TempTarget(Func<double, float> scoreFunc) { _scoreFunc = scoreFunc; }
        public TargetShape Shape => TargetShape.SingleTurn;
        public TypedParameterSpace GetParameterSpace()
            => TypedParameterSpace.Empty.Add("temperature", new Continuous(0.0, 2.0));
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>((_scoreFunc(_temp), new Trace()));
        public TargetState GetState() => TargetState.From(BcStateBox.From(_temp));
        public void ApplyState(TargetState state) => _temp = ((BcStateBox)state.Value).DoubleValue;
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        {
            var clone = new TempTarget(_scoreFunc);
            if (assignment.TryGet<double>("temperature", out var t)) clone._temp = t;
            return clone;
        }
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>
    /// Target with Continuous("x") space.
    /// Original returns <paramref name="incumbentScore"/>; clones return <paramref name="candidateScore"/>.
    /// Tracks whether ApplyState was called on the original.
    /// </summary>
    private sealed class ControlledTarget : IOptimizationTarget
    {
        private readonly float _incumbentScore;
        private readonly float _candidateScore;
        private readonly bool _isClone;
        public bool ApplyStateCalled { get; private set; }

        public ControlledTarget(float incumbentScore, float candidateScore)
        {
            _incumbentScore = incumbentScore;
            _candidateScore = candidateScore;
        }

        private ControlledTarget(float incumbentScore, float candidateScore, bool isClone)
            : this(incumbentScore, candidateScore) => _isClone = isClone;

        public TargetShape Shape => TargetShape.SingleTurn;

        public TypedParameterSpace GetParameterSpace()
            => TypedParameterSpace.Empty.Add("x", new Continuous(0.0, 1.0));

        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        {
            float score = _isClone ? _candidateScore : _incumbentScore;
            return Task.FromResult<(object, Trace)>((score, new Trace()));
        }

        public TargetState GetState() => TargetState.From(BcStateBox.From(0.0));
        public void ApplyState(TargetState state) => ApplyStateCalled = true;
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
            => new ControlledTarget(_incumbentScore, _candidateScore, isClone: true);
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>
    /// Target that scores clones differently based on the input value:
    /// "train" → 1.0f (beats 0.5f incumbent); "dev" → 0.0f (fails confirmation).
    /// Tracks whether ApplyState was called on the original.
    /// </summary>
    private sealed class ConfirmFailTarget : IOptimizationTarget
    {
        private readonly bool _isClone;
        public bool ApplyStateCalled { get; private set; }
        public ConfirmFailTarget(bool isClone = false) => _isClone = isClone;
        public TargetShape Shape => TargetShape.SingleTurn;
        public TypedParameterSpace GetParameterSpace()
            => TypedParameterSpace.Empty.Add("x", new Continuous(0.0, 1.0));
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        {
            float score = _isClone
                ? (input?.ToString() == "train" ? 1.0f : 0.0f)
                : 0.5f;
            return Task.FromResult<(object, Trace)>((score, new Trace()));
        }
        public TargetState GetState() => TargetState.From(BcStateBox.From(0.0));
        public void ApplyState(TargetState state) => ApplyStateCalled = true;
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
            => new ConfirmFailTarget(isClone: true);
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>Records the first input seen in ExecuteAsync (for DevSet/TrainSet selection tests).</summary>
    private sealed class InputTrackingTarget : IOptimizationTarget
    {
        public string? FirstInput { get; private set; }
        private bool _recorded;
        public TargetShape Shape => TargetShape.SingleTurn;
        public TypedParameterSpace GetParameterSpace()
            => TypedParameterSpace.Empty.Add("x", new Continuous(0.0, 1.0));
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        {
            if (!_recorded) { FirstInput = input?.ToString(); _recorded = true; }
            return Task.FromResult<(object, Trace)>((0.5f, new Trace()));
        }
        public TargetState GetState() => TargetState.From(BcStateBox.From(0.0));
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>Records ParameterAssignment values passed via WithParameters.</summary>
    private sealed class ParameterCapturingTarget : IOptimizationTarget
    {
        private readonly TypedParameterSpace _space;
        public List<ParameterAssignment> CapturedAssignments { get; } = [];
        public ParameterCapturingTarget(TypedParameterSpace space) { _space = space; }
        public TargetShape Shape => TargetShape.SingleTurn;
        public TypedParameterSpace GetParameterSpace() => _space;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>((0.5f, new Trace()));
        public TargetState GetState() => TargetState.From(BcStateBox.From(0.0));
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        {
            CapturedAssignments.Add(assignment);
            return this;
        }
        public TService? GetService<TService>() where TService : class => null;
    }
}

/// <summary>Simple reference-type state box for BayesianCalibration tests.</summary>
file sealed class BcStateBox
{
    public double DoubleValue { get; }
    private BcStateBox(double v) { DoubleValue = v; }
    public static BcStateBox From(double v) => new(v);
    public static BcStateBox From(float v) => new(v);
}
