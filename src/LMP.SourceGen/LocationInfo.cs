using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace LMP.SourceGen;

/// <summary>
/// Serializable location wrapper for incremental source generator caching.
/// Roslyn's <see cref="Location"/> does not implement <see cref="IEquatable{T}"/>,
/// so this struct stores the location components with proper equality semantics.
/// </summary>
internal readonly struct LocationInfo : IEquatable<LocationInfo>
{
    public string? FilePath { get; }
    public TextSpan TextSpan { get; }
    public LinePositionSpan LineSpan { get; }

    public LocationInfo(string? filePath, TextSpan textSpan, LinePositionSpan lineSpan)
    {
        FilePath = filePath;
        TextSpan = textSpan;
        LineSpan = lineSpan;
    }

    /// <summary>
    /// Creates a <see cref="LocationInfo"/> from a Roslyn <see cref="Location"/>.
    /// </summary>
    public static LocationInfo From(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new LocationInfo(lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    /// <summary>
    /// Reconstructs a Roslyn <see cref="Location"/> from the stored components.
    /// </summary>
    public Location ToLocation() =>
        FilePath is not null
            ? Location.Create(FilePath, TextSpan, LineSpan)
            : Location.None;

    public bool Equals(LocationInfo other)
        => FilePath == other.FilePath
           && TextSpan.Equals(other.TextSpan)
           && LineSpan.Equals(other.LineSpan);

    public override bool Equals(object? obj) => obj is LocationInfo other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (FilePath?.GetHashCode() ?? 0);
            hash = hash * 31 + TextSpan.GetHashCode();
            hash = hash * 31 + LineSpan.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(LocationInfo left, LocationInfo right) => left.Equals(right);
    public static bool operator !=(LocationInfo left, LocationInfo right) => !left.Equals(right);
}
