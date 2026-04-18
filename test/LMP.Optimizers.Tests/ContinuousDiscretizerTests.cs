using LMP.Optimizers;

namespace LMP.Tests;

public class ContinuousDiscretizerTests
{
    // ── From() validation ────────────────────────────────────────────────

    [Fact]
    public void From_ContinuousStepsLessThan2_Throws()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(0.0, 2.0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContinuousDiscretizer.From(space, continuousSteps: 1));
    }

    [Fact]
    public void From_LogScaleWithNonPositiveMin_Throws()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(0.0, 2.0, Scale.Log));

        Assert.Throws<ArgumentException>(() =>
            ContinuousDiscretizer.From(space, continuousSteps: 4));
    }

    [Fact]
    public void From_LogScaleWithNegativeMin_Throws()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(-1.0, 2.0, Scale.Log));

        Assert.Throws<ArgumentException>(() =>
            ContinuousDiscretizer.From(space, continuousSteps: 4));
    }

    // ── Linear Continuous grid ───────────────────────────────────────────

    [Fact]
    public void LinearGrid_HasCorrectCountAndEndpoints()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(0.0, 2.0));
        var disc = ContinuousDiscretizer.From(space, continuousSteps: 8);

        Assert.Equal(8, disc.Cardinalities["t"]);

        var decoded = disc.Decode(new Dictionary<string, int> { ["t"] = 0 });
        Assert.Equal(0.0, decoded.Get<double>("t"), precision: 10);

        decoded = disc.Decode(new Dictionary<string, int> { ["t"] = 7 });
        Assert.Equal(2.0, decoded.Get<double>("t"), precision: 10);
    }

    [Fact]
    public void LinearGrid_ValuesAreEquallySpaced()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(0.0, 1.0));
        var disc = ContinuousDiscretizer.From(space, continuousSteps: 5);

        var values = Enumerable.Range(0, 5)
            .Select(i => disc.Decode(new Dictionary<string, int> { ["t"] = i }).Get<double>("t"))
            .ToArray();

        double step = values[1] - values[0];
        for (int i = 1; i < values.Length; i++)
            Assert.Equal(step, values[i] - values[i - 1], precision: 10);
    }

    // ── Log Continuous grid ──────────────────────────────────────────────

    [Fact]
    public void LogGrid_HasCorrectCountAndEndpoints()
    {
        var space = TypedParameterSpace.Empty.Add("lr", new Continuous(1e-4, 1e-1, Scale.Log));
        var disc = ContinuousDiscretizer.From(space, continuousSteps: 6);

        Assert.Equal(6, disc.Cardinalities["lr"]);

        var first = disc.Decode(new Dictionary<string, int> { ["lr"] = 0 }).Get<double>("lr");
        var last = disc.Decode(new Dictionary<string, int> { ["lr"] = 5 }).Get<double>("lr");

        Assert.Equal(1e-4, first, precision: 12);
        Assert.Equal(1e-1, last, precision: 12);
    }

    [Fact]
    public void LogGrid_RatioBetweenConsecutiveValuesIsApproximatelyConstant()
    {
        var space = TypedParameterSpace.Empty.Add("lr", new Continuous(1e-3, 1e0, Scale.Log));
        var disc = ContinuousDiscretizer.From(space, continuousSteps: 4);

        var values = Enumerable.Range(0, 4)
            .Select(i => disc.Decode(new Dictionary<string, int> { ["lr"] = i }).Get<double>("lr"))
            .ToArray();

        double ratio0 = values[1] / values[0];
        double ratio1 = values[2] / values[1];
        double ratio2 = values[3] / values[2];

        Assert.Equal(ratio0, ratio1, precision: 8);
        Assert.Equal(ratio1, ratio2, precision: 8);
    }

    // ── Integer grid ─────────────────────────────────────────────────────

    [Fact]
    public void IntegerGrid_SmallRange_AllValuesPresent()
    {
        var space = TypedParameterSpace.Empty.Add("n", new Integer(1, 5));
        var disc = ContinuousDiscretizer.From(space);

        Assert.Equal(5, disc.Cardinalities["n"]);

        var values = Enumerable.Range(0, 5)
            .Select(i => disc.Decode(new Dictionary<string, int> { ["n"] = i }).Get<int>("n"))
            .ToArray();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, values);
    }

    [Fact]
    public void IntegerGrid_LargeRange_TwentyDedupedValues()
    {
        var space = TypedParameterSpace.Empty.Add("n", new Integer(1, 100));
        var disc = ContinuousDiscretizer.From(space);

        // Should have exactly 20 unique integers
        Assert.Equal(20, disc.Cardinalities["n"]);

        // All values should be in range
        for (int i = 0; i < 20; i++)
        {
            var val = disc.Decode(new Dictionary<string, int> { ["n"] = i }).Get<int>("n");
            Assert.InRange(val, 1, 100);
        }
    }

    // ── Categorical ──────────────────────────────────────────────────────

    [Fact]
    public void CategoricalParam_DecodesAsIntIndex()
    {
        var space = TypedParameterSpace.Empty.Add("demo", new Categorical(4));
        var disc = ContinuousDiscretizer.From(space);

        Assert.Equal(4, disc.Cardinalities["demo"]);

        for (int i = 0; i < 4; i++)
        {
            var val = disc.Decode(new Dictionary<string, int> { ["demo"] = i }).Get<int>("demo");
            Assert.Equal(i, val);
        }
    }

    // ── Skipping non-numeric params ──────────────────────────────────────

    [Fact]
    public void From_SkipsStringValuedAndSubset()
    {
        var space = TypedParameterSpace.Empty
            .Add("t", new Continuous(0.0, 1.0))
            .Add("instr", new StringValued("hello"))
            .Add("tools", new Subset(new List<object> { "a", "b" }));

        var disc = ContinuousDiscretizer.From(space, continuousSteps: 4);

        Assert.True(disc.Cardinalities.ContainsKey("t"));
        Assert.False(disc.Cardinalities.ContainsKey("instr"));
        Assert.False(disc.Cardinalities.ContainsKey("tools"));
    }

    // ── Encode/Decode round-trip ─────────────────────────────────────────

    [Fact]
    public void EncodeDecodeContinuous_RoundTrip()
    {
        var space = TypedParameterSpace.Empty.Add("t", new Continuous(0.0, 2.0));
        var disc = ContinuousDiscretizer.From(space, continuousSteps: 8);

        for (int i = 0; i < 8; i++)
        {
            var catConfig = new Dictionary<string, int> { ["t"] = i };
            var assignment = disc.Decode(catConfig);
            var encoded = disc.Encode(assignment);
            Assert.Equal(i, encoded["t"]);
        }
    }

    [Fact]
    public void EncodeDecodeInteger_RoundTrip()
    {
        var space = TypedParameterSpace.Empty.Add("n", new Integer(1, 5));
        var disc = ContinuousDiscretizer.From(space);

        for (int i = 0; i < 5; i++)
        {
            var catConfig = new Dictionary<string, int> { ["n"] = i };
            var assignment = disc.Decode(catConfig);
            var encoded = disc.Encode(assignment);
            Assert.Equal(i, encoded["n"]);
        }
    }

    [Fact]
    public void EncodeDecodeCategorical_RoundTrip()
    {
        var space = TypedParameterSpace.Empty.Add("cls", new Categorical(6));
        var disc = ContinuousDiscretizer.From(space);

        for (int i = 0; i < 6; i++)
        {
            var catConfig = new Dictionary<string, int> { ["cls"] = i };
            var assignment = disc.Decode(catConfig);
            var encoded = disc.Encode(assignment);
            Assert.Equal(i, encoded["cls"]);
        }
    }
}
