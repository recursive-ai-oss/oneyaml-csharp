using System;
using System.Globalization;

namespace OneYaml.Compat.YamlDotNet.Core;

/// <summary>A place in the source text: a character index, a line and a column, all counted from 1 except the index.</summary>
public readonly struct Mark : IEquatable<Mark>, IComparable<Mark>, IComparable
{
    /// <summary>The mark at the start of the input, with no position information.</summary>
    public static readonly Mark Empty = new(0, 1, 1);

    /// <summary>Creates a mark.</summary>
    /// <param name="index">The character index, from 0.</param>
    /// <param name="line">The line, from 1.</param>
    /// <param name="column">The column, from 1.</param>
    public Mark(long index, long line, long column)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Index must be greater than or equal to zero.");
        }

        if (line < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Line must be greater than or equal to 1.");
        }

        if (column < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Column must be greater than or equal to 1.");
        }

        Index = index;
        Line = line;
        Column = column;
    }

    /// <summary>The character index, from 0.</summary>
    public long Index { get; }

    /// <summary>The line, from 1.</summary>
    public long Line { get; }

    /// <summary>The column, from 1.</summary>
    public long Column { get; }

    /// <inheritdoc/>
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "Line: {0}, Col: {1}, Idx: {2}", Line, Column, Index);

    /// <inheritdoc/>
    public bool Equals(Mark other) => Index == other.Index && Line == other.Line && Column == other.Column;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Mark other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Line, Column);

    /// <inheritdoc/>
    public int CompareTo(Mark other)
    {
        int result = Line.CompareTo(other.Line);
        return result != 0 ? result : Column.CompareTo(other.Column);
    }

    /// <inheritdoc/>
    public int CompareTo(object? obj) => obj is null ? 1 : obj is Mark other ? CompareTo(other) : throw new ArgumentException("Object is not a Mark.", nameof(obj));

    /// <summary>Tells whether two marks are the same place.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Mark left, Mark right) => left.Equals(right);

    /// <summary>Tells whether two marks differ.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(Mark left, Mark right) => !left.Equals(right);

    /// <summary>Tells whether a mark comes before another.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is earlier.</returns>
    public static bool operator <(Mark left, Mark right) => left.CompareTo(right) < 0;

    /// <summary>Tells whether a mark comes after another.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is later.</returns>
    public static bool operator >(Mark left, Mark right) => left.CompareTo(right) > 0;

    /// <summary>Tells whether a mark comes before or is another.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is not later.</returns>
    public static bool operator <=(Mark left, Mark right) => left.CompareTo(right) <= 0;

    /// <summary>Tells whether a mark comes after or is another.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is not earlier.</returns>
    public static bool operator >=(Mark left, Mark right) => left.CompareTo(right) >= 0;
}
