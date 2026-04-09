namespace LMP.Tests;

public class LmpAssertTests
{
    [Fact]
    public void That_PassingPredicate_DoesNotThrow()
    {
        var result = 42;

        LmpAssert.That(result, r => r == 42, "Should be 42");
    }

    [Fact]
    public void That_FailingPredicate_ThrowsLmpAssertionException()
    {
        var result = 42;

        var ex = Assert.Throws<LmpAssertionException>(
            () => LmpAssert.That(result, r => r == 99, "Expected 99"));

        Assert.Equal("Expected 99", ex.Message);
        Assert.Equal(42, ex.FailedResult);
    }

    [Fact]
    public void That_FailingPredicate_NoMessage_UsesDefaultMessage()
    {
        var ex = Assert.Throws<LmpAssertionException>(
            () => LmpAssert.That("bad", _ => false));

        Assert.Equal("LMP assertion failed.", ex.Message);
        Assert.Equal("bad", ex.FailedResult);
    }

    [Fact]
    public void That_WorksWithComplexTypes()
    {
        var result = new { Category = "billing", Urgency = 4 };

        LmpAssert.That(result,
            r => r.Urgency >= 1 && r.Urgency <= 5,
            "Urgency must be between 1 and 5");
    }
}
