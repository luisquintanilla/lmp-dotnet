namespace LMP.Tests;

public class TurnTests
{
    [Fact]
    public void DefaultKind_IsMessage()
    {
        var turn = new Turn();
        Assert.Equal(TurnKind.Message, turn.Kind);
    }

    [Fact]
    public void AllDefaults_AreNull()
    {
        var turn = new Turn();
        Assert.Null(turn.Input);
        Assert.Null(turn.Output);
        Assert.Null(turn.Reward);
        Assert.Null(turn.Usage);
        Assert.Null(turn.Attribution);
    }

    [Fact]
    public void AllParameters_ArePreserved()
    {
        var usage = new Microsoft.Extensions.AI.UsageDetails { TotalTokenCount = 42 };
        var turn = new Turn(
            Kind: TurnKind.ToolCall,
            Input: "call me",
            Output: "result",
            Reward: 0.9f,
            Usage: usage,
            Attribution: "MyPredictor");

        Assert.Equal(TurnKind.ToolCall, turn.Kind);
        Assert.Equal("call me", turn.Input);
        Assert.Equal("result", turn.Output);
        Assert.Equal(0.9f, turn.Reward);
        Assert.Same(usage, turn.Usage);
        Assert.Equal("MyPredictor", turn.Attribution);
    }

    [Fact]
    public void TurnKinds_AllValuesAvailable()
    {
        _ = new Turn(Kind: TurnKind.Message);
        _ = new Turn(Kind: TurnKind.ToolCall);
        _ = new Turn(Kind: TurnKind.Observation);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var t1 = new Turn(Kind: TurnKind.Message, Input: "hi", Output: "hello", Reward: 1f);
        var t2 = new Turn(Kind: TurnKind.Message, Input: "hi", Output: "hello", Reward: 1f);
        Assert.Equal(t1, t2);
    }

    [Fact]
    public void RecordEquality_DifferentReward_NotEqual()
    {
        var t1 = new Turn(Reward: 0.5f);
        var t2 = new Turn(Reward: 1.0f);
        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void RecordEquality_DifferentKind_NotEqual()
    {
        var t1 = new Turn(Kind: TurnKind.Message);
        var t2 = new Turn(Kind: TurnKind.ToolCall);
        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void NullReward_IsNull()
    {
        var turn = new Turn(Reward: null);
        Assert.Null(turn.Reward);
    }
}
