namespace LMP.Tests;

public sealed class ContextualBanditTests
{
    // ── Constructor Validation ─────────────────────────────────

    [Fact]
    public void Constructor_NullParameterName_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ContextualBandit(null!));

    [Fact]
    public void Constructor_ZeroNumTrials_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ContextualBandit("skills", numTrials: 0));

    [Fact]
    public void Constructor_NegativeNumTrials_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ContextualBandit("skills", numTrials: -5));

    [Fact]
    public void Constructor_ValidParams_Succeeds()
    {
        var bandit = new ContextualBandit("skills", successThreshold: 0.6f, numTrials: 10, seed: 42);
        Assert.Equal("skills", bandit.ParameterName);
        Assert.Equal(0.6f, bandit.SuccessThreshold);
        Assert.Equal(10, bandit.NumTrials);
        Assert.Equal(42, bandit.Seed);
    }

    [Fact]
    public void Constructor_DefaultProperties()
    {
        var bandit = new ContextualBandit("skills");
        Assert.Equal(0.5f, bandit.SuccessThreshold);
        Assert.Equal(20, bandit.NumTrials);
        Assert.Null(bandit.Seed);
    }

    // ── IOptimizer Contract ─────────────────────────────────────

    [Fact]
    public void ImplementsIOptimizer()
    {
        IOptimizer optimizer = new ContextualBandit("skills");
        Assert.NotNull(optimizer);
    }

    [Fact]
    public async Task OptimizeAsync_NullContext_Throws()
    {
        var bandit = new ContextualBandit("skills");
        await Assert.ThrowsAsync<ArgumentNullException>(() => bandit.OptimizeAsync(null!));
    }

    // ── No-Op Cases ─────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_EmptyTrainSet_IsNoOp()
    {
        var bandit = new ContextualBandit("skills", numTrials: 5, seed: 1);
        var ctx = MakeContext([SkillManifest.For("a")], []);
        await bandit.OptimizeAsync(ctx);
        Assert.Empty(ctx.Diagnostics.Snapshots);
    }

    [Fact]
    public async Task OptimizeAsync_ParameterNotInSearchSpace_IsNoOp()
    {
        var bandit = new ContextualBandit("tools", numTrials: 5, seed: 1);
        var ctx = MakeContext([SkillManifest.For("a")], [MakeExample()]);
        // "tools" param not added, only "skills"
        await bandit.OptimizeAsync(ctx);
        Assert.Empty(ctx.Diagnostics.Snapshots);
    }

    [Fact]
    public async Task OptimizeAsync_EmptyPool_IsNoOp()
    {
        var bandit = new ContextualBandit("skills", numTrials: 3, seed: 1);
        var ctx = MakeContext([], [MakeExample()]);
        await bandit.OptimizeAsync(ctx);
        Assert.Empty(ctx.Diagnostics.Snapshots);
    }

    // ── Bag Key Population ──────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_PopulatesAlphasBetasAndBestKeys()
    {
        var bandit = new ContextualBandit("skills", numTrials: 5, seed: 0);
        var ctx = MakeContext([SkillManifest.For("a"), SkillManifest.For("b")], [MakeExample(), MakeExample()]);

        await bandit.OptimizeAsync(ctx);

        Assert.True(ctx.Diagnostics.Snapshots.ContainsKey("lmp.bandit:skills:alphas"), "Missing alphas key");
        Assert.True(ctx.Diagnostics.Snapshots.ContainsKey("lmp.bandit:skills:betas"), "Missing betas key");
        Assert.True(ctx.Diagnostics.Snapshots.ContainsKey("lmp.bandit:skills:best"), "Missing best key");
    }

    [Fact]
    public async Task OptimizeAsync_AlphasAndBetasHaveCorrectLength()
    {
        var skills = new[] { SkillManifest.For("a"), SkillManifest.For("b"), SkillManifest.For("c") };
        var bandit = new ContextualBandit("skills", numTrials: 5, seed: 0);
        var ctx = MakeContext(skills, [MakeExample()]);

        await bandit.OptimizeAsync(ctx);

        var alphas = (float[])ctx.Diagnostics.Snapshots["lmp.bandit:skills:alphas"];
        var betas = (float[])ctx.Diagnostics.Snapshots["lmp.bandit:skills:betas"];
        Assert.Equal(3, alphas.Length);
        Assert.Equal(3, betas.Length);
    }

    [Fact]
    public async Task OptimizeAsync_BestSubsetRespectsBounds()
    {
        var skills = new[] { SkillManifest.For("a"), SkillManifest.For("b"), SkillManifest.For("c") };
        var bandit = new ContextualBandit("skills", numTrials: 10, seed: 7);
        var ctx = MakeContext(skills, [MakeExample(), MakeExample()], minSize: 1, maxSize: 2);

        await bandit.OptimizeAsync(ctx);

        var best = (IReadOnlyList<object>)ctx.Diagnostics.Snapshots["lmp.bandit:skills:best"];
        Assert.InRange(best.Count, 1, 2);
    }

    [Fact]
    public async Task OptimizeAsync_AllAlphasAndBetasAtLeastOne()
    {
        var skills = new[] { SkillManifest.For("a"), SkillManifest.For("b") };
        var bandit = new ContextualBandit("skills", numTrials: 10, seed: 3);
        var ctx = MakeContext(skills, [MakeExample(), MakeExample(), MakeExample()]);

        await bandit.OptimizeAsync(ctx);

        var alphas = (float[])ctx.Diagnostics.Snapshots["lmp.bandit:skills:alphas"];
        var betas = (float[])ctx.Diagnostics.Snapshots["lmp.bandit:skills:betas"];
        Assert.All(alphas, a => Assert.True(a >= 1f, $"alpha should be >= 1 but was {a}"));
        Assert.All(betas, b => Assert.True(b >= 1f, $"beta should be >= 1 but was {b}"));
    }

    // ── Categorical Support ─────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_CategoricalParameter_PopulatesBag()
    {
        var bandit = new ContextualBandit("choice", numTrials: 5, seed: 0);
        var space = TypedParameterSpace.Empty.Add("choice", new Categorical(3));
        var ctx = new OptimizationContext { Target = new ScorableTarget(score: 0.8f), TrainSet = [MakeExample()], Metric = (_, _) => 0.8f };
        ctx.SearchSpace = space;

        await bandit.OptimizeAsync(ctx);

        Assert.True(ctx.Diagnostics.Snapshots.ContainsKey("lmp.bandit:choice:alphas"));
        Assert.Equal(3, ((float[])ctx.Diagnostics.Snapshots["lmp.bandit:choice:alphas"]).Length);
    }

    // ── Graceful NotSupportedException ─────────────────────────

    [Fact]
    public async Task OptimizeAsync_WithParameters_ThrowsNotSupported_BreaksGracefully()
    {
        var bandit = new ContextualBandit("skills", numTrials: 5, seed: 1);
        var ctx = new OptimizationContext { Target = new NotSupportedTarget(), TrainSet = [MakeExample(), MakeExample()], Metric = (_, _) => 0.5f };
        ctx.SearchSpace = TypedParameterSpace.Empty.AddSkillPool(
            [SkillManifest.For("a"), SkillManifest.For("b")]);

        // Should not throw — NotSupportedException breaks the loop gracefully
        await bandit.OptimizeAsync(ctx);

        // Bag keys are still written (best-of-nothing = empty list from MAP)
        Assert.True(ctx.Diagnostics.Snapshots.ContainsKey("lmp.bandit:skills:alphas"));
    }

    // ── Seed Reproducibility ────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_SameSeed_ProducesSameAlphas()
    {
        var skills = new[] { SkillManifest.For("a"), SkillManifest.For("b") };
        var examples = Enumerable.Range(0, 4).Select(_ => MakeExample()).ToList();

        var ctx1 = MakeContext(skills, examples);
        var ctx2 = MakeContext(skills, examples);

        await new ContextualBandit("skills", numTrials: 8, seed: 42).OptimizeAsync(ctx1);
        await new ContextualBandit("skills", numTrials: 8, seed: 42).OptimizeAsync(ctx2);

        var alphas1 = (float[])ctx1.Diagnostics.Snapshots["lmp.bandit:skills:alphas"];
        var alphas2 = (float[])ctx2.Diagnostics.Snapshots["lmp.bandit:skills:alphas"];
        Assert.Equal(alphas1, alphas2);
    }

    // ── TrialHistory ────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_RecordsTrials()
    {
        var skills = new[] { SkillManifest.For("a"), SkillManifest.For("b") };
        var bandit = new ContextualBandit("skills", numTrials: 5, seed: 0);
        var ctx = MakeContext(skills, [MakeExample(), MakeExample(), MakeExample()]);

        await bandit.OptimizeAsync(ctx);

        Assert.True(ctx.TrialHistory.Count > 0, "Expected some trials to be recorded");
    }

    // ── Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task OptimizeAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var bandit = new ContextualBandit("skills", numTrials: 100, seed: 0);
        var ctx = MakeContext(
            Enumerable.Range(0, 5).Select(i => SkillManifest.For($"skill{i}")).ToArray(),
            Enumerable.Range(0, 10).Select(_ => MakeExample()).ToList());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bandit.OptimizeAsync(ctx, cts.Token));
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static Example MakeExample()
        => new Example<string, string>("input", "output");

    private static OptimizationContext MakeContext(
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<Example> trainSet,
        int minSize = 1, int maxSize = -1)
    {
        var target = new ScorableTarget(score: 0.8f);
        var ctx = new OptimizationContext { Target = target, TrainSet = trainSet, Metric = (_, _) => 0.8f };
        if (skills.Count > 0)
            ctx.SearchSpace = TypedParameterSpace.Empty.AddSkillPool(skills, minSize: minSize, maxSize: maxSize);
        return ctx;
    }

    /// <summary>Target that executes successfully and returns a fixed score.</summary>
    private sealed class ScorableTarget : IOptimizationTarget
    {
        private readonly float _score;
        public ScorableTarget(float score) => _score = score;

        public TargetShape Shape => TargetShape.SingleTurn;

        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult(((object)_score, new Trace()));

        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From(0);
        public void ApplyState(TargetState state) { }

        public IOptimizationTarget WithParameters(ParameterAssignment assignment) => this;
        public TService? GetService<TService>() where TService : class => null;
    }

    /// <summary>Target that throws <see cref="NotSupportedException"/> from WithParameters.</summary>
    private sealed class NotSupportedTarget : IOptimizationTarget
    {
        public TargetShape Shape => TargetShape.SingleTurn;

        public Task<(object Output, Trace Trace)> ExecuteAsync(object input, CancellationToken ct = default)
            => Task.FromResult(((object)"ok", new Trace()));

        public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;
        public TargetState GetState() => TargetState.From(0);
        public void ApplyState(TargetState state) { }

        public IOptimizationTarget WithParameters(ParameterAssignment assignment)
            => throw new NotSupportedException("This target does not support parameter application.");

        public TService? GetService<TService>() where TService : class => null;
    }
}
