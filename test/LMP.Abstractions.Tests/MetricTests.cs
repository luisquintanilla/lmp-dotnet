namespace LMP.Tests;

public class MetricTests
{
    private record TestOutput(string Answer, int Score);

    [Fact]
    public void Create_Float_ReturnsTypedScore()
    {
        var metric = Metric.Create<TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer ? 1f : 0f);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));
        var output = new TestOutput("yes", 5);

        Assert.Equal(1f, metric(example, output));
    }

    [Fact]
    public void Create_Float_PartialCredit()
    {
        var metric = Metric.Create<TestOutput>((predicted, expected) =>
            predicted.Score == expected.Score ? 1f : 0.5f);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(1f, metric(example, new TestOutput("no", 5)));
        Assert.Equal(0.5f, metric(example, new TestOutput("no", 3)));
    }

    [Fact]
    public void Create_Bool_TrueMapsToOne()
    {
        var metric = Metric.Create<TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(1f, metric(example, new TestOutput("yes", 99)));
    }

    [Fact]
    public void Create_Bool_FalseMapsToZero()
    {
        var metric = Metric.Create<TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(0f, metric(example, new TestOutput("no", 5)));
    }

    [Fact]
    public void Create_Float_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<TestOutput>((Func<TestOutput, TestOutput, float>)null!));
    }

    [Fact]
    public void Create_Bool_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<TestOutput>((Func<TestOutput, TestOutput, bool>)null!));
    }

    [Fact]
    public void Create_Float_CastsOutputCorrectly()
    {
        var metric = Metric.Create<TestOutput>((predicted, expected) =>
            predicted.Score / (float)expected.Score);

        var example = new Example<string, TestOutput>("q", new TestOutput("a", 10));
        object output = new TestOutput("b", 7);

        Assert.Equal(0.7f, metric(example, output));
    }
}
