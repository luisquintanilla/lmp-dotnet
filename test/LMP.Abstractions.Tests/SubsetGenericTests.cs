namespace LMP.Abstractions.Tests;

public class SubsetGenericTests
{
    [Fact]
    public void Subset_T_IsAssignableTo_Subset()
    {
        Subset<string> s = new(["a", "b"]);
        Assert.IsAssignableFrom<Subset>(s);
    }

    [Fact]
    public void Subset_T_BasePool_MirrorsTypedPool()
    {
        var s = new Subset<int>([1, 2, 3]);

        Assert.Equal(3, s.TypedPool.Count);
        Assert.Equal(3, s.Pool.Count);
        Assert.Equal((object)1, s.Pool[0]);
        Assert.Equal((object)2, s.Pool[1]);
        Assert.Equal((object)3, s.Pool[2]);
    }

    [Fact]
    public void Subset_T_PatternMatch_AsSubset_Succeeds()
    {
        ParameterKind kind = new Subset<string>(["x", "y", "z"]);

        Assert.True(kind is Subset);
        var s = (Subset)kind;
        Assert.Equal(3, s.Pool.Count);
    }

    [Fact]
    public void Subset_T_PatternMatch_AsGenericSubset_Succeeds()
    {
        ParameterKind kind = new Subset<string>(["x", "y"]);

        Assert.True(kind is Subset<string>);
        var sg = (Subset<string>)kind;
        Assert.Equal(2, sg.TypedPool.Count);
        Assert.Equal("x", sg.TypedPool[0]);
    }

    [Fact]
    public void Subset_T_Equality_IsStructural()
    {
        // Record equality uses EqualityContract (runtime type). A typed Subset<T>
        // is never equal to the non-generic base Subset, even when the base
        // Pool contents are identical after boxing.
        IReadOnlyList<object> boxedPool = [1, 2, 3];
        var typed = new Subset<int>([1, 2, 3], 0, 3);
        var untyped = new Subset(boxedPool, 0, 3);
        Assert.NotEqual<Subset>(typed, untyped);

        // Two Subset<T>s with different element types are also distinct records,
        // even at the Subset base reference.
        var asInts = new Subset<int>([1, 2, 3], 0, 3);
        var asObjs = new Subset<object>([1, 2, 3], 0, 3);
        Assert.NotEqual<Subset>(asInts, asObjs);

        // Reflexivity: an instance is equal to itself.
        Assert.Equal(typed, typed);
    }

    [Fact]
    public void Subset_NonGeneric_StillConstructable()
    {
        var obj1 = new object();
        var obj2 = "hello";
        var s = new Subset([obj1, obj2], 0, 2);

        Assert.Equal(2, s.Pool.Count);
        Assert.Same(obj1, s.Pool[0]);
        Assert.Equal(0, s.MinSize);
        Assert.Equal(2, s.MaxSize);
    }
}
