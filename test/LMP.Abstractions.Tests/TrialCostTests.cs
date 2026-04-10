namespace LMP.Tests;

public class TrialCostTests
{
    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 600, 400, 250, 3);

        Assert.Equal(cost1, cost2);
    }

    [Fact]
    public void RecordEquality_DifferentTotalTokens_AreNotEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(999, 600, 400, 250, 3);

        Assert.NotEqual(cost1, cost2);
    }

    [Fact]
    public void RecordEquality_DifferentInputTokens_AreNotEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 500, 400, 250, 3);

        Assert.NotEqual(cost1, cost2);
    }

    [Fact]
    public void RecordEquality_DifferentOutputTokens_AreNotEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 600, 300, 250, 3);

        Assert.NotEqual(cost1, cost2);
    }

    [Fact]
    public void RecordEquality_DifferentElapsedMs_AreNotEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 600, 400, 100, 3);

        Assert.NotEqual(cost1, cost2);
    }

    [Fact]
    public void RecordEquality_DifferentApiCalls_AreNotEqual()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 600, 400, 250, 1);

        Assert.NotEqual(cost1, cost2);
    }

    [Fact]
    public void Properties_ReturnConstructorValues()
    {
        var cost = new TrialCost(1500, 800, 700, 500, 5);

        Assert.Equal(1500, cost.TotalTokens);
        Assert.Equal(800, cost.InputTokens);
        Assert.Equal(700, cost.OutputTokens);
        Assert.Equal(500, cost.ElapsedMilliseconds);
        Assert.Equal(5, cost.ApiCalls);
    }

    [Fact]
    public void With_CreatesModifiedCopy()
    {
        var original = new TrialCost(1000, 600, 400, 250, 3);
        var modified = original with { TotalTokens = 2000 };

        Assert.Equal(2000, modified.TotalTokens);
        Assert.Equal(600, modified.InputTokens);
        Assert.NotEqual(original, modified);
    }

    [Fact]
    public void GetHashCode_EqualInstances_SameHash()
    {
        var cost1 = new TrialCost(1000, 600, 400, 250, 3);
        var cost2 = new TrialCost(1000, 600, 400, 250, 3);

        Assert.Equal(cost1.GetHashCode(), cost2.GetHashCode());
    }

    [Fact]
    public void ToString_ContainsPropertyValues()
    {
        var cost = new TrialCost(1000, 600, 400, 250, 3);
        var str = cost.ToString();

        Assert.Contains("1000", str);
        Assert.Contains("600", str);
        Assert.Contains("400", str);
        Assert.Contains("250", str);
        Assert.Contains("3", str);
    }

    [Fact]
    public void ZeroCost_IsValid()
    {
        var cost = new TrialCost(0, 0, 0, 0, 0);

        Assert.Equal(0, cost.TotalTokens);
        Assert.Equal(0, cost.InputTokens);
        Assert.Equal(0, cost.OutputTokens);
        Assert.Equal(0, cost.ElapsedMilliseconds);
        Assert.Equal(0, cost.ApiCalls);
    }
}
