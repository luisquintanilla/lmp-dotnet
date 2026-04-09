namespace LMP.Tests;

public class LmpSuggestTests
{
    [Fact]
    public void That_PassingPredicate_ReturnsTrue()
    {
        var result = LmpSuggest.That(42, r => r == 42, "Should be 42");

        Assert.True(result);
    }

    [Fact]
    public void That_FailingPredicate_ReturnsFalse()
    {
        var result = LmpSuggest.That(42, r => r == 99, "Expected 99");

        Assert.False(result);
    }

    [Fact]
    public void That_FailingPredicate_NeverThrows()
    {
        var exception = Record.Exception(
            () => LmpSuggest.That("bad", _ => false, "Always fails"));

        Assert.Null(exception);
    }

    [Fact]
    public void That_NoMessage_StillWorks()
    {
        Assert.False(LmpSuggest.That(0, r => r > 0));
        Assert.True(LmpSuggest.That(1, r => r > 0));
    }
}
