using LMP.Extensions.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace LMP.Extensions.Evaluation.Tests;

public sealed class EvaluationCritiqueTests
{
    // ── Constructor Validation ──────────────────────────────────

    [Fact]
    public void Constructor_NullEvaluator_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EvaluationCritique(null!, null, "MyMetric"));
    }

    [Fact]
    public void Constructor_NullMetricName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EvaluationCritique(new FakeEvaluator("MyMetric"), null, null!));
    }

    [Fact]
    public void Constructor_WhitespaceMetricName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new EvaluationCritique(new FakeEvaluator("x"), null, "   "));
    }

    [Fact]
    public void Constructor_ZeroMaxScore_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EvaluationCritique(new FakeEvaluator("x"), null, "x", maxScore: 0f));
    }

    [Fact]
    public void Constructor_ZeroMaxExamples_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EvaluationCritique(new FakeEvaluator("x"), null, "x", maxExamples: 0));
    }

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var critique = new EvaluationCritique(new FakeEvaluator("Coherence"), null, "Coherence",
            maxScore: 5.0f, maxExamples: 3);
        Assert.NotNull(critique);
    }

    // ── IOptimizer contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new EvaluationCritique(new FakeEvaluator("x"), null, "x");
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var critique = new EvaluationCritique(new FakeEvaluator("x"), null, "x");
        await Assert.ThrowsAsync<ArgumentNullException>(() => critique.OptimizeAsync(null!));
    }

    [Fact]
    public async Task OptimizeAsync_EmptyTrainSet_IsNoOp()
    {
        var evaluator = new FakeEvaluator("Coherence", rationale: "Good output");
        var critique = new EvaluationCritique(evaluator, null, "Coherence");
        var target = new StubCritiqueTarget();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = [],
            Metric = (_, _) => 1f
        };

        await critique.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.ReflectionLog.Count);
    }

    [Fact]
    public async Task OptimizeAsync_AddsGlobalScopeEntriesToReflectionLog()
    {
        var evaluator = new FakeEvaluator("Coherence", rationale: "The output is coherent");
        var critique = new EvaluationCritique(evaluator, null, "Coherence", maxExamples: 2);
        var target = new StubCritiqueTarget();
        var trainSet = Enumerable.Range(0, 3).Select(i =>
            (Example)new Example<string, string>($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.8f
        };

        await critique.OptimizeAsync(ctx);

        Assert.Equal(2, ctx.ReflectionLog.Count); // capped to maxExamples
        Assert.All(ctx.ReflectionLog.Entries, e => Assert.Equal(ReflectionScope.Global, e.Scope));
        Assert.All(ctx.ReflectionLog.Entries, e => Assert.Contains("coherent", e.Text));
    }

    [Fact]
    public async Task OptimizeAsync_EmptyRationale_NoEntryAdded()
    {
        var evaluator = new FakeEvaluator("Coherence", rationale: ""); // empty rationale
        var critique = new EvaluationCritique(evaluator, null, "Coherence");
        var target = new StubCritiqueTarget();
        IReadOnlyList<Example> trainList = [new Example<string, string>("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainList,
            Metric = (_, _) => 1f
        };

        await critique.OptimizeAsync(ctx);

        Assert.Equal(0, ctx.ReflectionLog.Count); // nothing added
    }

    [Fact]
    public async Task OptimizeAsync_EvaluatorThrows_ContinuesOtherExamples()
    {
        var evaluator = new ExplodingEvaluator();
        var critique = new EvaluationCritique(evaluator, null, "Coherence");
        var target = new StubCritiqueTarget();
        IReadOnlyList<Example> trainList = [new Example<string, string>("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainList,
            Metric = (_, _) => 1f
        };

        // Should not throw; evaluator failure is swallowed
        await critique.OptimizeAsync(ctx);
        Assert.Equal(0, ctx.ReflectionLog.Count);
    }

    [Fact]
    public async Task OptimizeAsync_SourceNamedEvaluationCritique()
    {
        var evaluator = new FakeEvaluator("MyMetric", rationale: "Test observation");
        var critique = new EvaluationCritique(evaluator, null, "MyMetric");
        var target = new StubCritiqueTarget();
        IReadOnlyList<Example> trainList = [new Example<string, string>("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainList,
            Metric = (_, _) => 1f
        };

        await critique.OptimizeAsync(ctx);

        Assert.Equal("EvaluationCritique", ctx.ReflectionLog.Entries[0].Source);
    }

    [Fact]
    public async Task OptimizeAsync_RecordsTrials()
    {
        var evaluator = new FakeEvaluator("Coherence", rationale: "Good");
        var critique = new EvaluationCritique(evaluator, null, "Coherence", maxExamples: 3);
        var target = new StubCritiqueTarget();
        var trainSet = Enumerable.Range(0, 3).Select(i =>
            (Example)new Example<string, string>($"q{i}", "a")).ToList();
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 0.5f
        };

        await critique.OptimizeAsync(ctx);

        Assert.Equal(3, ctx.TrialHistory.Count);
    }

    [Fact]
    public async Task OptimizeAsync_CancellationToken_Respected()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var evaluator = new FakeEvaluator("Coherence", rationale: "Good");
        var critique = new EvaluationCritique(evaluator, null, "Coherence");
        var target = new StubCritiqueTarget();
        IReadOnlyList<Example> trainSet = [new Example<string, string>("q", "a")];
        var ctx = new OptimizationContext
        {
            Target = target,
            TrainSet = trainSet,
            Metric = (_, _) => 1f
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => critique.OptimizeAsync(ctx, cts.Token));
    }

    // ── Helpers ─────────────────────────────────────────────────

    private sealed class StubCritiqueTarget : IOptimizationTarget
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

    /// <summary>Deterministic evaluator that returns a fixed rationale and score.</summary>
    private sealed class FakeEvaluator : IEvaluator
    {
        private readonly string _metricName;
        private readonly string _rationale;
        private readonly double _score;

        public FakeEvaluator(string metricName, string rationale = "", double score = 4.0)
        {
            _metricName = metricName;
            _rationale = rationale;
            _score = score;
        }

        public IReadOnlyCollection<string> EvaluationMetricNames => [_metricName];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? evaluationContext = null,
            CancellationToken cancellationToken = default)
        {
            var metric = new NumericMetric(_metricName, value: _score, _rationale);
            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }
    }

    /// <summary>Evaluator that always throws to simulate transient failures.</summary>
    private sealed class ExplodingEvaluator : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["Coherence"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? evaluationContext = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated evaluator failure");
    }
}
