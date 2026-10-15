using System;

namespace OneYaml.Compat.YamlDotNet.Core.Events;

/// <summary>How a mapping is written.</summary>
public enum MappingStyle
{
    /// <summary>No preference.</summary>
    Any,

    /// <summary>Indentation, one entry per line.</summary>
    Block,

    /// <summary>Braces, <c>{ a: 1 }</c>.</summary>
    Flow,
}

/// <summary>How a sequence is written.</summary>
public enum SequenceStyle
{
    /// <summary>No preference.</summary>
    Any,

    /// <summary>Indentation, one item per line.</summary>
    Block,

    /// <summary>Brackets, <c>[1, 2]</c>.</summary>
    Flow,
}

/// <summary>An event in the stream a parser produces or an emitter consumes.</summary>
public abstract class ParsingEvent
{
    private protected ParsingEvent(Mark start, Mark end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Where the event starts in the source.</summary>
    public Mark Start { get; }

    /// <summary>Where the event ends in the source.</summary>
    public Mark End { get; }

    /// <summary>How much the event changes the nesting depth: 1 for a start, -1 for an end, otherwise 0.</summary>
    public virtual int NestingIncrease => 0;
}

/// <summary>An event for a node that can carry an anchor and a tag.</summary>
public abstract class NodeEvent : ParsingEvent
{
    private protected NodeEvent(AnchorName anchor, TagName tag, Mark start, Mark end)
        : base(start, end)
    {
        Anchor = anchor;
        Tag = tag;
    }

    /// <summary>The anchor, or the empty name.</summary>
    public AnchorName Anchor { get; }

    /// <summary>The tag, or the empty tag.</summary>
    public TagName Tag { get; }

    /// <summary>Whether the node is written in canonical form.</summary>
    public abstract bool IsCanonical { get; }
}

/// <summary>The start of the stream.</summary>
public sealed class StreamStart : ParsingEvent
{
    /// <summary>Creates the event.</summary>
    public StreamStart()
        : base(Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public StreamStart(in Mark start, in Mark end)
        : base(start, end)
    {
    }

    /// <inheritdoc/>
    public override int NestingIncrease => 1;
}

/// <summary>The end of the stream.</summary>
public sealed class StreamEnd : ParsingEvent
{
    /// <summary>Creates the event.</summary>
    public StreamEnd()
        : base(Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public StreamEnd(in Mark start, in Mark end)
        : base(start, end)
    {
    }

    /// <inheritdoc/>
    public override int NestingIncrease => -1;
}

/// <summary>The start of a document.</summary>
public sealed class DocumentStart : ParsingEvent
{
    /// <summary>Creates an implicit document start.</summary>
    public DocumentStart()
        : this(true, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event.</summary>
    /// <param name="isImplicit">Whether the document has no <c>---</c> marker.</param>
    public DocumentStart(bool isImplicit)
        : this(isImplicit, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public DocumentStart(in Mark start, in Mark end)
        : this(true, start, end)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="isImplicit">Whether the document has no <c>---</c> marker.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public DocumentStart(bool isImplicit, in Mark start, in Mark end)
        : base(start, end)
    {
        IsImplicit = isImplicit;
    }

    /// <summary>Whether the document has no <c>---</c> marker.</summary>
    public bool IsImplicit { get; }

    /// <inheritdoc/>
    public override int NestingIncrease => 1;
}

/// <summary>The end of a document.</summary>
public sealed class DocumentEnd : ParsingEvent
{
    /// <summary>Creates an implicit document end.</summary>
    public DocumentEnd()
        : this(true, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event.</summary>
    /// <param name="isImplicit">Whether the document has no <c>...</c> marker.</param>
    public DocumentEnd(bool isImplicit)
        : this(isImplicit, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="isImplicit">Whether the document has no <c>...</c> marker.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public DocumentEnd(bool isImplicit, in Mark start, in Mark end)
        : base(start, end)
    {
        IsImplicit = isImplicit;
    }

    /// <summary>Whether the document has no <c>...</c> marker.</summary>
    public bool IsImplicit { get; }

    /// <inheritdoc/>
    public override int NestingIncrease => -1;
}

/// <summary>A scalar.</summary>
public sealed class Scalar : NodeEvent
{
    /// <summary>Creates a plain scalar.</summary>
    /// <param name="value">The text.</param>
    public Scalar(string value)
        : this(AnchorName.Empty, TagName.Empty, value, ScalarStyle.Any, true, true)
    {
    }

    /// <summary>Creates a tagged scalar.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The text.</param>
    public Scalar(TagName tag, string value)
        : this(AnchorName.Empty, tag, value, ScalarStyle.Any, true, true)
    {
    }

    /// <summary>Creates a scalar with an anchor and a tag.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The text.</param>
    public Scalar(AnchorName anchor, TagName tag, string value)
        : this(anchor, tag, value, ScalarStyle.Any, true, true)
    {
    }

    /// <summary>Creates a scalar.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The text.</param>
    /// <param name="style">The style.</param>
    /// <param name="isPlainImplicit">Whether the tag can be left out when the scalar is plain.</param>
    /// <param name="isQuotedImplicit">Whether the tag can be left out when the scalar is quoted.</param>
    public Scalar(AnchorName anchor, TagName tag, string value, ScalarStyle style, bool isPlainImplicit, bool isQuotedImplicit)
        : this(anchor, tag, value, style, isPlainImplicit, isQuotedImplicit, Mark.Empty, Mark.Empty, false)
    {
    }

    /// <summary>Creates a scalar with positions.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The text.</param>
    /// <param name="style">The style.</param>
    /// <param name="isPlainImplicit">Whether the tag can be left out when the scalar is plain.</param>
    /// <param name="isQuotedImplicit">Whether the tag can be left out when the scalar is quoted.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="isKey">Whether the scalar is a mapping key.</param>
    public Scalar(AnchorName anchor, TagName tag, string value, ScalarStyle style, bool isPlainImplicit, bool isQuotedImplicit, in Mark start, in Mark end, bool isKey)
        : base(anchor, tag, start, end)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Style = style;
        IsPlainImplicit = isPlainImplicit;
        IsQuotedImplicit = isQuotedImplicit;
        IsKey = isKey;
    }

    /// <summary>The text.</summary>
    public string Value { get; }

    /// <summary>The style.</summary>
    public ScalarStyle Style { get; }

    /// <summary>Whether the tag can be left out when the scalar is plain.</summary>
    public bool IsPlainImplicit { get; }

    /// <summary>Whether the tag can be left out when the scalar is quoted.</summary>
    public bool IsQuotedImplicit { get; }

    /// <summary>Whether the scalar is a mapping key.</summary>
    public bool IsKey { get; }

    /// <inheritdoc/>
    public override bool IsCanonical => !IsPlainImplicit && !IsQuotedImplicit;
}

/// <summary>The start of a sequence.</summary>
public sealed class SequenceStart : NodeEvent
{
    /// <summary>Creates the event.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="isImplicit">Whether the tag can be left out.</param>
    /// <param name="style">The style.</param>
    public SequenceStart(AnchorName anchor, TagName tag, bool isImplicit, SequenceStyle style)
        : this(anchor, tag, isImplicit, style, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="isImplicit">Whether the tag can be left out.</param>
    /// <param name="style">The style.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public SequenceStart(AnchorName anchor, TagName tag, bool isImplicit, SequenceStyle style, in Mark start, in Mark end)
        : base(anchor, tag, start, end)
    {
        IsImplicit = isImplicit;
        Style = style;
    }

    /// <summary>Whether the tag can be left out.</summary>
    public bool IsImplicit { get; }

    /// <summary>The style.</summary>
    public SequenceStyle Style { get; }

    /// <inheritdoc/>
    public override bool IsCanonical => false;

    /// <inheritdoc/>
    public override int NestingIncrease => 1;
}

/// <summary>The end of a sequence.</summary>
public sealed class SequenceEnd : ParsingEvent
{
    /// <summary>Creates the event.</summary>
    public SequenceEnd()
        : base(Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public SequenceEnd(in Mark start, in Mark end)
        : base(start, end)
    {
    }

    /// <inheritdoc/>
    public override int NestingIncrease => -1;
}

/// <summary>The start of a mapping.</summary>
public sealed class MappingStart : NodeEvent
{
    /// <summary>Creates an event with no anchor or tag.</summary>
    public MappingStart()
        : this(AnchorName.Empty, TagName.Empty, true, MappingStyle.Any)
    {
    }

    /// <summary>Creates the event.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="isImplicit">Whether the tag can be left out.</param>
    /// <param name="style">The style.</param>
    public MappingStart(AnchorName anchor, TagName tag, bool isImplicit, MappingStyle style)
        : this(anchor, tag, isImplicit, style, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="isImplicit">Whether the tag can be left out.</param>
    /// <param name="style">The style.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public MappingStart(AnchorName anchor, TagName tag, bool isImplicit, MappingStyle style, in Mark start, in Mark end)
        : base(anchor, tag, start, end)
    {
        IsImplicit = isImplicit;
        Style = style;
    }

    /// <summary>Whether the tag can be left out.</summary>
    public bool IsImplicit { get; }

    /// <summary>The style.</summary>
    public MappingStyle Style { get; }

    /// <inheritdoc/>
    public override bool IsCanonical => false;

    /// <inheritdoc/>
    public override int NestingIncrease => 1;
}

/// <summary>The end of a mapping.</summary>
public sealed class MappingEnd : ParsingEvent
{
    /// <summary>Creates the event.</summary>
    public MappingEnd()
        : base(Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public MappingEnd(in Mark start, in Mark end)
        : base(start, end)
    {
    }

    /// <inheritdoc/>
    public override int NestingIncrease => -1;
}

/// <summary>An alias (<c>*name</c>) that refers to an anchored node.</summary>
public sealed class AnchorAlias : ParsingEvent
{
    /// <summary>Creates the event.</summary>
    /// <param name="value">The name of the anchor.</param>
    public AnchorAlias(AnchorName value)
        : this(value, Mark.Empty, Mark.Empty)
    {
    }

    /// <summary>Creates the event with positions.</summary>
    /// <param name="value">The name of the anchor.</param>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    public AnchorAlias(AnchorName value, in Mark start, in Mark end)
        : base(start, end)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentNullException(nameof(value));
        }

        Value = value;
    }

    /// <summary>The name of the anchor.</summary>
    public AnchorName Value { get; }
}
