namespace LMP.Tests;

public class LmpSignatureAttributeTests
{
    [Fact]
    public void Constructor_SetsInstructions()
    {
        var attr = new LmpSignatureAttribute("Classify a support ticket");

        Assert.Equal("Classify a support ticket", attr.Instructions);
    }

    [Fact]
    public void AttributeUsage_IsCorrect()
    {
        var usage = typeof(LmpSignatureAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }

    [Fact]
    public void Instructions_RoundTrips()
    {
        const string instructions = "Generate a summary of the input text";
        var attr = new LmpSignatureAttribute(instructions);

        Assert.Equal(instructions, attr.Instructions);
    }
}
