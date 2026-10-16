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

    /// <summary>
    /// Not a style to write with: the key is longer than 1,024 UTF-16 code units in every style that
    /// could hold it, so it cannot be an implicit key. Nothing was written; the caller writes
    /// <c>? </c> and the key in double quotes (or a block scalar) and puts <c>:</c> on the next line.
    /// </summary>
    ExplicitKeyRequired,
}

/// <summary>How the trailing line breaks of a block scalar are kept.</summary>
internal enum BlockChomping : byte
{
    /// <summary>Clip (no indicator): one final line break is kept.</summary>
    Clip,

    /// <summary>Strip (<c>-</c>): no final line break.</summary>
    Strip,

    /// <summary>Keep (<c>+</c>): every trailing line break is kept.</summary>
    Keep,
}

/// <summary>
/// The header of a block scalar, <c>|2+</c> without the style character: used both as the header
/// a scalar has now (a preference for the rewrite) and as the header the writer picks.
/// <c>default</c> means no preference.
/// </summary>
/// <param name="Chomping">The chomping.</param>
/// <param name="IndentIndicator">The indentation indicator, 1 to 9, or 0 for none.</param>
internal readonly record struct BlockHeader(BlockChomping Chomping, int IndentIndicator);

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

    /// <summary>
    /// Changing a value inside an existing document, and the caller asked that new plain text survive a YAML 1.1
    /// reader too: <c>yes</c> and <c>2026-01-01</c> are quoted, as in <see cref="Dump"/>.
    /// </summary>
    EditQuoting,
}
