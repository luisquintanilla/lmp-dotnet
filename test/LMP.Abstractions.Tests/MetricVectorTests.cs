using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Xunit;

namespace LMP.Tests;

public class MetricVectorTests
{
    [Fact]
    public void DefaultCtor_HasZeroValues()
    {
        var v = new MetricVector(0f);
        Assert.Equal(0f, v.Score);
        Assert.Equal(0L, v.Tokens);
        Assert.Equal(0.0, v.LatencyMs);
        Assert.Equal(0, v.Turns);
        Assert.Empty(v.Custom);
    }

    [Fact]
    public void ParameterCtor_SetsAllFields()
    {
        var custom = ImmutableDictionary<string, float>.Empty.Add("cost_usd", 0.02f);
        var v = new MetricVector(0.9f, 1000L, 250.0, 3, custom);
        Assert.Equal(0.9f, v.Score);
        Assert.Equal(1000L, v.Tokens);
        Assert.Equal(250.0, v.LatencyMs);
        Assert.Equal(3, v.Turns);
        Assert.Equal(0.02f, v.Custom["cost_usd"]);
    }

    [Fact]
    public void FromScore_PopulatesOnlyScore()
    {
        var v = MetricVector.FromScore(0.75f);
        Assert.Equal(0.75f, v.Score);
        Assert.Equal(0L, v.Tokens);
        Assert.Equal(0, v.Turns);
    }

    [Fact]
    public void FromTrace_PopulatesFromTrace()
    {
        var trace = new Trace();
        trace.Record("pred", "input", "output",
            new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 });
        var v = MetricVector.FromTrace(0.8f, trace, elapsedMs: 120.0);
        Assert.Equal(0.8f, v.Score);
        Assert.Equal(30L, v.Tokens);
        Assert.Equal(120.0, v.LatencyMs);
        Assert.Equal(1, v.Turns);
    }

    [Fact]
    public void Dominates_StrictlyBetterOnAll_ReturnsTrue()
    {
        var better = new MetricVector(0.9f, 500L, 0, 1);
        var worse = new MetricVector(0.7f, 800L, 0, 3);
        Assert.True(better.Dominates(worse));
    }

    [Fact]
    public void Dominates_BetterOnOneWeaklyOnOthers_ReturnsTrue()
    {
        var a = new MetricVector(0.8f, 500L, 0, 2);
        var b = new MetricVector(0.8f, 800L, 0, 2); // same score+turns, more tokens
        Assert.True(a.Dominates(b)); // a has fewer tokens (strictly)
    }

    [Fact]
    public void Dominates_InferiorOnAnyDimension_ReturnsFalse()
    {
        var a = new MetricVector(0.9f, 1000L, 0, 1); // better score, worse tokens
        var b = new MetricVector(0.7f, 500L, 0, 1);
        Assert.False(a.Dominates(b)); // a has more tokens — not weakly better on tokens
    }

    [Fact]
    public void Dominates_Equal_ReturnsFalse()
    {
        var a = new MetricVector(0.8f, 500L, 0, 2);
        Assert.False(a.Dominates(a)); // equal ≠ dominance (must be strictly better on one)
    }

    [Fact]
    public void Dominates_Incomparable_ReturnsFalse()
    {
        var a = new MetricVector(0.9f, 1000L, 0, 1);
        var b = new MetricVector(0.7f, 200L, 0, 1);
        Assert.False(a.Dominates(b)); // a has better score but worse tokens
        Assert.False(b.Dominates(a)); // b has better tokens but worse score
    }

    [Fact]
    public void Dominates_LatencyExcluded_NotAffectingResult()
    {
        var highLatency = new MetricVector(0.9f, 500L, 5000.0, 1);
        var lowLatency = new MetricVector(0.8f, 500L, 10.0, 1);
        // highLatency dominates on score; lowLatency is NOT dominated just because latency is lower
        Assert.True(highLatency.Dominates(lowLatency));
    }

    [Fact]
    public void Equality_SameValues_IsEqual()
    {
        var a = new MetricVector(0.5f, 100L, 50.0, 2);
        var b = new MetricVector(0.5f, 100L, 50.0, 2);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equality_DifferentValues_NotEqual()
    {
        var a = new MetricVector(0.5f, 100L, 50.0, 2);
        var b = new MetricVector(0.6f, 100L, 50.0, 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_ContainsAllDimensions()
    {
        var v = new MetricVector(0.8f, 500L, 200.0, 3);
        var str = v.ToString();
        Assert.Contains("Score=0.800", str);
        Assert.Contains("Tokens=500", str);
        Assert.Contains("Turns=3", str);
    }
}
