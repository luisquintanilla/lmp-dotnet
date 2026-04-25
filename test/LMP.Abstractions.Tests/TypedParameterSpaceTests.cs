namespace LMP.Tests;

public class TypedParameterSpaceTests
{
    // ── Empty / singleton ────────────────────────────────────────────────

    [Fact]
    public void Empty_HasNoParameters()
    {
        Assert.True(TypedParameterSpace.Empty.IsEmpty);
        Assert.Empty(TypedParameterSpace.Empty.Parameters);
    }

    [Fact]
    public void Empty_HasNoContinuous()
        => Assert.False(TypedParameterSpace.Empty.HasContinuous);

    [Fact]
    public void Empty_HasNoSubset()
        => Assert.False(TypedParameterSpace.Empty.HasSubset);

    // ── Add ──────────────────────────────────────────────────────────────

    [Fact]
    public void Add_AddsCategoricalParameter()
    {
        var space = TypedParameterSpace.Empty.Add("demo_count", new Categorical(4));
        Assert.False(space.IsEmpty);
        Assert.True(space.Parameters.ContainsKey("demo_count"));
        Assert.IsType<Categorical>(space.Parameters["demo_count"]);
        Assert.Equal(4, ((Categorical)space.Parameters["demo_count"]).Count);
    }

    [Fact]
    public void Add_OverwritesExistingKey()
    {
        var space = TypedParameterSpace.Empty
            .Add("x", new Categorical(3))
            .Add("x", new Integer(0, 10));
        Assert.IsType<Integer>(space.Parameters["x"]);
    }

    [Fact]
    public void Add_IsImmutable()
    {
        var original = TypedParameterSpace.Empty;
        var updated = original.Add("x", new Categorical(2));
        Assert.True(original.IsEmpty);
        Assert.False(updated.IsEmpty);
    }

    [Fact]
    public void Add_NullNameThrows()
        => Assert.Throws<ArgumentException>(() => TypedParameterSpace.Empty.Add("", new Categorical(1)));

    [Fact]
    public void Add_NullKindThrows()
        => Assert.Throws<ArgumentNullException>(() => TypedParameterSpace.Empty.Add("x", null!));

    // ── Remove ───────────────────────────────────────────────────────────

    [Fact]
    public void Remove_RemovesParameter()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(2)).Remove("x");
        Assert.True(space.IsEmpty);
    }

    [Fact]
    public void Remove_MissingKey_ReturnsSame()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(2));
        var result = space.Remove("y");
        Assert.Same(space, result);
    }

    // ── Merge ────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_CombinesDistinctKeys()
    {
        var a = TypedParameterSpace.Empty.Add("x", new Categorical(2));
        var b = TypedParameterSpace.Empty.Add("y", new Categorical(3));
        var merged = a.Merge(b);
        Assert.Equal(2, merged.Parameters.Count);
        Assert.True(merged.Parameters.ContainsKey("x"));
        Assert.True(merged.Parameters.ContainsKey("y"));
    }

    [Fact]
    public void Merge_OtherWinsOnCollision()
    {
        var a = TypedParameterSpace.Empty.Add("x", new Categorical(2));
        var b = TypedParameterSpace.Empty.Add("x", new Categorical(5));
        var merged = a.Merge(b);
        Assert.Equal(5, ((Categorical)merged.Parameters["x"]).Count);
    }

    [Fact]
    public void Merge_WithEmpty_ReturnsSelf()
    {
        var a = TypedParameterSpace.Empty.Add("x", new Categorical(2));
        Assert.Same(a, a.Merge(TypedParameterSpace.Empty));
    }

    [Fact]
    public void Merge_FromEmpty_ReturnsOther()
    {
        var b = TypedParameterSpace.Empty.Add("x", new Categorical(2));
        Assert.Same(b, TypedParameterSpace.Empty.Merge(b));
    }

    // ── HasContinuous / HasSubset ────────────────────────────────────────

    [Fact]
    public void HasContinuous_TrueWhenContinuousPresent()
    {
        var space = TypedParameterSpace.Empty.Add("temp", new Continuous(0.0, 2.0));
        Assert.True(space.HasContinuous);
    }

    [Fact]
    public void HasContinuous_FalseWhenNoContinuous()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(3));
        Assert.False(space.HasContinuous);
    }

    [Fact]
    public void HasSubset_TrueWhenSubsetPresent()
    {
        var space = TypedParameterSpace.Empty
            .Add("tools", new Subset(new List<object> { "tool1", "tool2" }, 1, 2));
        Assert.True(space.HasSubset);
    }

    [Fact]
    public void HasSubset_FalseWhenNoSubset()
    {
        var space = TypedParameterSpace.Empty.Add("x", new Categorical(3));
        Assert.False(space.HasSubset);
    }

    // ── All 6 ParameterKind variants ─────────────────────────────────────

    [Fact]
    public void Add_Integer_RoundTrips()
    {
        var space = TypedParameterSpace.Empty.Add("k", new Integer(1, 8));
        var kind = (Integer)space.Parameters["k"];
        Assert.Equal(1, kind.Min);
        Assert.Equal(8, kind.Max);
    }

    [Fact]
    public void Add_Continuous_RoundTrips()
    {
        var space = TypedParameterSpace.Empty.Add("lr", new Continuous(1e-5, 1e-1, Scale.Log));
        var kind = (Continuous)space.Parameters["lr"];
        Assert.Equal(Scale.Log, kind.Scale);
    }

    [Fact]
    public void Add_StringValued_RoundTrips()
    {
        var space = TypedParameterSpace.Empty.Add("instr", new StringValued("Answer the question."));
        var kind = (StringValued)space.Parameters["instr"];
        Assert.Equal("Answer the question.", kind.InitialValue);
    }

    [Fact]
    public void Add_Subset_RoundTrips()
    {
        var pool = new List<object> { "toolA", "toolB", "toolC" };
        var space = TypedParameterSpace.Empty.Add("tools", new Subset(pool, 1, 2));
        var kind = (Subset)space.Parameters["tools"];
        Assert.Equal(3, kind.Pool.Count);
        Assert.Equal(1, kind.MinSize);
        Assert.Equal(2, kind.MaxSize);
    }

    [Fact]
    public void Add_Composite_RoundTrips()
    {
        var inner = TypedParameterSpace.Empty.Add("demo", new Categorical(3));
        var space = TypedParameterSpace.Empty.Add("nested", new Composite(inner));
        var kind = (Composite)space.Parameters["nested"];
        Assert.False(kind.Members.IsEmpty);
    }

    // ── FromCategorical / ToCategoricalDictionary ────────────────────────

    [Fact]
    public void FromCategorical_CreatesAllCategoricalParameters()
    {
        var cardinalities = new Dictionary<string, int> { ["a"] = 3, ["b"] = 5 };
        var space = TypedParameterSpace.FromCategorical(cardinalities);
        Assert.Equal(2, space.Parameters.Count);
        Assert.IsType<Categorical>(space.Parameters["a"]);
        Assert.Equal(3, ((Categorical)space.Parameters["a"]).Count);
        Assert.Equal(5, ((Categorical)space.Parameters["b"]).Count);
    }

    [Fact]
    public void ToCategoricalDictionary_RoundTrips()
    {
        var original = new Dictionary<string, int> { ["x"] = 4, ["y"] = 7 };
        var space = TypedParameterSpace.FromCategorical(original);
        var result = space.ToCategoricalDictionary();
        Assert.Equal(original, result);
    }

    [Fact]
    public void ToCategoricalDictionary_DropsNonCategorical()
    {
        var space = TypedParameterSpace.Empty
            .Add("cat", new Categorical(3))
            .Add("cont", new Continuous(0.0, 1.0));
        var dict = space.ToCategoricalDictionary();
        Assert.Single(dict);
        Assert.True(dict.ContainsKey("cat"));
        Assert.False(dict.ContainsKey("cont"));
    }

    [Fact]
    public void FromCategorical_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => TypedParameterSpace.FromCategorical(null!));
}
