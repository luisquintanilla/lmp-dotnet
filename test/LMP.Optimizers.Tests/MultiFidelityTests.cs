using LMP.Optimizers;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public sealed class MultiFidelityTests
{
    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_LessThan2Candidates_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiFidelity(numCandidates: 1));
    }

    [Fact]
    public void Constructor_ZeroFidelity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiFidelity(initialFidelity: 0));
    }

    [Fact]
    public void Constructor_PruningFactorLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiFidelity(pruningFactor: 1));
    }

    [Fact]
    public void Constructor_ValidParams_SetsProperties()
    {
        var mf = new MultiFidelity(numCandidates: 4, initialFidelity: 2, pruningFactor: 2, seed: 7);
        Assert.Equal(4, mf.NumCandidates);
        Assert.Equal(2, mf.InitialFidelity);
        Assert.Equal(2, mf.PruningFactor);
        Assert.Equal(7, mf.Seed);
    }

    // ── IOptimizer contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new MultiFidelity();
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var mf = new MultiFidelity();
        await Assert.ThrowsAsync<ArgumentNullException>(() => mf.OptimizeAsync(null!));
    }

    [Fact]
    public async Task OptimizeAsync_EmptySearchSpace_IsNoOp()
    {
        var mf = new MultiFidelity(seed: 0);
        var target = new StubMfTarget(score: 0.5f);
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [MakeExample("q", "a")],
            Metric = (_, _) => 0.5f,
            SearchSpace = TypedParameterSpace.Empty
        };

        await mf.OptimizeAsync(ctx);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_EmptyTrainSet_IsNoOp()
    {
        var mf = new MultiFidelity(seed: 0);
        var target = new StubMfTarget(score: 0.5f);
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(3));
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [],
            Metric = (_, _) => 0.5f,
            SearchSpace = space
        };

        await mf.OptimizeAsync(ctx);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_AddsCandidatesToParetoFrontier()
    {
        var mf = new MultiFidelity(numCandidates: 4, initialFidelity: 2, pruningFactor: 2, seed: 1);
        var target = new StubMfTarget(score: 0.7f);
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(4));
        var trainSet = Enumerable.Range(0, 5).Select(i => MakeExample($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.7f,
            SearchSpace = space
        };

        await mf.OptimizeAsync(ctx);

        Assert.NotNull(ctx.ParetoBoundary);
        Assert.True(ctx.ParetoBoundary.Count >= 1);
    }

    [Fact]
    public async Task OptimizeAsync_RecordedTrialCount_ReasonableRange()
    {
        var mf = new MultiFidelity(numCandidates: 4, initialFidelity: 2, pruningFactor: 2, seed: 5);
        var target = new StubMfTarget(score: 0.5f);
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(4));
        var trainSet = Enumerable.Range(0, 8).Select(i => MakeExample($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.5f,
            SearchSpace = space
        };

        await mf.OptimizeAsync(ctx);

        // At least some trials were recorded (pruning + final eval)
        Assert.True(ctx.TrialHistory.Count > 0);
    }

    [Fact]
    public async Task OptimizeAsync_BudgetExhausted_StopsEarly()
    {
        var mf = new MultiFidelity(numCandidates: 4, initialFidelity: 2, pruningFactor: 2, seed: 0);
        var target = new StubMfTarget(score: 0.5f, tokensPerCall: 10_000_000L);
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(4));
        var trainSet = Enumerable.Range(0, 5).Select(i => MakeExample($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.5f,
            SearchSpace = space,
            Budget = new CostBudget { MaxTokens = 1_000L } // Very small budget
        };

        await mf.OptimizeAsync(ctx);

        // Should have stopped before evaluating all candidates
        Assert.True(ctx.TrialHistory.Count < 4 * 5);
    }

    [Fact]
    public async Task OptimizeAsync_NotSupportedException_CandidateSkipped()
    {
        var mf = new MultiFidelity(numCandidates: 2, initialFidelity: 1, pruningFactor: 2, seed: 0);
        var target = new ThrowingMfTarget();
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(2));
        IReadOnlyList<Example> trainSet = [MakeExample("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 1f,
            SearchSpace = space
        };

        // Should not throw — skips unsupported candidates
        await mf.OptimizeAsync(ctx);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_AppliesBestState()
    {
        var mf = new MultiFidelity(numCandidates: 2, initialFidelity: 2, pruningFactor: 2, seed: 0);
        var scores = new[] { 0.3f, 0.9f };
        int callCount = 0;
        var target = new ScoreVariantTarget(scores, ref callCount);
        var space = TypedParameterSpace.Empty.Add("p", new Categorical(2));
        var trainSet = Enumerable.Range(0, 3).Select(i => MakeExample($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (ex, out_) => (float)out_,
            SearchSpace = space
        };

        await mf.OptimizeAsync(ctx);

        // Best trial should be the 0.9 candidate
        Assert.Contains(ctx.TrialHistory.Trials, t => Math.Abs(t.Score - 0.9f) < 0.05f);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static Example MakeExample(string input, string label) =>
        new Example<string, string>(input, label);

    private sealed class StubMfTarget : IOptimizationTarget
    {
        private readonly float _score;
        private readonly long _tokensPerCall;
        public StubMfTarget(float score, long tokensPerCall = 100)
        { _score = score; _tokensPerCall = tokensPerCall; }
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
        {
            var trace = new Trace();
            trace.Record("p", "input", "output",
                new UsageDetails { InputTokenCount = _tokensPerCall / 2,
                    OutputTokenCount = _tokensPerCall / 2, TotalTokenCount = _tokensPerCall });
            return Task.FromResult<(object, Trace)>((_score, trace));
        }
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }

    private sealed class ThrowingMfTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>(("out", new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
            => throw new NotSupportedException();
        public TService? GetService<TService>() where TService : class => null;
    }

    private sealed class ScoreVariantTarget : IOptimizationTarget
    {
        private readonly float[] _scores;
        private int _idx;

        public ScoreVariantTarget(float[] scores, ref int idx)
        { _scores = scores; _idx = 0; }

        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>(((object)_scores[_idx % _scores.Length], new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        {
            if (assignment.TryGet<int>("p", out var val))
                _idx = val;
            return this;
        }
        public TService? GetService<TService>() where TService : class => null;
    }
}
