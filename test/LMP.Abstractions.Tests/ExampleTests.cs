namespace LMP.Tests;

public class ExampleTests
{
    [Fact]
    public void Constructor_SetsInputAndLabel()
    {
        var example = new Example<string, int>("hello", 42);

        Assert.Equal("hello", example.Input);
        Assert.Equal(42, example.Label);
    }

    [Fact]
    public void WithInputs_ReturnsInput()
    {
        var example = new Example<string, int>("test input", 99);

        var input = example.WithInputs();

        Assert.Equal("test input", input);
    }

    [Fact]
    public void RecordEquality_Works()
    {
        var example1 = new Example<string, int>("hello", 42);
        var example2 = new Example<string, int>("hello", 42);

        Assert.Equal(example1, example2);
    }

    [Fact]
    public void RecordEquality_DifferentValues_NotEqual()
    {
        var example1 = new Example<string, int>("hello", 42);
        var example2 = new Example<string, int>("world", 42);

        Assert.NotEqual(example1, example2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new Example<string, int>("hello", 42);

        var modified = original with { Label = 100 };

        Assert.Equal(42, original.Label);
        Assert.Equal(100, modified.Label);
        Assert.Equal("hello", modified.Input);
    }

    [Fact]
    public void ComplexTypes_Work()
    {
        var input = new { Text = "ticket text", Tier = "pro" };
        var label = new { Category = "billing", Urgency = 4 };
        var example = new Example<object, object>(input, label);

        Assert.Same(input, example.Input);
        Assert.Same(label, example.Label);
        Assert.Same(input, example.WithInputs());
    }
}
