namespace LMP.Tests;

public class ExceptionTests
{
    [Fact]
    public void LmpAssertionException_SetsMessageAndFailedResult()
    {
        var ex = new LmpAssertionException("Urgency out of range", 99);

        Assert.Equal("Urgency out of range", ex.Message);
        Assert.Equal(99, ex.FailedResult);
    }

    [Fact]
    public void LmpAssertionException_NullFailedResult()
    {
        var ex = new LmpAssertionException("Failed", null);

        Assert.Equal("Failed", ex.Message);
        Assert.Null(ex.FailedResult);
    }

    [Fact]
    public void LmpAssertionException_IsException()
    {
        var ex = new LmpAssertionException("test", "result");

        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void LmpMaxRetriesExceededException_SetsProperties()
    {
        var ex = new LmpMaxRetriesExceededException("classify", 3);

        Assert.Equal("classify", ex.PredictorName);
        Assert.Equal(3, ex.MaxRetries);
        Assert.Contains("classify", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void LmpMaxRetriesExceededException_IsException()
    {
        var ex = new LmpMaxRetriesExceededException("test", 5);

        Assert.IsAssignableFrom<Exception>(ex);
    }
}
