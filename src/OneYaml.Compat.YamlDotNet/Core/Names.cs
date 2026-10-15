using System;

namespace OneYaml.Compat.YamlDotNet.Core;

/// <summary>How a scalar is written in the text.</summary>
public enum ScalarStyle
{
    /// <summary>No preference: the emitter chooses.</summary>
    Any,

    /// <summary>No quotes.</summary>
    Plain,

    /// <summary>Single quotes.</summary>
    SingleQuoted,

    /// <summary>Double quotes.</summary>
    DoubleQuoted,

    /// <summary>A literal block, <c>|</c>.</summary>
    Literal,

    /// <summary>A folded block, <c>&gt;</c>.</summary>
    Folded,
}

/// <summary>The name of an anchor, without the ampersand. The default value is the empty name.</summary>
public readonly struct AnchorName : IEquatable<AnchorName>
{
    /// <summary>The empty anchor name: no anchor.</summary>
    public static AnchorName Empty => default;

    private readonly string? _value;

    /// <summary>Creates an anchor name.</summary>
    /// <param name="value">The name; <see langword="null"/> or empty for none.</param>
    public AnchorName(string? value)
        : this(value, validate: true)
    {
    }

    // Names read from a document may hold characters the constructor rejects; they are kept as written.
    private AnchorName(string? value, bool validate)
    {
        if (validate && value is not null)
        {
            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c is '_' or '-'))
                {
                    throw new ArgumentException($"Anchor name '{value}' contains characters that are not allowed.", nameof(value));
                }
            }
        }

        _value = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>The name, or <see langword="null"/> for the empty name.</summary>
    public string? Value => _value;

    /// <summary>Whether this is the empty name.</summary>
    public bool IsEmpty => _value is null;

    /// <inheritdoc/>
    public override string ToString() => _value ?? "[empty]";

    /// <inheritdoc/>
    public bool Equals(AnchorName other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AnchorName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>Tells whether two names are equal.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(AnchorName left, AnchorName right) => left.Equals(right);

    /// <summary>Tells whether two names differ.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(AnchorName left, AnchorName right) => !left.Equals(right);

    /// <summary>Makes a name from a string.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The anchor name.</returns>
    public static implicit operator AnchorName(string? value) => new(value);

    /// <summary>Makes an anchor name from a string.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The anchor name.</returns>
    public static AnchorName FromString(string? value) => new(value);

    internal static AnchorName AsWritten(string? value) => new(value, validate: false);
}

/// <summary>A tag such as <c>!!str</c> (stored in full as <c>tag:yaml.org,2002:str</c>) or <c>!local</c>. The default value is the empty tag.</summary>
public readonly struct TagName : IEquatable<TagName>
{
    /// <summary>The empty tag: no tag.</summary>
    public static TagName Empty => default;

    private readonly string? _value;

    /// <summary>Creates a tag.</summary>
    /// <param name="value">The tag in full, a local tag starting with <c>!</c>, or <see langword="null"/>/empty for none.</param>
    public TagName(string? value)
    {
        if (value is not null && value.Length == 0)
        {
            throw new ArgumentException("Tag value must not be empty.", nameof(value));
        }

        _value = value;
    }

    /// <summary>The tag text, or <see langword="null"/> for the empty tag.</summary>
    public string? Value => _value;

    /// <summary>Whether this is the empty tag.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>Whether this is the non-specific tag <c>!</c>.</summary>
    public bool IsNonSpecific => _value == "!";

    /// <summary>Whether this is a local tag.</summary>
    public bool IsLocal => _value is not null && _value.Length > 0 && _value[0] == '!';

    /// <summary>Whether this is a global tag.</summary>
    public bool IsGlobal => _value is not null && !IsLocal;

    /// <inheritdoc/>
    public override string ToString() => _value ?? "?";

    /// <inheritdoc/>
    public bool Equals(TagName other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TagName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>Tells whether two tags are equal.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(TagName left, TagName right) => left.Equals(right);

    /// <summary>Tells whether two tags differ.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(TagName left, TagName right) => !left.Equals(right);

    /// <summary>Makes a tag from a string.</summary>
    /// <param name="value">The tag.</param>
    /// <returns>The tag name.</returns>
    public static implicit operator TagName(string? value) => new(value);

    /// <summary>Makes a tag from a string.</summary>
    /// <param name="value">The tag.</param>
    /// <returns>The tag name.</returns>
    public static TagName FromString(string? value) => new(value);
}
