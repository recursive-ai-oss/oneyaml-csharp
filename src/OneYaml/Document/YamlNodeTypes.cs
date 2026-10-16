using System;

namespace OneYaml;

/// <summary>The structural kind of a <see cref="YamlNode"/>.</summary>
public enum YamlNodeKind
{
    /// <summary>A scalar: text that may resolve to a string, number, boolean or null.</summary>
    Scalar = 0,

    /// <summary>A mapping of keys to values.</summary>
    Mapping,

    /// <summary>A sequence of items.</summary>
    Sequence,

    /// <summary>An alias (<c>*name</c>) that refers to an anchored node. Reads follow it.</summary>
    Alias,
}

/// <summary>How a node is written in the source.</summary>
public enum YamlNodeStyle
{
    /// <summary>A plain scalar.</summary>
    Plain = 0,

    /// <summary>A single-quoted scalar.</summary>
    SingleQuoted,

    /// <summary>A double-quoted scalar.</summary>
    DoubleQuoted,

    /// <summary>A literal block scalar (<c>|</c>).</summary>
    Literal,

    /// <summary>A folded block scalar (<c>&gt;</c>).</summary>
    Folded,

    /// <summary>A block mapping or sequence, and the style of an alias.</summary>
    Block,

    /// <summary>A flow mapping or sequence (<c>{ }</c>, <c>[ ]</c>).</summary>
    Flow,
}

/// <summary>A place in the source text.</summary>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column, counted in characters.</param>
/// <param name="Offset">The byte offset in the UTF-8 text, not counting a byte order mark.</param>
public readonly record struct YamlPosition(int Line, int Column, long Offset);

/// <summary>Options for <see cref="YamlDocument"/>.</summary>
public sealed record YamlDocumentOptions
{
    /// <summary>The limits to enforce when parsing and when following aliases. <see langword="null"/> means <see cref="YamlLimits.Default"/>.</summary>
    public YamlLimits? Limits { get; init; }

    /// <summary>
    /// Accept mappings that hold the same key twice, as lossless editing of such a file needs. Keys are
    /// compared after Core schema resolution, so <c>1</c> and <c>0x1</c> are the same key but <c>1</c> and
    /// <c>"1"</c> are not. By default a repeated key is a <see cref="YamlErrorCategory.DuplicateKey"/> error.
    /// When allowed, a pointer or indexer that matches more than one key is refused as ambiguous.
    /// </summary>
    public bool AllowDuplicateKeys { get; init; }

    /// <summary>
    /// Quote a new plain string that a YAML 1.1 reader would take for something else (<c>yes</c>, <c>off</c>,
    /// <c>2026-01-01</c>, <c>0x10</c>), the way <see cref="YamlSerializer"/> does. By default an edit keeps such text
    /// plain, which is correct under the Core schema and changes the fewest bytes.
    /// </summary>
    /// <remarks>
    /// Judging plainness by the Core schema (the YAML 1.2 default, <c>yes</c> and <c>off</c> are strings) is deliberate, and so is
    /// leaving the option off: the editor changes only what the edit needs and does not rewrite text for readers of an older
    /// YAML. <see cref="YamlSerializer"/> quotes such text because it writes whole documents for any reader. Turn this on when
    /// the edited file is also read by a YAML 1.1 reader.
    /// </remarks>
    public bool QuoteYaml11Lookalikes { get; init; }

    /// <summary>
    /// A name for the input, such as a file name. It prefixes the message of every <see cref="YamlException"/> the document raises
    /// (parsing, reading, editing, binding a node) and is kept in <see cref="YamlException.SourceName"/>.
    /// </summary>
    public string? SourceName { get; init; }

    /// <summary>The options used when none are given.</summary>
    public static YamlDocumentOptions Default { get; } = new();
}
