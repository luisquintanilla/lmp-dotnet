namespace LMP.Tests;

public class ParameterAssignmentTests
{
    // ── Empty / singleton ────────────────────────────────────────────────

    [Fact]
    public void Empty_HasNoValues()
    {
        Assert.True(ParameterAssignment.Empty.IsEmpty);
        Assert.Empty(ParameterAssignment.Empty.Values);
    }

    // ── With ─────────────────────────────────────────────────────────────

    [Fact]
    public void With_AddsValue()
    {
        var a = ParameterAssignment.Empty.With("x", 2);
        Assert.False(a.IsEmpty);
        Assert.Equal(2, a.Get<int>("x"));
    }

    [Fact]
    public void With_OverwritesExistingKey()
    {
        var a = ParameterAssignment.Empty.With("x", 1).With("x", 99);
        Assert.Equal(99, a.Get<int>("x"));
    }

    [Fact]
    public void With_IsImmutable()
    {
        var original = ParameterAssignment.Empty;
        var updated = original.With("x", 7);
        Assert.True(original.IsEmpty);
        Assert.False(updated.IsEmpty);
    }

    [Fact]
    public void With_NullNameThrows()
        => Assert.Throws<ArgumentException>(() => ParameterAssignment.Empty.With("", "v"));

    [Fact]
    public void With_NullValueThrows()
        => Assert.Throws<ArgumentNullException>(() => ParameterAssignment.Empty.With("x", null!));

    // ── Get<T> ───────────────────────────────────────────────────────────

    [Fact]
    public void Get_ReturnsTypedValue()
    {
        var a = ParameterAssignment.Empty.With("k", "hello");
        Assert.Equal("hello", a.Get<string>("k"));
    }

    [Fact]
    public void Get_ThrowsKeyNotFoundWhenAbsent()
        => Assert.Throws<KeyNotFoundException>(() => ParameterAssignment.Empty.Get<int>("missing"));

    [Fact]
    public void Get_ThrowsInvalidCastOnWrongType()
    {
        var a = ParameterAssignment.Empty.With("x", "a string");
        Assert.Throws<InvalidCastException>(() => a.Get<int>("x"));
    }

    // ── TryGet ───────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ReturnsTrueAndValueWhenPresent()
    {
        var a = ParameterAssignment.Empty.With("n", 42);
        Assert.True(a.TryGet<int>("n", out var v));
        Assert.Equal(42, v);
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenAbsent()
    {
        Assert.False(ParameterAssignment.Empty.TryGet<int>("missing", out _));
    }

    [Fact]
    public void TryGet_ReturnsFalseOnWrongType()
    {
        var a = ParameterAssignment.Empty.With("x", "text");
        Assert.False(a.TryGet<int>("x", out _));
    }

    [Fact]
    public void TryGet_ReturnsFalseOnNullOrEmptyName()
    {
        var a = ParameterAssignment.Empty.With("x", 1);
        Assert.False(a.TryGet<int>("", out _));
        Assert.False(a.TryGet<int>(null!, out _));
    }

    // ── IsEmpty ──────────────────────────────────────────────────────────

    [Fact]
    public void IsEmpty_FalseAfterWith()
    {
        var a = ParameterAssignment.Empty.With("x", 1);
        Assert.False(a.IsEmpty);
    }

    // ── FromCategorical / ToCategoricalDictionary ────────────────────────

    [Fact]
    public void FromCategorical_CreatesIntValues()
    {
        var config = new Dictionary<string, int> { ["a"] = 0, ["b"] = 3 };
        var a = ParameterAssignment.FromCategorical(config);
        Assert.Equal(0, a.Get<int>("a"));
        Assert.Equal(3, a.Get<int>("b"));
    }

    [Fact]
    public void ToCategoricalDictionary_RoundTrips()
    {
        var original = new Dictionary<string, int> { ["x"] = 1, ["y"] = 4 };
        var a = ParameterAssignment.FromCategorical(original);
        Assert.Equal(original, a.ToCategoricalDictionary());
    }

    [Fact]
    public void ToCategoricalDictionary_DropsNonInt()
    {
        var a = ParameterAssignment.Empty
            .With("idx", 2)
            .With("instr", "be helpful");
        var dict = a.ToCategoricalDictionary();
        Assert.Single(dict);
        Assert.True(dict.ContainsKey("idx"));
        Assert.False(dict.ContainsKey("instr"));
    }

    [Fact]
    public void FromCategorical_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => ParameterAssignment.FromCategorical(null!));

    // ── Multiple parameters ───────────────────────────────────────────────

    [Fact]
    public void With_MultipleParameters_AllAccessible()
    {
        var a = ParameterAssignment.Empty
            .With("score", 0.95)
            .With("label", "positive")
            .With("count", 7);
        Assert.Equal(0.95, a.Get<double>("score"));
        Assert.Equal("positive", a.Get<string>("label"));
        Assert.Equal(7, a.Get<int>("count"));
    }
}
