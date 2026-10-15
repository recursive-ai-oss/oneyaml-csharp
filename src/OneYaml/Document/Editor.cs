using System;
using OneYaml.Emitting;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// The edit engine. Every edit, whether it comes from an indexer or a pointer, is one of four
/// operations that read a state and return the next one: replace a node, add a mapping member,
/// insert a sequence item, remove an entry. None of them changes the state it is given, so a
/// failure leaves the document as it was, and a series of them can be chained and committed at once.
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

        ReadOnlySpan<byte> name = raw;
        if (name.Length > 3 && name[1] == (byte)'<' && name[^1] == (byte)'>')
        {
            name = name.Slice(2, name.Length - 3);
        }

        if (name.StartsWith("!!"u8) || name.StartsWith("tag:yaml.org,2002:"u8))
        {
            ReadOnlySpan<byte> bare = name.StartsWith("!!"u8) ? name.Slice(2) : name.Slice(18);
            if (bare.SequenceEqual("map"u8))
            {
                return nv.Kind == VKind.Mapping;
            }

            if (bare.SequenceEqual("seq"u8))
            {
                return nv.Kind == VKind.Sequence;
            }
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

        bool oldKeep = n.Kind == NodeKind.Scalar && (ScalarStyle)n.Style is ScalarStyle.Literal or ScalarStyle.Folded && (Chomping)n.Chomp == Chomping.Keep;
        int regionEnd = oldKeep ? DocState.TrimBlankLines(src, n.Start, n.End, n.Aux) : n.End;
        if (v.IsScalar)
        {
            ref readonly VNode sv = ref v[0];
            if (sv.Scalar == ScalarKind.String && pref is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded && n.Kind == NodeKind.Scalar && ctx == EmitContext.BlockValue
                && Utf8YamlWriter.CanWriteBlock(sv.Text, ownerCol, contentIndent))
            {
                ReadOnlySpan<byte> header = HeaderComment(s, node);
                var chomp = (Chomping)n.Chomp;
                BlockScalar.Write(body, sv.Text, pref == ScalarWriteStyle.Folded, ownerCol, contentIndent, chomp, header, out bool keepsBlanks);
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

                ScalarWriteStyle used = ValueWriter.WriteScalar(body, sv, pref, ctx, ownerCol, contentIndent);
                valueEndsWithBreak = ValueWriter.EndsWithBreak(used);
            }
        }
        else if (v[0].Count == 0)
        {
            body.Write(v[0].Kind == VKind.Mapping ? "{}"u8 : "[]"u8);
        }
        else if (flowStyle)
        {
            ValueWriter.WriteFlow(body, v, 0);
        }

        int regionStart;
        if (inline)
        {
            regionStart = propsUntouched ? n.Start : s.PropStart(node);
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

            if (parent < 0 && (n.Flags & NodeFlags.Empty) != 0 && !valueEndsWithBreak && regionEnd < src.Length && !DocState.IsBreak(src[regionEnd]))
            {
                // An empty document that ends where a marker begins: the marker needs its own line.
                edit.Write((byte)'\n');
            }
        }
        else
        {
            bool root = parent < 0;
            regionStart = root ? (hasProps || hasTag ? s.PropStart(node) : n.Start) : slotStart;
            if (root && newBlockNonEmpty)
            {
                if (hasProps && propsUntouched)
                {
                    // The properties stay on the marker line; the collection starts on the next one.
                    regionStart = s.PropEnd(node);
                    edit.Write((byte)'\n');
                }
                else if (hasProps)
                {
                    WriteProps(s, node, propsUntouched, anchor, edit);
                    edit.Write((byte)'\n');
                }
                else
                {
                    // The collection starts a line of its own; a block collection cannot share the line of a "---" marker.
                    while (regionStart > 0 && src[regionStart - 1] is (byte)' ' or (byte)'\t')
                    {
                        regionStart--;
                    }

                    if (regionStart > 0 && !DocState.IsBreak(src[regionStart - 1]))
                    {
                        edit.Write((byte)'\n');
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

            if (newBlockNonEmpty)
            {
                using var lines = new ByteBuffer();
                if (parent >= 0 && s.Node(parent).Kind == NodeKind.Sequence)
                {
                    bool compact = !hasProps;
                    if (compact)
                    {
                        edit.Write((byte)' ');
                    }
                    else
                    {
                        edit.Write((byte)'\n');
                    }

                    ValueWriter.WriteBlock(lines, v, 0, ownerCol + 2, compact, lay);
                }
                else if (parent >= 0)
                {
                    edit.Write((byte)'\n');
                    int col = ownerCol + (v[0].Kind == VKind.Mapping ? lay.Step : lay.SeqOffset);
                    ValueWriter.WriteBlock(lines, v, 0, col, false, lay);
                }
                else
                {
                    ValueWriter.WriteBlock(lines, v, 0, 0, false, lay);
                }

                ReadOnlySpan<byte> text = lines.WrittenSpan;
                // An empty root has no line of its own yet, so the new lines keep their final break.
                if (!oldEndsWithBreak && !(root && (n.Flags & NodeFlags.Empty) != 0))
                {
                    text = text.Slice(0, text.Length - 1);
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
            ScalarWriteStyle used = ValueWriter.WriteScalar(lines, v[0], ScalarWriteStyle.Plain, EmitContext.BlockValue, col, col + lay.Step);
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

        return AppendLines(s, limits, s.PastLine(s.Node(key).End), lines, lay.Nl);
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
    /// <param name="old">The chomping the scalar had before.</param>
    /// <param name="headerSuffix">A comment to put after the header, or empty.</param>
    /// <param name="keepsBlanks">Whether the new value keeps trailing blank lines, which the caller must not leave behind.</param>
    public static void Write(ByteBuffer o, ReadOnlySpan<byte> text, bool folded, int parentIndent, int contentIndent, Chomping old, ReadOnlySpan<byte> headerSuffix, out bool keepsBlanks)
    {
        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        int trailing = text.Length - body.Length;
        o.Write(folded ? (byte)'>' : (byte)'|');
        if (body.TrimStart((byte)'\n')[0] == (byte)' ')
        {
            o.Write((byte)('0' + (contentIndent - parentIndent)));
        }

        bool keep = trailing >= 2 || (trailing == 1 && old == Chomping.Keep);
        if (trailing == 0)
        {
            o.Write((byte)'-');
        }
        else if (keep)
        {
            o.Write((byte)'+');
        }

        keepsBlanks = keep;
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
