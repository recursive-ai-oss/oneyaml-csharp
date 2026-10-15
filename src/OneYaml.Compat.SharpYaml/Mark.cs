using System;
using System.Globalization;

namespace OneYaml.Compat.SharpYaml;

/// <summary>A place in the source text: the character index, the line and the column. Lines and columns count from zero.</summary>
public readonly struct Mark : IEquatable<Mark>
{
    /// <summary>The mark that stands for an unknown place.</summary>
    public static readonly Mark Empty = new(0, 0, 0);

    /// <summary>Creates a mark.</summary>
    /// <param name="index">The index of the character in the text.</param>
    /// <param name="line">The line, counted from zero.</param>
    /// <param name="column">The column, counted from zero.</param>
    public Mark(int index, int line, int column)
    {
        Index = index;
        Line = line;
        Column = column;
    }

    /// <summary>The index of the character in the text.</summary>
    public int Index { get; }

    /// <summary>The line, counted from zero.</summary>
    public int Line { get; }

    /// <summary>The column, counted from zero.</summary>
    public int Column { get; }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Lin: {Line}, Col: {Column}, Chr: {Index}");

    /// <inheritdoc/>
    public bool Equals(Mark other) => Index == other.Index && Line == other.Line && Column == other.Column;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Mark other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Line, Column);

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
}
