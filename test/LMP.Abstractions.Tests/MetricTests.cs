namespace LMP.Tests;

public class MetricTests
{
    private record TestOutput(string Answer, int Score);

    [Fact]
    public void Create_Float_ReturnsTypedScore()
    {
        var metric = Metric.Create<TestOutput, TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer ? 1f : 0f);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));
        var output = new TestOutput("yes", 5);

        Assert.Equal(1f, metric(example, output));
    }

    [Fact]
    public void Create_Float_PartialCredit()
    {
        var metric = Metric.Create<TestOutput, TestOutput>((predicted, expected) =>
            predicted.Score == expected.Score ? 1f : 0.5f);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(1f, metric(example, new TestOutput("no", 5)));
        Assert.Equal(0.5f, metric(example, new TestOutput("no", 3)));
    }

    [Fact]
    public void Create_Bool_TrueMapsToOne()
    {
        var metric = Metric.Create<TestOutput, TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(1f, metric(example, new TestOutput("yes", 99)));
    }

    [Fact]
    public void Create_Bool_FalseMapsToZero()
    {
        var metric = Metric.Create<TestOutput, TestOutput>((predicted, expected) =>
            predicted.Answer == expected.Answer);

        var example = new Example<string, TestOutput>("q", new TestOutput("yes", 5));

        Assert.Equal(0f, metric(example, new TestOutput("no", 5)));
    }

    [Fact]
    public void Create_Float_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<TestOutput, TestOutput>((Func<TestOutput, TestOutput, float>)null!));
    }

    [Fact]
    public void Create_Bool_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<TestOutput, TestOutput>((Func<TestOutput, TestOutput, bool>)null!));
    }

    [Fact]
    public void Create_Float_CastsOutputCorrectly()
    {
        var metric = Metric.Create<TestOutput, TestOutput>((predicted, expected) =>
            predicted.Score / (float)expected.Score);

        var example = new Example<string, TestOutput>("q", new TestOutput("a", 10));
        object output = new TestOutput("b", 7);

        Assert.Equal(0.7f, metric(example, output));
    }

    // ── Cross-type tests (TPredicted ≠ TExpected) ──

    private record RichOutput(string Answer, float Confidence, string Reasoning);
    private record SimpleLabel(string Answer);

    [Fact]
    public void Create_CrossType_Float_ComparesFieldsAcrossTypes()
    {
        var metric = Metric.Create<RichOutput, SimpleLabel>(
            (predicted, expected) => predicted.Answer == expected.Answer ? 1f : 0f);

        var example = new Example<string, SimpleLabel>("q", new SimpleLabel("Paris"));
        var output = new RichOutput("Paris", 0.95f, "France's capital");

        Assert.Equal(1f, metric(example, output));
    }

    [Fact]
    public void Create_CrossType_Float_Mismatch()
    {
        var metric = Metric.Create<RichOutput, SimpleLabel>(
            (predicted, expected) => predicted.Answer == expected.Answer ? 1f : 0f);

        var example = new Example<string, SimpleLabel>("q", new SimpleLabel("Paris"));
        var output = new RichOutput("London", 0.8f, "UK capital");

        Assert.Equal(0f, metric(example, output));
    }

    [Fact]
    public void Create_CrossType_Float_CanUseExtraFields()
    {
        // Metric uses Confidence from predicted (not available in ground truth)
        var metric = Metric.Create<RichOutput, SimpleLabel>(
            (predicted, expected) =>
                predicted.Answer == expected.Answer ? predicted.Confidence : 0f);

        var example = new Example<string, SimpleLabel>("q", new SimpleLabel("Paris"));

        Assert.Equal(0.95f, metric(example, new RichOutput("Paris", 0.95f, "reasoning")));
        Assert.Equal(0f, metric(example, new RichOutput("London", 0.8f, "reasoning")));
    }

    [Fact]
    public void Create_CrossType_Bool_TrueMapsToOne()
    {
        var metric = Metric.Create<RichOutput, SimpleLabel>(
            (predicted, expected) => predicted.Answer == expected.Answer);

        var example = new Example<string, SimpleLabel>("q", new SimpleLabel("Paris"));

        Assert.Equal(1f, metric(example, new RichOutput("Paris", 0.5f, "r")));
    }

    [Fact]
    public void Create_CrossType_Bool_FalseMapsToZero()
    {
        var metric = Metric.Create<RichOutput, SimpleLabel>(
            (predicted, expected) => predicted.Answer == expected.Answer);

        var example = new Example<string, SimpleLabel>("q", new SimpleLabel("Paris"));

        Assert.Equal(0f, metric(example, new RichOutput("London", 0.9f, "r")));
    }

    [Fact]
    public void Create_CrossType_Float_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<RichOutput, SimpleLabel>((Func<RichOutput, SimpleLabel, float>)null!));
    }

    [Fact]
    public void Create_CrossType_Bool_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Metric.Create<RichOutput, SimpleLabel>((Func<RichOutput, SimpleLabel, bool>)null!));
    }

    [Fact]
    public void Create_CrossType_StringLabel()
    {
        // Module produces structured type, ground truth is just a string
        var metric = Metric.Create<RichOutput, string>(
            (predicted, expected) => predicted.Answer == expected ? 1f : 0f);

        var example = new Example<string, string>("q", "Paris");

        Assert.Equal(1f, metric(example, new RichOutput("Paris", 0.9f, "r")));
        Assert.Equal(0f, metric(example, new RichOutput("London", 0.9f, "r")));
    }
}
