using System;
namespace OneYaml.Emitting;

/// <summary>How a scalar is written.</summary>
internal enum ScalarWriteStyle : byte
{
    /// <summary>No quotes: <c>text</c>.</summary>
    Plain,

    /// <summary>Single quotes: <c>'text'</c>; only the quote itself is escaped, by doubling.</summary>
    SingleQuoted,

    /// <summary>Double quotes with escapes: <c>"text\n"</c>; holds any text.</summary>
    DoubleQuoted,

    /// <summary>A literal block, <c>|</c>: line breaks are kept as written.</summary>
    Literal,

    /// <summary>A folded block, <c>&gt;</c>: written so the line structure reads back exactly.</summary>
    Folded,
}

/// <summary>Where a scalar sits, which decides what a plain scalar may contain.</summary>
internal enum EmitContext : byte
{
    /// <summary>The value of a block mapping entry, a block sequence item or a document.</summary>
    BlockValue,

    /// <summary>An implicit key of a block mapping.</summary>
    BlockKey,

    /// <summary>An item or value inside <c>[ ]</c> or <c>{ }</c>.</summary>
    FlowValue,

    /// <summary>A key inside <c>{ }</c>.</summary>
    FlowKey,
}

/// <summary>Whose reading a plain scalar must survive.</summary>
internal enum StyleMode : byte
{
    /// <summary>
    /// Writing new output: plain only when the Core schema and the common YAML 1.1 readings all
    /// take the text as a string.
    /// </summary>
    Dump,

    /// <summary>
    /// Changing a value inside an existing document: plainness is judged by the Core schema alone,
    /// so <c>yes</c> stays plain.
    /// </summary>
    Edit,
}
