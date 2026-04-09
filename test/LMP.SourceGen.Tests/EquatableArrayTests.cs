using System.Collections.Immutable;
using Xunit;

namespace LMP.SourceGen.Tests;

public class EquatableArrayTests
{
    [Fact]
    public void Default_HasZeroCount()
    {
        var arr = default(EquatableArray<string>);
        Assert.Equal(0, arr.Count);
    }

    [Fact]
    public void Empty_HasZeroCount()
    {
        var arr = new EquatableArray<string>(ImmutableArray<string>.Empty);
        Assert.Equal(0, arr.Count);
    }

    [Fact]
    public void Indexer_ReturnsCorrectElement()
    {
        var arr = new EquatableArray<string>(ImmutableArray.Create("a", "b", "c"));

        Assert.Equal("a", arr[0]);
        Assert.Equal("b", arr[1]);
        Assert.Equal("c", arr[2]);
    }

    [Fact]
    public void Equals_SameElements_ReturnsTrue()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));
        var b = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Equals_DifferentElements_ReturnsFalse()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));
        var b = new EquatableArray<int>(ImmutableArray.Create(1, 2, 4));

        Assert.False(a.Equals(b));
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_DifferentLengths_ReturnsFalse()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2));
        var b = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_BothDefault_ReturnsTrue()
    {
        var a = default(EquatableArray<int>);
        var b = default(EquatableArray<int>);

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_DefaultAndEmpty_ReturnsTrue()
    {
        var a = default(EquatableArray<int>);
        var b = new EquatableArray<int>(ImmutableArray<int>.Empty);

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void GetHashCode_SameElements_ReturnsSameHash()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));
        var b = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentElements_ReturnsDifferentHash()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2, 3));
        var b = new EquatableArray<int>(ImmutableArray.Create(4, 5, 6));

        // Not guaranteed, but highly likely for these values
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Enumerable_YieldsAllElements()
    {
        var arr = new EquatableArray<string>(ImmutableArray.Create("x", "y"));
        var list = new List<string>();

        foreach (var item in arr)
            list.Add(item);

        Assert.Equal(["x", "y"], list);
    }

    [Fact]
    public void Enumerable_DefaultArray_YieldsNothing()
    {
        var arr = default(EquatableArray<string>);
        var list = new List<string>();

        foreach (var item in arr)
            list.Add(item);

        Assert.Empty(list);
    }

    [Fact]
    public void AsImmutableArray_ReturnsOriginal()
    {
        var original = ImmutableArray.Create(1, 2, 3);
        var arr = new EquatableArray<int>(original);

        Assert.Equal(original, arr.AsImmutableArray());
    }

    [Fact]
    public void AsImmutableArray_Default_ReturnsEmpty()
    {
        var arr = default(EquatableArray<int>);

        Assert.True(arr.AsImmutableArray().IsEmpty);
    }

    [Fact]
    public void ObjectEquals_BoxedEquatableArray_Works()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2));
        object b = new EquatableArray<int>(ImmutableArray.Create(1, 2));

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void ObjectEquals_NonEquatableArray_ReturnsFalse()
    {
        var a = new EquatableArray<int>(ImmutableArray.Create(1, 2));

        Assert.False(a.Equals("not an array"));
    }
}
