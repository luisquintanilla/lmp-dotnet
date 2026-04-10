using Microsoft.Extensions.AI;

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

    [Fact]
    public void Record_WithUsage_StoresUsageDetails()
    {
        var trace = new Trace();
        var usage = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 50,
            TotalTokenCount = 150
        };

        trace.Record("classify", "input", "output", usage);

        Assert.Single(trace.Entries);
        Assert.NotNull(trace.Entries[0].Usage);
        Assert.Equal(100, trace.Entries[0].Usage!.InputTokenCount);
        Assert.Equal(50, trace.Entries[0].Usage!.OutputTokenCount);
        Assert.Equal(150, trace.Entries[0].Usage!.TotalTokenCount);
    }

    [Fact]
    public void Record_WithoutUsage_UsageIsNull()
    {
        var trace = new Trace();

        trace.Record("classify", "input", "output");

        Assert.Null(trace.Entries[0].Usage);
    }

    [Fact]
    public void TotalTokens_SumsAllEntries()
    {
        var trace = new Trace();
        trace.Record("step1", "in1", "out1", new UsageDetails { TotalTokenCount = 100 });
        trace.Record("step2", "in2", "out2", new UsageDetails { TotalTokenCount = 200 });
        trace.Record("step3", "in3", "out3", new UsageDetails { TotalTokenCount = 50 });

        Assert.Equal(350, trace.TotalTokens);
    }

    [Fact]
    public void TotalTokens_IgnoresNullUsage()
    {
        var trace = new Trace();
        trace.Record("step1", "in1", "out1", new UsageDetails { TotalTokenCount = 100 });
        trace.Record("step2", "in2", "out2"); // no usage
        trace.Record("step3", "in3", "out3", new UsageDetails { TotalTokenCount = 50 });

        Assert.Equal(150, trace.TotalTokens);
    }

    [Fact]
    public void TotalTokens_ReturnsZero_WhenNoEntries()
    {
        var trace = new Trace();

        Assert.Equal(0, trace.TotalTokens);
    }

    [Fact]
    public void TotalTokens_ReturnsZero_WhenAllUsageNull()
    {
        var trace = new Trace();
        trace.Record("step1", "in1", "out1");
        trace.Record("step2", "in2", "out2");

        Assert.Equal(0, trace.TotalTokens);
    }

    [Fact]
    public void TotalApiCalls_CountsEntriesWithUsage()
    {
        var trace = new Trace();
        trace.Record("step1", "in1", "out1", new UsageDetails { TotalTokenCount = 100 });
        trace.Record("step2", "in2", "out2"); // no usage — not an API call
        trace.Record("step3", "in3", "out3", new UsageDetails { TotalTokenCount = 50 });

        Assert.Equal(2, trace.TotalApiCalls);
    }

    [Fact]
    public void TotalApiCalls_ReturnsZero_WhenNoEntries()
    {
        var trace = new Trace();

        Assert.Equal(0, trace.TotalApiCalls);
    }

    [Fact]
    public void TotalApiCalls_ReturnsZero_WhenAllUsageNull()
    {
        var trace = new Trace();
        trace.Record("step1", "in1", "out1");
        trace.Record("step2", "in2", "out2");

        Assert.Equal(0, trace.TotalApiCalls);
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

    [Fact]
    public void RecordEquality_WithUsage_Works()
    {
        var usage = new UsageDetails { TotalTokenCount = 100 };
        var entry1 = new TraceEntry("classify", "input", "output", usage);
        var entry2 = new TraceEntry("classify", "input", "output", usage);

        Assert.Equal(entry1, entry2);
    }

    [Fact]
    public void RecordEquality_NullVsNonNullUsage_NotEqual()
    {
        var entry1 = new TraceEntry("classify", "input", "output");
        var entry2 = new TraceEntry("classify", "input", "output", new UsageDetails { TotalTokenCount = 100 });

        Assert.NotEqual(entry1, entry2);
    }
}
