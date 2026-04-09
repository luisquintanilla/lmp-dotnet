namespace LMP.Tests;

public class TraceTests
{
    [Fact]
    public void NewTrace_HasEmptyEntries()
    {
        var trace = new Trace();

        Assert.Empty(trace.Entries);
    }

    [Fact]
    public void Record_AddsEntry()
    {
        var trace = new Trace();

        trace.Record("classify", "input text", "billing");

        Assert.Single(trace.Entries);
        Assert.Equal("classify", trace.Entries[0].PredictorName);
        Assert.Equal("input text", trace.Entries[0].Input);
        Assert.Equal("billing", trace.Entries[0].Output);
    }

    [Fact]
    public void Record_MultipleEntries_PreservesOrder()
    {
        var trace = new Trace();

        trace.Record("classify", "input1", "output1");
        trace.Record("draft", "input2", "output2");
        trace.Record("review", "input3", "output3");

        Assert.Equal(3, trace.Entries.Count);
        Assert.Equal("classify", trace.Entries[0].PredictorName);
        Assert.Equal("draft", trace.Entries[1].PredictorName);
        Assert.Equal("review", trace.Entries[2].PredictorName);
    }
}

public class TraceEntryTests
{
    [Fact]
    public void RecordEquality_Works()
    {
        var entry1 = new TraceEntry("classify", "input", "output");
        var entry2 = new TraceEntry("classify", "input", "output");

        Assert.Equal(entry1, entry2);
    }

    [Fact]
    public void RecordEquality_DifferentValues_NotEqual()
    {
        var entry1 = new TraceEntry("classify", "input", "output1");
        var entry2 = new TraceEntry("classify", "input", "output2");

        Assert.NotEqual(entry1, entry2);
    }
}
