using System;
namespace OneYaml.Parsing;

/// <summary>The kind of event the reader is positioned on.</summary>
internal enum YamlTokenType : byte
{
    None = 0,
    StreamStart,
    StreamEnd,
    DocumentStart,
    DocumentEnd,
    MappingStart,
    MappingEnd,
    SequenceStart,
    SequenceEnd,
    Scalar,
    Alias,
}

/// <summary>How a scalar is written in the source.</summary>
internal enum ScalarStyle : byte
{
    Plain = 0,
    SingleQuoted,
    DoubleQuoted,
    Literal,
    Folded,
}

/// <summary>Whether a collection is written in block or flow style.</summary>
internal enum CollectionStyle : byte
{
    Block = 0,
    Flow,
}

/// <summary>How a block scalar treats the line breaks at its end.</summary>
internal enum Chomping : byte
{
    Clip = 0,
    Strip,
    Keep,
}

/// <summary>Options for <see cref="YamlReader"/>.</summary>
internal readonly record struct YamlReaderOptions
{
    /// <summary>The limits to enforce. <see langword="null"/> means <see cref="YamlLimits.Default"/>.</summary>
    public YamlLimits? Limits { get; init; }

    /// <summary>Record the span of every comment, for readers that need to keep them.</summary>
    public bool RecordComments { get; init; }
}

internal enum TokenKind : byte
{
    StreamStart,
    StreamEnd,
    VersionDirective,
    TagDirective,
    ReservedDirective,
    DocumentStart,
    DocumentEnd,
    BlockSequenceStart,
    BlockMappingStart,
    BlockEnd,
    FlowSequenceStart,
    FlowSequenceEnd,
    FlowMappingStart,
    FlowMappingEnd,
    BlockEntry,
    FlowEntry,
    Key,
    Value,
    Alias,
    Anchor,
    Tag,
    Scalar,

    // A scanner error, kept in the queue so events before it are still produced.
    Error,
}

// One scanner token. All positions are byte offsets into the source, so a token is plain data.
internal struct Token
{
    public int Start;
    public int End;

    // Content span: scalar text without quotes, anchor or alias name, tag suffix, directive handle.
    public int ContentStart;
    public int ContentEnd;

    // Kind specific: tag handle length, block scalar indent, version numbers, directive prefix span.
    public int X;
    public int Y;
    public TokenKind Kind;
    public byte Style;
    public byte Flags;

    // Tokens the scanner inserts before this one once it finds an implicit key: 1 = block mapping start, 2 = key.
    public byte Pre;
}
