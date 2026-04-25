namespace LMP.Tests;

public class TrajectoryMetricTests
{
    private static Trajectory MakeTrajectory(params float?[] rewards)
    {
        var turns = rewards.Select(r => new Turn(Output: "out", Reward: r)).ToList();
        return new Trajectory(turns);
    }

    private static Trajectory WithSource(Trajectory t, Example source)
        => new Trajectory(t.Turns, source);

    // ── Create (sync) ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_Sync_ScoresCorrectly()
    {
        var metric = TrajectoryMetric.Create(t => t.TurnCount * 0.1f);
        var t = MakeTrajectory(0f, 0f, 0f); // 3 turns
        var score = await metric.ScoreAsync(t);
        Assert.Equal(0.3f, score, precision: 5);
    }

    [Fact]
    public void Create_Sync_NullFn_Throws()
        => Assert.Throws<ArgumentNullException>(() => TrajectoryMetric.Create((Func<Trajectory, float>)null!));

    // ── Create (async) ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_Async_ScoresCorrectly()
    {
        var metric = TrajectoryMetric.Create(
            async (t, _) => { await Task.Yield(); return t.TurnCount * 0.5f; });
        var t = MakeTrajectory(0f, 0f); // 2 turns
        var score = await metric.ScoreAsync(t);
        Assert.Equal(1.0f, score, precision: 5);
    }

    [Fact]
    public void Create_Async_NullFn_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => TrajectoryMetric.Create(
                (Func<Trajectory, CancellationToken, ValueTask<float>>)null!));

    // ── AverageReward ──────────────────────────────────────────────────

    [Fact]
    public async Task AverageReward_AveragesNonNullRewards()
    {
        var metric = TrajectoryMetric.AverageReward();
        var t = MakeTrajectory(0.0f, 1.0f, null); // avg = 0.5, null excluded
        var score = await metric.ScoreAsync(t);
        Assert.Equal(0.5f, score, precision: 5);
    }

    [Fact]
    public async Task AverageReward_EmptyTrajectory_ReturnsZero()
    {
        var metric = TrajectoryMetric.AverageReward();
        var score = await metric.ScoreAsync(Trajectory.Empty);
        Assert.Equal(0f, score);
    }

    [Fact]
    public async Task AverageReward_AllNullRewards_ReturnsZero()
    {
        var metric = TrajectoryMetric.AverageReward();
        var t = MakeTrajectory(null, null);
        var score = await metric.ScoreAsync(t);
        Assert.Equal(0f, score);
    }

    // ── TotalReward ────────────────────────────────────────────────────

    [Fact]
    public async Task TotalReward_SumsNonNullRewards()
    {
        var metric = TrajectoryMetric.TotalReward();
        var t = MakeTrajectory(0.3f, 0.5f, 0.2f);
        var score = await metric.ScoreAsync(t);
        Assert.Equal(1.0f, score, precision: 5);
    }

    [Fact]
    public async Task TotalReward_EmptyTrajectory_ReturnsZero()
    {
        var metric = TrajectoryMetric.TotalReward();
        var score = await metric.ScoreAsync(Trajectory.Empty);
        Assert.Equal(0f, score);
    }

    [Fact]
    public async Task TotalReward_NullRewards_ContributeZero()
    {
        var metric = TrajectoryMetric.TotalReward();
        var t = MakeTrajectory(0.5f, null, 0.5f);
        var score = await metric.ScoreAsync(t);
        Assert.Equal(1.0f, score, precision: 5);
    }

    // ── WrapMetric ─────────────────────────────────────────────────────

    [Fact]
    public async Task WrapMetric_ScoresLastTurnOutputVsSource()
    {
        var metric = TrajectoryMetric.WrapMetric(
            Metric.Create((string predicted, string expected) =>
                predicted == expected ? 1f : 0f));

        var example = new Example<string, string>("question", "correct");
        var turns = new Turn[]
        {
            new Turn(Output: "wrong"),
            new Turn(Output: "correct")  // last turn
        };
        var t = WithSource(new Trajectory(turns), example);
        var score = await metric.ScoreAsync(t);
        Assert.Equal(1f, score);
    }

    [Fact]
    public async Task WrapMetric_EmptyTrajectory_ReturnsZero()
    {
        var metric = TrajectoryMetric.WrapMetric((_, _) => 1f);
        var score = await metric.ScoreAsync(Trajectory.Empty);
        Assert.Equal(0f, score);
    }

    [Fact]
    public async Task WrapMetric_NoSource_ReturnsZero()
    {
        var metric = TrajectoryMetric.WrapMetric((_, _) => 1f);
        var t = MakeTrajectory((float?)null); // has turns, no source
        var score = await metric.ScoreAsync(t);
        Assert.Equal(0f, score);
    }

    [Fact]
    public async Task WrapMetric_LastTurnNullOutput_ReturnsZero()
    {
        var metric = TrajectoryMetric.WrapMetric((_, _) => 1f);
        var example = new Example<string, string>("q", "a");
        var t = WithSource(new Trajectory([new Turn(Output: null)]), example);
        var score = await metric.ScoreAsync(t);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void WrapMetric_NullMetric_Throws()
        => Assert.Throws<ArgumentNullException>(()
            => TrajectoryMetric.WrapMetric((Func<Example, object, float>)null!));

    // ── CancellationToken propagation ──────────────────────────────────

    [Fact]
    public async Task Create_Async_CancellationToken_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken captured = default;

        var metric = TrajectoryMetric.Create((t, ct) =>
        {
            captured = ct;
            return ValueTask.FromResult(0f);
        });

        await metric.ScoreAsync(Trajectory.Empty, cts.Token);
        Assert.Equal(cts.Token, captured);
    }
}
