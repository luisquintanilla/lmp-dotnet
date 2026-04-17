namespace LMP.Tests;

public class TrialHistoryTests
{
    [Fact]
    public void Empty_BestScore_IsZero()
    {
        var history = new TrialHistory();
        Assert.Equal(0f, history.BestScore);
    }

    [Fact]
    public void Empty_TotalTokens_IsZero()
    {
        var history = new TrialHistory();
        Assert.Equal(0L, history.TotalTokens);
    }

    [Fact]
    public void Empty_TotalApiCalls_IsZero()
    {
        var history = new TrialHistory();
        Assert.Equal(0, history.TotalApiCalls);
    }

    [Fact]
    public void Add_Trial_IncrementsCount()
    {
        var history = new TrialHistory();
        history.Add(new Trial(0.8f, new TrialCost(100, 60, 40, 50, 2)));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void BestScore_ReturnsMaxScoreAcrossTrials()
    {
        var history = new TrialHistory();
        history.Add(new Trial(0.3f, new TrialCost(100, 60, 40, 50, 1)));
        history.Add(new Trial(0.9f, new TrialCost(200, 120, 80, 60, 2)));
        history.Add(new Trial(0.5f, new TrialCost(150, 80, 70, 55, 1)));
        Assert.Equal(0.9f, history.BestScore);
    }

    [Fact]
    public void TotalTokens_SumsAllTrialCosts()
    {
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(300, 150, 150, 50, 1)));
        history.Add(new Trial(0.6f, new TrialCost(200, 100, 100, 60, 2)));
        Assert.Equal(500L, history.TotalTokens);
    }

    [Fact]
    public void TotalApiCalls_SumsAllTrialCosts()
    {
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(100, 50, 50, 40, 3)));
        history.Add(new Trial(0.6f, new TrialCost(200, 100, 100, 60, 5)));
        Assert.Equal(8, history.TotalApiCalls);
    }

    [Fact]
    public void AddRange_AddsMultipleTrials()
    {
        var history = new TrialHistory();
        var trials = new[]
        {
            new Trial(0.1f, new TrialCost(10, 5, 5, 10, 1)),
            new Trial(0.2f, new TrialCost(20, 10, 10, 20, 1)),
        };
        history.AddRange(trials);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Add_Null_ThrowsArgumentNullException()
    {
        var history = new TrialHistory();
        Assert.Throws<ArgumentNullException>(() => history.Add(null!));
    }

    [Fact]
    public void Trials_ReturnsChronologicalList()
    {
        var history = new TrialHistory();
        var t1 = new Trial(0.3f, new TrialCost(100, 50, 50, 10, 1));
        var t2 = new Trial(0.7f, new TrialCost(200, 100, 100, 20, 2));
        history.Add(t1);
        history.Add(t2);
        Assert.Equal([t1, t2], history.Trials);
    }
}
