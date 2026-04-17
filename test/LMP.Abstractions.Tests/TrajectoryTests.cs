namespace LMP.Tests;

public class TrajectoryTests
{
    [Fact]
    public void Empty_HasNoTurns()
    {
        Assert.Equal(0, Trajectory.Empty.TurnCount);
        Assert.Empty(Trajectory.Empty.Turns);
    }

    [Fact]
    public void Empty_LastTurn_IsNull()
    {
        Assert.Null(Trajectory.Empty.LastTurn);
    }

    [Fact]
    public void Empty_TotalReward_IsZero()
    {
        Assert.Equal(0f, Trajectory.Empty.TotalReward);
    }

    [Fact]
    public void Empty_AverageReward_IsZero()
    {
        Assert.Equal(0f, Trajectory.Empty.AverageReward);
    }

    [Fact]
    public void Constructor_NullTurns_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Trajectory(null!));
    }

    [Fact]
    public void TurnCount_ReflectsNumberOfTurns()
    {
        var t = new Trajectory([new Turn(), new Turn(), new Turn()]);
        Assert.Equal(3, t.TurnCount);
    }

    [Fact]
    public void TotalReward_SumsNonNullRewards()
    {
        var t = new Trajectory([
            new Turn(Reward: 0.3f),
            new Turn(Reward: 0.5f),
            new Turn(Reward: 0.2f)]);
        Assert.Equal(1.0f, t.TotalReward, precision: 5);
    }

    [Fact]
    public void TotalReward_IgnoresNullReward()
    {
        var t = new Trajectory([
            new Turn(Reward: 0.4f),
            new Turn(Reward: null),
            new Turn(Reward: 0.6f)]);
        Assert.Equal(1.0f, t.TotalReward, precision: 5);
    }

    [Fact]
    public void AverageReward_AveragesNonNullRewards()
    {
        var t = new Trajectory([
            new Turn(Reward: 0.0f),
            new Turn(Reward: 1.0f),
            new Turn(Reward: null)]); // null excluded from denominator
        Assert.Equal(0.5f, t.AverageReward, precision: 5);
    }

    [Fact]
    public void AverageReward_AllNull_ReturnsZero()
    {
        var t = new Trajectory([new Turn(), new Turn()]);
        Assert.Equal(0f, t.AverageReward);
    }

    [Fact]
    public void LastTurn_ReturnsLastElement()
    {
        var last = new Turn(Output: "final");
        var t = new Trajectory([new Turn(Output: "first"), last]);
        Assert.Same(last, t.LastTurn);
    }

    [Fact]
    public void Source_IsPreserved()
    {
        var example = new Example<string, string>("q", "a");
        var t = new Trajectory([], example);
        Assert.Same(example, t.Source);
    }

    [Fact]
    public void Source_DefaultIsNull()
    {
        var t = new Trajectory([]);
        Assert.Null(t.Source);
    }

    [Fact]
    public void Turns_IsDefensiveCopy_ModifyingInputDoesNotAffectTrajectory()
    {
        var list = new List<Turn> { new Turn(Output: "original") };
        var t = new Trajectory(list);
        list.Add(new Turn(Output: "added"));
        Assert.Equal(1, t.TurnCount);
    }

    [Fact]
    public void FromTrace_EmptyTrace_ProducesEmptyTrajectory()
    {
        var trace = new Trace();
        var t = Trajectory.FromTrace(trace);
        Assert.Equal(0, t.TurnCount);
    }

    [Fact]
    public void FromTrace_NullTrace_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Trajectory.FromTrace(null!));
    }

    [Fact]
    public void FromTrace_MapsEntriesInOrder()
    {
        var trace = new Trace();
        trace.Record("predict1", "in1", "out1");
        trace.Record("predict2", "in2", "out2");
        var t = Trajectory.FromTrace(trace);

        Assert.Equal(2, t.TurnCount);
        Assert.Equal("in1", t.Turns[0].Input);
        Assert.Equal("out1", t.Turns[0].Output);
        Assert.Equal("predict1", t.Turns[0].Attribution);
        Assert.Equal("in2", t.Turns[1].Input);
        Assert.Equal("predict2", t.Turns[1].Attribution);
    }

    [Fact]
    public void FromTrace_AllTurnsHaveMessageKind()
    {
        var trace = new Trace();
        trace.Record("p", "i", "o");
        var t = Trajectory.FromTrace(trace);
        Assert.Equal(TurnKind.Message, t.Turns[0].Kind);
    }

    [Fact]
    public void FromTrace_PreservesUsage()
    {
        var trace = new Trace();
        var usage = new Microsoft.Extensions.AI.UsageDetails { TotalTokenCount = 99 };
        trace.Record("p", "i", "o", usage);
        var t = Trajectory.FromTrace(trace);
        Assert.Same(usage, t.Turns[0].Usage);
    }

    [Fact]
    public void FromTrace_PreservesSource()
    {
        var trace = new Trace();
        var example = new Example<string, string>("q", "a");
        var t = Trajectory.FromTrace(trace, example);
        Assert.Same(example, t.Source);
    }
}
