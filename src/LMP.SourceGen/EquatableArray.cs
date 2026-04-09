using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace LMP.SourceGen;

/// <summary>
/// Wraps an <see cref="ImmutableArray{T}"/> with element-wise equality semantics.
/// Required for incremental source generator model caching — Roslyn uses equality
/// checks to decide whether to re-run downstream pipeline stages.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> uses reference equality by default, which defeats
/// the incremental cache. This wrapper provides structural (element-wise) equality.
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array) => _array = array;

    public ImmutableArray<T> AsImmutableArray() => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    public int Count => AsImmutableArray().Length;

    public T this[int index] => AsImmutableArray()[index];

    public bool Equals(EquatableArray<T> other)
        => AsImmutableArray().SequenceEqual(other.AsImmutableArray());

    public override bool Equals(object? obj)
        => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var arr = AsImmutableArray();
        unchecked
        {
            int hash = 17;
            foreach (var item in arr)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
        => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
        => !left.Equals(right);

    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
        => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => ((IEnumerable)AsImmutableArray()).GetEnumerator();
}
