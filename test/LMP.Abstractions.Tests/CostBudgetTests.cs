namespace LMP.Tests;

public class CostBudgetTests
{
    [Fact]
    public void Unlimited_IsWithinBudget_Always()
    {
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(1_000_000, 500_000, 500_000, 10_000, 1000)));
        Assert.True(CostBudget.Unlimited.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTokens_BelowLimit_IsWithinBudget()
    {
        var budget = new CostBudget { MaxTokens = 1000 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(500, 250, 250, 10, 1)));
        Assert.True(budget.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTokens_AtLimit_ExceedsBudget()
    {
        var budget = new CostBudget { MaxTokens = 1000 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(1000, 500, 500, 10, 1)));
        Assert.False(budget.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTokens_AboveLimit_ExceedsBudget()
    {
        var budget = new CostBudget { MaxTokens = 500 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(1000, 500, 500, 10, 1)));
        Assert.False(budget.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTurns_BelowLimit_IsWithinBudget()
    {
        var budget = new CostBudget { MaxTurns = 5 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(100, 50, 50, 10, 3)));
        Assert.True(budget.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTurns_AtLimit_ExceedsBudget()
    {
        var budget = new CostBudget { MaxTurns = 5 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(100, 50, 50, 10, 5)));
        Assert.False(budget.IsWithinBudget(history));
    }

    [Fact]
    public void MaxTokens_MultipleTrials_SumsAllTokens()
    {
        var budget = new CostBudget { MaxTokens = 999 };
        var history = new TrialHistory();
        history.Add(new Trial(0.5f, new TrialCost(500, 250, 250, 10, 1)));
        history.Add(new Trial(0.6f, new TrialCost(500, 250, 250, 10, 1)));
        // total = 1000 >= 999 → exceeds budget
        Assert.False(budget.IsWithinBudget(history));
    }

    [Fact]
    public void Builder_MaxTokens_SetsCorrectBudget()
    {
        var budget = new CostBudget.Builder().MaxTokens(5000).Build();
        Assert.Equal(5000, budget.MaxTokens);
    }

    [Fact]
    public void Builder_MaxTurns_SetsCorrectBudget()
    {
        var budget = new CostBudget.Builder().MaxTurns(10).Build();
        Assert.Equal(10, budget.MaxTurns);
    }

    [Fact]
    public void Builder_MaxSeconds_SetsWallClock()
    {
        var budget = new CostBudget.Builder().MaxSeconds(30).Build();
        Assert.Equal(TimeSpan.FromSeconds(30), budget.MaxWallClock);
    }

    [Fact]
    public void Builder_Custom_SetsCustomPredicate()
    {
        Func<TrialCost, bool> predicate = _ => false;
        var budget = new CostBudget.Builder().Custom(predicate).Build();
        Assert.Same(predicate, budget.Custom);
    }

    [Fact]
    public void IsWithinBudget_NullHistory_Throws()
    {
        Assert.Throws<ArgumentNullException>(()
            => CostBudget.Unlimited.IsWithinBudget(null!));
    }
}
