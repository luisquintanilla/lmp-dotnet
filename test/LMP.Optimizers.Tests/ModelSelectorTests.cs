using LMP.Optimizers;

namespace LMP.Tests;

public sealed class ModelSelectorTests
{
    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_NullParameterName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ModelSelector(null!));
    }

    [Fact]
    public void Constructor_ZeroSampleSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSelector("model", sampleSize: 0));
    }

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var ms = new ModelSelector("model", sampleSize: 5, seed: 42);
        Assert.Equal("model", ms.ParameterName);
        Assert.Equal(5, ms.SampleSize);
        Assert.Equal(42, ms.Seed);
    }

    // ── IOptimizer contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new ModelSelector("model");
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var ms = new ModelSelector("model");
        await Assert.ThrowsAsync<ArgumentNullException>(() => ms.OptimizeAsync(null!));
    }

    [Fact]
    public async Task OptimizeAsync_EmptyTrainSet_IsNoOp()
    {
        var ms = new ModelSelector("model", seed: 0);
        var target = new StubTarget();
        var space = TypedParameterSpace.Empty.Add("model", new Categorical(3));
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [],
            Metric = (_, _) => 1f,
            SearchSpace = space
        };

        await ms.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_ParameterNotInSpace_IsNoOp()
    {
        var ms = new ModelSelector("nonexistent", seed: 0);
        var target = new StubTarget();
        var space = TypedParameterSpace.Empty.Add("other", new Categorical(2));
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [MakeExample("q", "a")],
            Metric = (_, _) => 1f,
            SearchSpace = space
        };

        await ms.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_CategoricalParam_EvaluatesAllCandidates()
    {
        var ms = new ModelSelector("model", sampleSize: 2, seed: 1);
        var target = new StubTarget();
        var space = TypedParameterSpace.Empty.Add("model", new Categorical(3));
        var trainSet = Enumerable.Range(0, 3).Select(i => MakeExample($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.8f,
            SearchSpace = space
        };

        await ms.OptimizeAsync(ctx);

        // Should have evaluated 3 candidates
        Assert.Equal(3, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_NotSupportedException_CandidateSkipped()
    {
        var ms = new ModelSelector("model", sampleSize: 1, seed: 0);
        var target = new ThrowingTarget();
        var space = TypedParameterSpace.Empty.Add("model", new Categorical(2));
        IReadOnlyList<Example> trainSet = [MakeExample("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 1f,
            SearchSpace = space
        };

        // Should not throw; should just skip
        await ms.OptimizeAsync(ctx);
        Assert.Equal(0, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_InitializesParetoFrontier()
    {
        var ms = new ModelSelector("model", sampleSize: 2, seed: 42);
        var target = new StubTarget();
        var space = TypedParameterSpace.Empty.Add("model", new Categorical(2));
        IReadOnlyList<Example> trainSet = [MakeExample("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.5f,
            SearchSpace = space
        };

        await ms.OptimizeAsync(ctx);

        Assert.NotNull(ctx.ParetoBoundary);
        Assert.True(ctx.ParetoBoundary.Count >= 1);
    }

    [Fact]
    public async Task OptimizeAsync_BestAssignmentUsesParameterName()
    {
        var ms = new ModelSelector("model", sampleSize: 10, seed: 0);
        var scores = new[] { 0.3f, 0.9f, 0.5f }; // index 1 is best
        int candidateIdx = 0;
        var target = new IndexTrackingTarget(scores, ref candidateIdx);
        var space = TypedParameterSpace.Empty.Add("model", new Categorical(3));
        IReadOnlyList<Example> trainSet = [MakeExample("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (ex, out_) => (float)out_,
            SearchSpace = space
        };

        await ms.OptimizeAsync(ctx);

        // Best state corresponds to index 1 (score 0.9f)
        // The assignment should have used "model" key
        Assert.Contains(ctx.TrialHistory.Trials, t => Math.Abs(t.Score - 0.9f) < 0.01f);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static Example MakeExample(string input, string label) =>
        new Example<string, string>(input, label);

    // A stub target that does nothing but return empty outputs
    private sealed class StubTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>(("output", new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }

    // A target whose WithParameters() always throws NotSupportedException
    private sealed class ThrowingTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>(("output", new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
            => throw new NotSupportedException("not supported");
        public TService? GetService<TService>() where TService : class => null;
    }

    // A target whose score varies by which candidate index it's been assigned
    private sealed class IndexTrackingTarget : IOptimizationTarget
    {
        private readonly float[] _scores;
        private int _currentIndex;

        public IndexTrackingTarget(float[] scores, ref int idx)
        {
            _scores = scores;
            _currentIndex = 0;
        }
        public TargetShape Shape => TargetShape.SingleTurn;
        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult<(object, Trace)>(((object)_scores[_currentIndex % _scores.Length], new Trace()));
        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From("state");
        public void ApplyState(TargetState state) { }
        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
        {
            if (assignment.TryGet<int>("model", out var idx))
                _currentIndex = idx;
            return this;
        }
        public TService? GetService<TService>() where TService : class => null;
    }
}
