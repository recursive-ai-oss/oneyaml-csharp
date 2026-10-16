using System;
using OneYaml.Emitting;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// The edit engine. Every edit, whether it comes from an indexer or a pointer, is one of four
/// operations that read a state and return the next one: replace a node, add a mapping member,
/// insert a sequence item, remove an entry. None of them changes a state it is given unless a patch has
/// marked that state exclusive (it made the state itself and nobody else can see it). A failure therefore
/// leaves the document as it was, and a series of them can be chained and committed at once.
/// </summary>
internal static partial class Editor
{
    private static YamlException Fail(YamlErrorCategory category, string message, DocState s, int node)
        => YamlException.At(category, message, s.Text, node >= 0 ? s.Node(node).Start : 0);

    /// <summary>
    /// Refuses an edit that would destroy an anchor that aliases elsewhere still use. Aliases inside
    /// the subtree itself do not count, and neither do aliases of <paramref name="except"/>.
    /// </summary>
    private static void CheckAnchorsFree(DocState s, int first, int size, int except)
    {
        if (s.AliasCount == 0)
        {
            return;
        }

        int end = first + size;
        bool any = false;
        for (int i = first; i < end; i++)
        {
            if (i != except && (s.Node(i).Flags & NodeFlags.HasAliases) != 0)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        for (int a = 0; a < s.NodeCount; a++)
        {
            ref readonly CstNode n = ref s.Node(a);
            if (n.Kind == NodeKind.Alias && n.Aux >= first && n.Aux < end && n.Aux != except && (a < first || a >= end))
            {
                throw Fail(YamlErrorCategory.RefusedAlias, "The edit would remove an anchor that aliases still refer to.", s, n.Aux);
            }
        }
    }

    private static ScalarWriteStyle StyleOf(in CstNode n) => n.Kind != NodeKind.Scalar ? ScalarWriteStyle.Plain : (ScalarStyle)n.Style switch
    {
        ScalarStyle.SingleQuoted => ScalarWriteStyle.SingleQuoted,
        ScalarStyle.DoubleQuoted => ScalarWriteStyle.DoubleQuoted,
        ScalarStyle.Literal => ScalarWriteStyle.Literal,
        ScalarStyle.Folded => ScalarWriteStyle.Folded,
        _ => ScalarWriteStyle.Plain,
    };

    // The scalar style of a sibling that new text should copy: quotes are copied, block styles are not.
    private static ScalarWriteStyle SiblingStyle(DocState s, int sibling)
    {
        if (sibling < 0)
        {
            return ScalarWriteStyle.Plain;
        }

        ScalarWriteStyle st = StyleOf(s.Node(sibling));
        return st is ScalarWriteStyle.SingleQuoted or ScalarWriteStyle.DoubleQuoted ? st : ScalarWriteStyle.Plain;
    }

    private static bool IsNullScalar(DocState s, int node)
        => s.Node(node).Kind == NodeKind.Scalar && s.KindOf(node, out bool valid) == ScalarKind.Null && valid;

    /// <summary>Whether the tag on a node still tells the truth about the new value.</summary>
    private static bool KeepTag(DocState s, int node, ValueTree v)
    {
        ReadOnlySpan<byte> raw = s.RawTag(node);
        if (raw.IsEmpty)
        {
            return false;
        }

        ref readonly VNode nv = ref v[0];
        CoreTag core = s.ExplicitCoreTag(node);
        if (core != CoreTag.Unknown)
        {
            return nv.Kind == VKind.Scalar && core switch
            {
                CoreTag.Str => nv.Scalar == ScalarKind.String,
                CoreTag.Int => nv.Scalar == ScalarKind.Int,
                CoreTag.Float => nv.Scalar == ScalarKind.Float,
                CoreTag.Bool => nv.Scalar == ScalarKind.Bool,
                CoreTag.Null => nv.Scalar == ScalarKind.Null,
                _ => false,
            };
        }

        TagClass kind = s.TagClassOf(node);
        if (kind == TagClass.Map)
        {
            return nv.Kind == VKind.Mapping;
        }

        if (kind == TagClass.Seq)
        {
            return nv.Kind == VKind.Sequence;
        }

        // A tag the schema does not know makes its scalar read as text, so it fits only a string.
        NodeKind old = s.Node(node).Kind;
        return (old == NodeKind.Scalar && nv.Kind == VKind.Scalar && nv.Scalar == ScalarKind.String)
            || (old == NodeKind.Mapping && nv.Kind == VKind.Mapping)
            || (old == NodeKind.Sequence && nv.Kind == VKind.Sequence);
    }

    private static DocState Commit(DocState s, YamlLimits limits, int start, int end, ByteBuffer text)
    {
        Span<TextEdit> one = [new TextEdit(start, end, 0, text.Count)];
        return DocState.Rebuild(s, one, text.WrittenSpan, limits);
    }

    /// <summary>Replaces a node's value, keeping everything around it.</summary>
    /// <param name="s">The current state.</param>
    /// <param name="limits">Limits for the new state.</param>
    /// <param name="node">The node to replace: a mapping value, a sequence item or a root.</param>
    /// <param name="v">The new value.</param>
    /// <returns>The next state, or <paramref name="s"/> itself when nothing needs to change.</returns>
    public static DocState Replace(DocState s, YamlLimits limits, int node, ValueTree v)
    {
        EditTrace? trace = s.Trace;
        if (trace is null)
        {
            return ReplaceCore(s, limits, node, v);
        }

        int oldSize = s.Node(node).Size;
        DocState next = ReplaceCore(s, limits, node, v);
        trace.Add(new NodeEdit(NodeEditKind.Replace, node, oldSize, next.Node(node).Size));
        return next;
    }

    private static DocState ReplaceCore(DocState s, YamlLimits limits, int node, ValueTree v)
    {
        ref readonly CstNode n = ref s.Node(node);
        if ((n.Flags & NodeFlags.IsKey) != 0)
        {
            throw Fail(YamlErrorCategory.PatchPath, "A key cannot be replaced.", s, node);
        }

        CheckAnchorsFree(s, node, n.Size, node);
        bool hasTag = s.HasTag(node);
        bool keepTag = hasTag && KeepTag(s, node, v);
        if (v[0].Kind == VKind.Scalar && v[0].Scalar == ScalarKind.Null && IsNullScalar(s, node) && (!hasTag || keepTag))
        {
            // An existing null keeps its spelling.
            return s;
        }

        ReadOnlySpan<byte> src = s.Text;
        int parent = n.Parent;
        bool inFlow = parent >= 0 && s.Node(parent).IsFlow;
        if ((n.Flags & NodeFlags.Bare) != 0 && !inFlow)
        {
            return ReplaceBareExplicit(s, limits, node, v);
        }

        bool oldIsColl = n.Kind is NodeKind.Mapping or NodeKind.Sequence;
        bool oldBlockColl = oldIsColl && !n.IsFlow;
        bool flowStyle = inFlow || (oldIsColl && n.IsFlow);
        bool newBlockNonEmpty = !v.IsScalar && !flowStyle && v[0].Count > 0;
        bool inline = !oldBlockColl && !newBlockNonEmpty;
        bool oldEndsWithBreak = n.End > n.Start && DocState.IsBreak(src[n.End - 1]);
        Layout lay = DetectLayout(s, node);

        int ownerCol = -1;
        int prevSibling = parent >= 0 ? s.PrevSibling(node) : -1;
        int dashPos = -1;
        int slotStart;
        if (parent < 0)
        {
            slotStart = -1;
        }
        else if (s.Node(parent).Kind == NodeKind.Mapping)
        {
            ownerCol = inFlow ? 0 : s.Column(s.EntryStart(prevSibling));
            slotStart = s.ColonEnd(prevSibling);
        }
        else
        {
            if (!inFlow)
            {
                dashPos = s.DashOf(node, prevSibling);
                ownerCol = s.Column(dashPos);
                slotStart = dashPos + 1;
            }
            else
            {
                slotStart = -1;
            }
        }

        using var body = new ByteBuffer();
        using var edit = new ByteBuffer();
        bool anchor = s.HasAnchor(node);
        bool propsUntouched = !hasTag || keepTag;
        bool hasProps = anchor || (hasTag && keepTag);

        // The value's text with LF breaks, and whether it ends with a break of its own.
        bool valueEndsWithBreak = false;
        ScalarWriteStyle pref = StyleOf(n);
        EmitContext ctx = inFlow ? EmitContext.FlowValue : EmitContext.BlockValue;
        int contentIndent = ownerCol + lay.Step;
        if (n.Kind == NodeKind.Scalar && (pref is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded) && n.Aux - ownerCol is >= 1 and <= 9)
        {
            contentIndent = n.Aux;
        }

        // A string with line breaks inside a collection that replaces a block scalar keeps that block style.
        ScalarWriteStyle nested = n.Kind == NodeKind.Scalar && pref is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded ? pref : ScalarWriteStyle.Plain;
        bool oldKeep = n.Kind == NodeKind.Scalar && (ScalarStyle)n.Style is ScalarStyle.Literal or ScalarStyle.Folded && (Chomping)n.Chomp == Chomping.Keep;
        int regionEnd = oldKeep ? DocState.TrimBlankLines(src, n.Start, n.End, n.Aux) : n.End;

        // Blank lines follow the old scalar. A kept block scalar as the replacement's last value would take
        // them as its own content, so that one value is written another way.
        bool blanksFollow = !oldKeep && nested != ScalarWriteStyle.Plain && BlankLinesEnd(src, regionEnd) > regionEnd;

        if (v.IsScalar)
        {
            ref readonly VNode sv = ref v[0];
            if (sv.Scalar == ScalarKind.String && pref is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded && n.Kind == NodeKind.Scalar && ctx == EmitContext.BlockValue
                && Utf8YamlWriter.CanWriteBlock(sv.Text, ownerCol, contentIndent))
            {
                ReadOnlySpan<byte> header = HeaderComment(s, node);
                BlockScalar.Write(body, sv.Text, pref == ScalarWriteStyle.Folded, ownerCol, contentIndent, HeaderOf(src, n.Start), header, out bool keepsBlanks);
                valueEndsWithBreak = true;
                if (keepsBlanks)
                {
                    regionEnd = BlankLinesEnd(src, regionEnd);
                }
            }
            else
            {
                if (n.Kind == NodeKind.Scalar && pref is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded)
                {
                    pref = ScalarWriteStyle.Plain;
                }

                ScalarWriteStyle used = ValueWriter.WriteScalar(body, sv, pref, ctx, ownerCol, contentIndent, s.EditMode);
                valueEndsWithBreak = ValueWriter.EndsWithBreak(used);
            }
        }
        else if (v[0].Count == 0)
        {
            body.Write(v[0].Kind == VKind.Mapping ? "{}"u8 : "[]"u8);
        }
        else if (flowStyle)
        {
            ValueWriter.WriteFlow(body, v, 0, s.EditMode);
        }

        int regionStart;
        if (inline)
        {
            regionStart = propsUntouched ? n.Start : s.PropStart(node);
            if (ownerCol >= 0 && !inFlow && !valueEndsWithBreak && s.StartsLine(regionStart) && s.Column(regionStart) <= ownerCol)
            {
                // The old value began a line at or left of its key's column (a block scalar header may). Text there
                // would be read as the next entry, so the new value is indented.
                edit.WriteSpaces(ownerCol + lay.Step - s.Column(regionStart));
            }

            if (!propsUntouched)
            {
                WriteProps(s, node, propsUntouched, anchor, edit);
                if (anchor)
                {
                    edit.Write((byte)' ');
                }
            }
            else if ((n.Flags & NodeFlags.Bare) != 0)
            {
                // A flow entry with no value: the colon is missing as well.
                edit.Write(": "u8);
            }
            else if ((n.Flags & NodeFlags.Empty) != 0 && n.Start > 0 && !(src[n.Start - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                edit.Write((byte)' ');
            }

            edit.Write(body.WrittenSpan);
            if (oldEndsWithBreak && !valueEndsWithBreak)
            {
                edit.Write((byte)'\n');
            }

            if (parent < 0 && (n.Flags & NodeFlags.Empty) != 0 && !valueEndsWithBreak && regionEnd < src.Length && !DocState.IsBreak(src[regionEnd])
                && PastMarkerComment(src, regionEnd) == regionEnd)
            {
                // An empty document that ends where a marker begins: the marker needs its own line. A comment on
                // the marker's line is not a marker, and it stays after the value.
                edit.Write((byte)'\n');
            }
        }
        else if (parent >= 0 && !inFlow && slotStart >= 0 && n.Start > slotStart && HasCommentIn(s, slotStart, n.Start) && !PropsCrossLines(s, node))
        {
            // A comment sits between the key (or dash) and a value on a later line. It stays, and only the
            // value's own line and lines are replaced. Properties on the key's line stay there (or lose the
            // tag that no longer fits); properties on the value's line are written again in front of the value.
            return ReplaceAfterComment(s, limits, node, v, body.WrittenSpan, lay, ownerCol, oldEndsWithBreak, valueEndsWithBreak, regionEnd, nested, blanksFollow, keepTag);
        }
        else
        {
            bool root = parent < 0;
            bool endsAtBreak = false;
            bool keptComment = false;
            regionStart = root ? (hasProps || hasTag ? s.PropStart(node) : n.Start) : slotStart;

            // A comment after a scalar or a flow value (or in a block scalar's header) belongs to the entry's line.
            // A block collection starts on the next line, so the comment moves up to the key instead of down to the
            // collection's last line.
            ReadOnlySpan<byte> trailing = default;
            if (!root && newBlockNonEmpty && s.Node(parent).Kind == NodeKind.Mapping && !inFlow && (n.Flags & NodeFlags.Empty) == 0 && !oldBlockColl)
            {
                if (n.Kind == NodeKind.Scalar && (ScalarStyle)n.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                {
                    trailing = HeaderComment(s, node).Trim((byte)' ');
                }
                else
                {
                    int commentEnd = PastMarkerComment(src, n.End);
                    if (commentEnd > n.End)
                    {
                        trailing = src.Slice(n.End, commentEnd - n.End).Trim((byte)' ');
                        regionEnd = Math.Max(regionEnd, commentEnd);
                    }
                }
            }
            if (root && newBlockNonEmpty)
            {
                if (hasProps && propsUntouched)
                {
                    // The properties stay on the marker line; the collection starts on the next one.
                    regionStart = (n.Flags & NodeFlags.Empty) != 0 ? PastMarkerComment(src, s.PropEnd(node)) : s.PropEnd(node);
                    regionEnd = Math.Max(regionEnd, regionStart);
                    edit.Write((byte)'\n');

                    // The break that ends the marker's line is still there, so the new lines need no final one.
                    endsAtBreak = regionStart < src.Length && DocState.IsBreak(src[regionStart]);
                }
                else if (hasProps)
                {
                    WriteProps(s, node, propsUntouched, anchor, edit);

                    // A comment on the marker's line stays there, after the properties that remain.
                    int propEnd = s.PropEnd(node);
                    int commentEnd = PastMarkerComment(src, propEnd);
                    if (commentEnd > propEnd)
                    {
                        edit.Write((byte)' ');
                        edit.Write(src.Slice(propEnd, commentEnd - propEnd).Trim(" \t"u8));
                    }

                    if ((n.Flags & NodeFlags.Empty) != 0)
                    {
                        regionEnd = Math.Max(regionEnd, commentEnd);
                        endsAtBreak = commentEnd < src.Length && DocState.IsBreak(src[commentEnd]);
                    }

                    edit.Write((byte)'\n');
                }
                else
                {
                    // The collection starts a line of its own; a block collection cannot share the line of a "---" marker.
                    // lineEnd is where the marker's line ends, once a tag that no longer fits is gone.
                    int lineEnd = regionStart;
                    bool wroteComment = false;
                    if ((n.Flags & NodeFlags.Empty) != 0 || hasTag)
                    {
                        // A comment on the marker's line stays there.
                        int tagEnd = hasTag ? s.PropEnd(node) : regionStart;
                        lineEnd = PastMarkerComment(src, tagEnd);
                        regionEnd = Math.Max(regionEnd, lineEnd);
                        if (!hasTag)
                        {
                            regionStart = lineEnd;
                        }
                        else if (lineEnd > tagEnd)
                        {
                            // The tag goes with the text around it, so the comment is written again where the tag was.
                            edit.Write((byte)' ');
                            edit.Write(src.Slice(tagEnd, lineEnd - tagEnd).Trim(" \t"u8));
                            wroteComment = true;
                        }
                    }

                    while (regionStart > 0 && src[regionStart - 1] is (byte)' ' or (byte)'\t')
                    {
                        regionStart--;
                    }

                    // A comment written again here must end its line, even when the text starts with it.
                    if (wroteComment || (regionStart > 0 && !DocState.IsBreak(src[regionStart - 1])))
                    {
                        edit.Write((byte)'\n');

                        // The break that ends the marker's line is still there, so the new lines need no final one.
                        int at = (n.Flags & NodeFlags.Empty) != 0 ? lineEnd : regionStart;
                        endsAtBreak = at < src.Length && DocState.IsBreak(src[at]);
                    }
                }
            }
            else if (hasProps)
            {
                if (!root)
                {
                    edit.Write((byte)' ');
                }

                WriteProps(s, node, propsUntouched, anchor, edit);
            }
            else if (!root && newBlockNonEmpty && (n.Flags & NodeFlags.Empty) != 0 && !hasTag)
            {
                // A comment after a key with no value belongs to the key: the collection starts on the next line.
                int afterComment = PastMarkerComment(src, regionStart);
                keptComment = afterComment > regionStart;
                regionStart = afterComment;
                regionEnd = Math.Max(regionEnd, regionStart);
            }

            if (newBlockNonEmpty)
            {
                bool endsInBlock;
                using var lines = new ByteBuffer();
                if (parent >= 0 && s.Node(parent).Kind == NodeKind.Sequence)
                {
                    bool compact = !hasProps && !keptComment;
                    if (compact)
                    {
                        edit.Write((byte)' ');
                    }
                    else
                    {
                        edit.Write((byte)'\n');
                    }

                    ValueWriter.WriteBlock(lines, v, 0, ownerCol + 2, compact, lay, out endsInBlock, valueStyle: nested, blanksFollow: blanksFollow);
                }
                else if (parent >= 0)
                {
                    if (!trailing.IsEmpty)
                    {
                        edit.Write((byte)' ');
                        edit.Write(trailing);
                    }

                    edit.Write((byte)'\n');
                    int col = ownerCol + (v[0].Kind == VKind.Mapping ? lay.Step : lay.SeqOffset);
                    ValueWriter.WriteBlock(lines, v, 0, col, false, lay, out endsInBlock, valueStyle: nested, blanksFollow: blanksFollow);
                }
                else
                {
                    ValueWriter.WriteBlock(lines, v, 0, 0, false, lay, out endsInBlock, valueStyle: nested, blanksFollow: blanksFollow);
                }

                ReadOnlySpan<byte> text = lines.WrittenSpan;
                // An empty root has no line of its own yet, so the new lines keep their final break.
                // A stream that ends without a break gets none, unless the last line is a block scalar whose value needs it.
                if ((!oldEndsWithBreak && !endsInBlock && !(root && (n.Flags & NodeFlags.Empty) != 0)) || (endsAtBreak && (n.Flags & NodeFlags.Empty) != 0))
                {
                    text = text.Slice(0, text.Length - 1);
                }

                if (oldKeep && text.EndsWith("\n\n"u8))
                {
                    // The new text carries its own blank lines (a kept block scalar inside it): the old ones go.
                    regionEnd = BlankLinesEnd(src, regionEnd);
                }

                edit.Write(text);
            }
            else
            {
                if (!root || hasProps)
                {
                    edit.Write((byte)' ');
                }

                edit.Write(body.WrittenSpan);
                if (root && hasTag && (n.Flags & NodeFlags.Empty) == 0 && !valueEndsWithBreak)
                {
                    // The comment on the marker's line followed the properties; the value takes the line now.
                    int propEnd = s.PropEnd(node);
                    int commentEnd = PastMarkerComment(src, propEnd);
                    if (commentEnd > propEnd)
                    {
                        edit.Write((byte)' ');
                        edit.Write(src.Slice(propEnd, commentEnd - propEnd).Trim(" \t"u8));
                    }
                }

                if (oldEndsWithBreak && !valueEndsWithBreak)
                {
                    edit.Write((byte)'\n');
                }
            }
        }

        using var final = new ByteBuffer(edit.Count + 16);
        ValueWriter.WriteWithBreaks(edit.WrittenSpan, lay.Nl, final);
        return Commit(s, limits, regionStart, regionEnd, final);
    }

    // The end of the comment that follows blanks at a position, or the position itself when there is none.
    private static int PastMarkerComment(ReadOnlySpan<byte> src, int pos)
    {
        int q = pos;
        while (q < src.Length && src[q] is (byte)' ' or (byte)'\t')
        {
            q++;
        }

        if (q >= src.Length || src[q] != (byte)'#')
        {
            return pos;
        }

        int e = src.Slice(q).IndexOfAny((byte)'\n', (byte)'\r');
        return e < 0 ? src.Length : q + e;
    }

    // The header of the block scalar whose indicator character sits at an offset: its chomping and indentation indicator.
    private static BlockHeader HeaderOf(ReadOnlySpan<byte> src, int at)
    {
        BlockChomping chomping = BlockChomping.Clip;
        int indicator = 0;
        for (int i = at + 1; i < src.Length && i < at + 4; i++)
        {
            byte b = src[i];
            if (b == (byte)'+')
            {
                chomping = BlockChomping.Keep;
            }
            else if (b == (byte)'-')
            {
                chomping = BlockChomping.Strip;
            }
            else if (b is >= (byte)'1' and <= (byte)'9')
            {
                indicator = b - '0';
            }
            else
            {
                break;
            }
        }

        return new BlockHeader(chomping, indicator);
    }

    // Between a key (or dash) and a value on a later line only properties, blanks and comments can lie.
    private static bool HasCommentIn(DocState s, int from, int to)
    {
        ReadOnlySpan<byte> src = s.Text;
        for (int i = from; i < to; i++)
        {
            if (src[i] == (byte)'#' && s.IsCommentAt(i))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the node's properties are split over two lines (an anchor on one, a tag on the next).
    private static bool PropsCrossLines(DocState s, int node)
    {
        int start = s.PropStart(node);
        int end = s.PropEnd(node);
        return end > start && s.LineStart(start) != s.LineStart(end - 1);
    }

    // The value lines of a node that has a comment between its key (or dash) and its first line.
    private static DocState ReplaceAfterComment(
        DocState s,
        YamlLimits limits,
        int node,
        ValueTree v,
        ReadOnlySpan<byte> body,
        Layout lay,
        int ownerCol,
        bool oldEndsWithBreak,
        bool valueEndsWithBreak,
        int regionEnd,
        ScalarWriteStyle nested,
        bool blanksFollow,
        bool keepTag)
    {
        ReadOnlySpan<byte> src = s.Text;
        ref readonly CstNode n = ref s.Node(node);
        bool hasTag = s.HasTag(node);
        bool anchor = s.HasAnchor(node);
        bool propsUntouched = !hasTag || keepTag;
        int propsStart = s.PropStart(node);
        int propsEnd = s.PropEnd(node);
        bool anyProps = propsEnd > propsStart;
        int lineStart = s.LineStart(n.Start);
        bool propsOnValueLine = anyProps && propsStart >= lineStart;
        bool propsOnKeyLine = anyProps && !propsOnValueLine;

        using var props = new ByteBuffer();
        WriteProps(s, node, propsUntouched, anchor, props);
        bool newBlockNonEmpty = !v.IsScalar && v[0].Count > 0;

        // A block sequence may sit as far left as its key; anything else (an empty one, "[]", included) must be
        // indented under it.
        int firstCol = (propsOnValueLine ? propsStart : n.Start) - lineStart;
        int col = v[0].Kind == VKind.Sequence && newBlockNonEmpty ? firstCol : Math.Max(firstCol, ownerCol + lay.Step);

        using var edit = new ByteBuffer();
        if (propsOnValueLine && newBlockNonEmpty && props.Count > 0)
        {
            // Properties of a block collection stand on a line of their own, above it.
            edit.WriteSpaces(col);
            edit.Write(props.WrittenSpan);
            edit.Write((byte)'\n');
        }

        if (newBlockNonEmpty)
        {
            using var lines = new ByteBuffer();
            ValueWriter.WriteBlock(lines, v, 0, col, false, lay, out bool endsInBlock, valueStyle: nested, blanksFollow: blanksFollow);
            ReadOnlySpan<byte> text = lines.WrittenSpan;
            if (!oldEndsWithBreak && !endsInBlock)
            {
                text = text.Slice(0, text.Length - 1);
            }

            if (text.EndsWith("\n\n"u8) && regionEnd < src.Length)
            {
                regionEnd = BlankLinesEnd(src, regionEnd);
            }

            edit.Write(text);
        }
        else
        {
            edit.WriteSpaces(col);
            if (propsOnValueLine && props.Count > 0)
            {
                edit.Write(props.WrittenSpan);
                edit.Write((byte)' ');
            }

            edit.Write(body);
            if (oldEndsWithBreak && !valueEndsWithBreak)
            {
                edit.Write((byte)'\n');
            }
        }

        using var final = new ByteBuffer(edit.Count + props.Count + 16);
        Span<TextEdit> edits = stackalloc TextEdit[2];
        int count = 0;
        if (propsOnKeyLine && !propsUntouched)
        {
            // The tag that no longer fits goes; an anchor stays. Spaces next to it go with it.
            int from = propsStart;
            int to = propsEnd;
            if (props.Count == 0)
            {
                while (to < src.Length && src[to] == (byte)' ')
                {
                    to++;
                }

                if (to >= src.Length || DocState.IsBreak(src[to]))
                {
                    while (from > 0 && src[from - 1] == (byte)' ')
                    {
                        from--;
                    }

                    if ((from == 0 || DocState.IsBreak(src[from - 1])) && to < src.Length)
                    {
                        // The properties filled their own line: the line goes with them.
                        to += src[to] == (byte)'\r' && to + 1 < src.Length && src[to + 1] == (byte)'\n' ? 2 : 1;
                    }
                }
            }

            edits[count++] = new TextEdit(from, to, 0, props.Count);
            final.Write(props.WrittenSpan);
        }

        int textStart = final.Count;
        ValueWriter.WriteWithBreaks(edit.WrittenSpan, lay.Nl, final);
        edits[count++] = new TextEdit(lineStart, regionEnd, textStart, final.Count - textStart);
        return DocState.Rebuild(s, edits.Slice(0, count), final.WrittenSpan, limits);
    }

    // A value for an explicit key that has none: "? a" gets a line ": value" under it.
    private static DocState ReplaceBareExplicit(DocState s, YamlLimits limits, int node, ValueTree v)
    {
        Layout lay = DetectLayout(s, node);
        int key = s.PrevSibling(node);
        int col = s.Column(s.EntryStart(key));
        using var lines = new ByteBuffer();
        lines.WriteSpaces(col);
        lines.Write(": "u8);
        if (v.IsScalar)
        {
            ScalarWriteStyle used = ValueWriter.WriteScalar(lines, v[0], ScalarWriteStyle.Plain, EmitContext.BlockValue, col, col + lay.Step, lay.Mode);
            if (!ValueWriter.EndsWithBreak(used))
            {
                lines.Write((byte)'\n');
            }
        }
        else if (v[0].Count == 0)
        {
            lines.Write(v[0].Kind == VKind.Mapping ? "{}\n"u8 : "[]\n"u8);
        }
        else
        {
            ValueWriter.WriteBlock(lines, v, 0, col + 2, true, lay);
        }

        return AppendLines(s, limits, s.PastLine(s.Node(key).End), lines, lay.Nl, key);
    }

    // Properties to write in front of the value when the old ones cannot be left in place.
    private static void WriteProps(DocState s, int node, bool untouched, bool anchor, ByteBuffer o)
    {
        if (untouched)
        {
            o.Write(s.Text.Slice(s.PropStart(node), s.PropEnd(node) - s.PropStart(node)));
        }
        else if (anchor)
        {
            o.Write((byte)'&');
            o.Write(s.AnchorName(node));
        }
    }

    private static ReadOnlySpan<byte> HeaderComment(DocState s, int node)
    {
        ReadOnlySpan<byte> src = s.Text;
        int start = s.Node(node).Start;
        int bodyStart = DocState.BodyStart(src, start);
        int lineEnd = bodyStart;
        while (lineEnd > start && DocState.IsBreak(src[lineEnd - 1]))
        {
            lineEnd--;
        }

        for (int i = start; i < lineEnd; i++)
        {
            if (src[i] == (byte)'#' && s.IsCommentAt(i))
            {
                int from = i;
                while (from > start && src[from - 1] == (byte)' ')
                {
                    from--;
                }

                return src.Slice(from, lineEnd - from);
            }
        }

        return default;
    }

    /// <summary>The end of the blank lines (only spaces) that start at an offset.</summary>
    private static int BlankLinesEnd(ReadOnlySpan<byte> src, int pos)
    {
        int p = pos;
        while (p < src.Length)
        {
            int q = p;
            while (q < src.Length && src[q] == (byte)' ')
            {
                q++;
            }

            if (q >= src.Length)
            {
                return q > p ? q : p;
            }

            if (!DocState.IsBreak(src[q]))
            {
                return p;
            }

            p = q + (src[q] == (byte)'\r' && q + 1 < src.Length && src[q + 1] == (byte)'\n' ? 2 : 1);
        }

        return p;
    }
}

/// <summary>Writes block scalars, keeping the old chomping indicator when it still fits the value.</summary>
internal static class BlockScalar
{
    /// <summary>Writes a block scalar header and lines. Text uses LF; the caller converts the breaks.</summary>
    /// <param name="o">The destination.</param>
    /// <param name="text">The value (UTF-8). The caller has checked <see cref="Utf8YamlWriter.CanWriteBlock"/>.</param>
    /// <param name="folded">Whether to write a folded scalar.</param>
    /// <param name="parentIndent">Column of the owning key or dash.</param>
    /// <param name="contentIndent">Column of the lines.</param>
    /// <param name="old">The header the scalar had before, kept as far as the new value allows.</param>
    /// <param name="headerSuffix">A comment to put after the header, or empty.</param>
    /// <param name="keepsBlanks">Whether the new value keeps trailing blank lines, which the caller must not leave behind.</param>
    public static void Write(ByteBuffer o, ReadOnlySpan<byte> text, bool folded, int parentIndent, int contentIndent, BlockHeader old, ReadOnlySpan<byte> headerSuffix, out bool keepsBlanks)
    {
        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        int trailing = text.Length - body.Length;
        BlockHeader chosen = Utf8YamlWriter.ResolveHeader(text, parentIndent, contentIndent, old);
        o.Write(folded ? (byte)'>' : (byte)'|');
        if (chosen.IndentIndicator != 0)
        {
            o.Write((byte)('0' + chosen.IndentIndicator));
        }

        if (chosen.Chomping == BlockChomping.Strip)
        {
            o.Write((byte)'-');
        }
        else if (chosen.Chomping == BlockChomping.Keep)
        {
            o.Write((byte)'+');
        }

        keepsBlanks = chosen.Chomping == BlockChomping.Keep;
        o.Write(headerSuffix);
        o.Write((byte)'\n');

        ReadOnlySpan<byte> rest = body;
        int pendingEmpty = 0;
        bool have = false;
        bool prevSpaced = false;
        while (true)
        {
            int nl = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl < 0 ? rest : rest.Slice(0, nl);
            if (line.IsEmpty)
            {
                pendingEmpty++;
            }
            else
            {
                bool spaced = line[0] is (byte)' ' or (byte)'\t';
                if (folded && have && !prevSpaced && !spaced)
                {
                    pendingEmpty++;
                }

                for (int i = 0; i < pendingEmpty; i++)
                {
                    o.Write((byte)'\n');
                }

                o.WriteSpaces(contentIndent);
                o.Write(line);
                o.Write((byte)'\n');
                pendingEmpty = 0;
                have = true;
                prevSpaced = spaced;
            }

            if (nl < 0)
            {
                break;
            }

            rest = rest.Slice(nl + 1);
        }

        if (trailing >= 2)
        {
            for (int i = 0; i < trailing - 1; i++)
            {
                o.Write((byte)'\n');
            }
        }
    }
}
