using System;
using OneYaml.Emitting;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

internal static partial class Editor
{
    /// <summary>Finds the line break, indentation step and sequence offset new text near a node should copy.</summary>
    private static Layout DetectLayout(DocState s, int near)
    {
        string nl = s.BreakNear(s.Node(near).Start);
        int step = 0;
        int seqOff = -1;
        int scope = s.Node(near).Kind == NodeKind.Mapping ? near : s.Node(near).Parent;
        if (scope >= 0 && s.Node(scope).Kind == NodeKind.Mapping)
        {
            Probe(s, scope, ref step, ref seqOff);
        }

        for (int i = 0; i < s.NodeCount && (step == 0 || seqOff < 0); i++)
        {
            if (s.Node(i).Kind == NodeKind.Mapping && !s.Node(i).IsFlow)
            {
                Probe(s, i, ref step, ref seqOff);
            }
        }

        if (step == 0)
        {
            step = 2;
        }

        if (seqOff < 0)
        {
            seqOff = step;
        }

        return new Layout(nl, step, seqOff);
    }

    // A flow collection takes only the line break from its surroundings, so the document is not searched
    // for indentation it would not use.
    private static Layout FlowOrDetect(DocState s, int near)
        => s.Node(near).IsFlow ? new Layout(s.BreakNear(s.Node(near).Start), 2, 2) : DetectLayout(s, near);

    // Looks at the entries of one block mapping for a nested block collection on its own lines.
    private static void Probe(DocState s, int map, ref int step, ref int seqOff)
    {
        int c = map + 1;
        int pairs = s.Node(map).Count / 2;
        for (int p = 0; p < pairs; p++)
        {
            int k = c;
            int v = c + s.Node(k).Size;
            ref readonly CstNode vn = ref s.Node(v);
            if (vn.Kind is NodeKind.Mapping or NodeKind.Sequence && !vn.IsFlow && s.StartsLine(vn.Start))
            {
                int keyCol = s.Column(s.EntryStart(k));
                int col = s.Column(vn.Start);
                if (vn.Kind == NodeKind.Mapping && step == 0 && col > keyCol)
                {
                    step = col - keyCol;
                }
                else if (vn.Kind == NodeKind.Sequence && seqOff < 0 && col >= keyCol)
                {
                    seqOff = col - keyCol;
                }
            }

            c = v + vn.Size;
        }
    }

    private static int LastChild(DocState s, int collection) => s.LastChildOf(collection);

    private static DocState InsertAt(DocState s, YamlLimits limits, int pos, ByteBuffer text, string nl)
    {
        using var final = new ByteBuffer(text.Count + 8);
        ValueWriter.WriteWithBreaks(text.WrittenSpan, nl, final);
        return Commit(s, limits, pos, pos, final);
    }

    // Text for a new last line: if the text ends without a line break, the new line starts with one instead.
    private static DocState AppendLines(DocState s, YamlLimits limits, int pos, ByteBuffer lines, string nl)
    {
        ReadOnlySpan<byte> src = s.Text;
        bool noBreak = pos == s.Length && (pos == 0 || !DocState.IsBreak(src[pos - 1]));
        using var text = new ByteBuffer(lines.Count + 2);
        if (noBreak && pos > 0)
        {
            text.Write((byte)'\n');
            text.Write(lines.WrittenSpan.Slice(0, lines.Count - 1));
        }
        else
        {
            text.Write(lines.WrittenSpan);
        }

        return InsertAt(s, limits, pos, text, nl);
    }

    /// <summary>Adds a member at the end of a mapping.</summary>
    /// <param name="s">The current state.</param>
    /// <param name="limits">Limits for the new state.</param>
    /// <param name="map">A mapping node.</param>
    /// <param name="key">The key's text, UTF-8.</param>
    /// <param name="v">The member's value.</param>
    /// <returns>The next state.</returns>
    public static DocState AddMember(DocState s, YamlLimits limits, int map, byte[] key, ValueTree v)
    {
        ref readonly CstNode m = ref s.Node(map);
        if ((m.Flags & NodeFlags.Pair) != 0)
        {
            throw Fail(YamlErrorCategory.PatchPath, "A key cannot be added to a pair inside a flow sequence.", s, map);
        }

        Layout lay = FlowOrDetect(s, map);
        int lastVal = m.Count > 0 ? LastChild(s, map) : -1;
        int lastKey = lastVal >= 0 ? s.PrevSibling(lastVal) : -1;
        ScalarWriteStyle keyPref = SiblingStyle(s, lastKey);
        ScalarWriteStyle valPref = v.IsScalar ? SiblingStyle(s, lastVal) : ScalarWriteStyle.Plain;
        if (m.IsFlow)
        {
            return AddFlow(s, limits, map, lay, key, v, keyPref, valPref);
        }

        ValueTree tree = ValueTree.Wrap(key, v);
        using var lines = new ByteBuffer();
        int col = s.Column(s.EntryStart(map + 1));
        ValueWriter.WriteBlock(lines, tree, 0, col, false, lay, keyPref, valPref);
        return AppendLines(s, limits, s.PastLine(m.End), lines, lay.Nl);
    }

    /// <summary>Inserts an item into a sequence.</summary>
    /// <param name="s">The current state.</param>
    /// <param name="limits">Limits for the new state.</param>
    /// <param name="seq">A sequence node.</param>
    /// <param name="index">Where the item goes: 0 to the item count (the count appends).</param>
    /// <param name="v">The item.</param>
    /// <returns>The next state.</returns>
    public static DocState InsertItem(DocState s, YamlLimits limits, int seq, int index, ValueTree v)
    {
        ref readonly CstNode q = ref s.Node(seq);
        int count = q.Count;
        if (index < 0 || index > count)
        {
            throw Fail(YamlErrorCategory.PatchPath, "The index is outside the sequence.", s, seq);
        }

        if ((q.Flags & NodeFlags.Pair) != 0)
        {
            throw Fail(YamlErrorCategory.PatchPath, "An item cannot be added to a pair inside a flow sequence.", s, seq);
        }

        Layout lay = FlowOrDetect(s, seq);
        if (q.IsFlow)
        {
            return AddFlow(s, limits, seq, lay, null, v, ScalarWriteStyle.Plain, index > 0 && v.IsScalar ? SiblingStyle(s, s.ChildAt(seq, index - 1)) : (count > 0 && v.IsScalar ? SiblingStyle(s, seq + 1) : ScalarWriteStyle.Plain), index);
        }

        ValueTree tree = ValueTree.Wrap(null, v);
        using var lines = new ByteBuffer();
        if (index == count)
        {
            ScalarWriteStyle pref = v.IsScalar ? SiblingStyle(s, LastChild(s, seq)) : ScalarWriteStyle.Plain;
            ValueWriter.WriteBlock(lines, tree, 0, s.Column(q.Start), false, lay, ScalarWriteStyle.Plain, pref);
            return AppendLines(s, limits, s.PastLine(q.End), lines, lay.Nl);
        }

        int item = s.ChildAt(seq, index);
        int prev = index > 0 ? s.ChildAt(seq, index - 1) : -1;
        int dash = s.DashOf(item, prev);
        int dashCol = s.Column(dash);
        ScalarWriteStyle p = v.IsScalar ? SiblingStyle(s, prev >= 0 ? prev : item) : ScalarWriteStyle.Plain;
        if (s.StartsLine(dash))
        {
            ValueWriter.WriteBlock(lines, tree, 0, dashCol, false, lay, ScalarWriteStyle.Plain, p);
            return InsertAt(s, limits, s.OwnedCommentsStart(s.LineStart(dash)), lines, lay.Nl);
        }

        // The dash follows another indicator on its line ("- - a"): the new item takes its place and the old one moves down.
        ValueWriter.WriteBlock(lines, tree, 0, dashCol, true, lay, ScalarWriteStyle.Plain, p);
        lines.WriteSpaces(dashCol);
        return InsertAt(s, limits, dash, lines, lay.Nl);
    }

    private static DocState AddFlow(DocState s, YamlLimits limits, int coll, Layout lay, byte[]? key, ValueTree v, ScalarWriteStyle keyPref, ScalarWriteStyle valPref, int index = -1)
    {
        ref readonly CstNode c = ref s.Node(coll);
        using var text = new ByteBuffer();
        if (key is not null)
        {
            var kn = new VNode { Kind = VKind.Scalar, Scalar = ScalarKind.String, Size = 1, Text = key };
            ValueWriter.WriteKey(text, kn, keyPref, EmitContext.FlowKey, 0, 0);
            text.Write(": "u8);
        }

        if (v.IsScalar)
        {
            ValueWriter.WriteScalar(text, v[0], valPref, EmitContext.FlowValue, 0, 0);
        }
        else
        {
            ValueWriter.WriteFlow(text, v, 0);
        }

        int count = c.Count;
        if (count == 0)
        {
            using var whole = new ByteBuffer();
            ValueWriter.WriteWithBreaks(text.WrittenSpan, lay.Nl, whole);
            return Commit(s, limits, c.Start + 1, c.End - 1, whole);
        }

        bool map = c.Kind == NodeKind.Mapping;
        int entries = map ? count / 2 : count;
        int at = index < 0 ? entries : index;
        using var edit = new ByteBuffer();
        int pos;
        if (at >= entries)
        {
            int last = LastChild(s, coll);
            pos = s.Node(last).End;
            if (TryAppendAfterComment(s, limits, last, map, text.WrittenSpan, lay.Nl, out DocState? appended))
            {
                return appended!;
            }

            edit.Write(", "u8);
            edit.Write(text.WrittenSpan);
        }
        else
        {
            int first = map ? s.ChildAt(coll, at * 2) : s.ChildAt(coll, at);
            pos = s.PropStart(first);
            edit.Write(text.WrittenSpan);
            edit.Write(", "u8);
        }

        using var final = new ByteBuffer(edit.Count + 8);
        ValueWriter.WriteWithBreaks(edit.WrittenSpan, lay.Nl, final);
        return Commit(s, limits, pos, pos, final);
    }

    // The last item of a flow collection is on a line of its own and ends with a comment: the new item
    // goes on the next line, after the comment, in the same column. The last item gets its comma, and the
    // new one a comma of its own only if the last had one.
    private static bool TryAppendAfterComment(DocState s, YamlLimits limits, int last, bool map, ReadOnlySpan<byte> item, string nl, out DocState? next)
    {
        next = null;
        ReadOnlySpan<byte> src = s.Text;
        int end = s.Node(last).End;
        int entryAt = map ? s.EntryStart(s.PrevSibling(last)) : s.PropStart(last);
        if (!s.StartsLine(entryAt))
        {
            return false;
        }

        int p = end;
        while (p < src.Length && src[p] == (byte)' ')
        {
            p++;
        }

        bool comma = p < src.Length && src[p] == (byte)',';
        if (comma)
        {
            p++;
            while (p < src.Length && src[p] == (byte)' ')
            {
                p++;
            }
        }

        if (p >= src.Length || src[p] != (byte)'#' || !s.IsCommentAt(p))
        {
            return false;
        }

        int lineEnd = s.LineBreakAt(p);
        using var text = new ByteBuffer(item.Length + 16);
        if (!comma)
        {
            text.Write((byte)',');
        }

        text.Write(src.Slice(end, lineEnd - end));
        text.Write((byte)'\n');
        text.WriteSpaces(s.Column(entryAt));
        text.Write(item);
        if (comma)
        {
            text.Write((byte)',');
        }

        using var final = new ByteBuffer(text.Count + 8);
        ValueWriter.WriteWithBreaks(text.WrittenSpan, nl, final);
        next = Commit(s, limits, end, lineEnd, final);
        return true;
    }

    private static bool IsKeepBlock(in CstNode n)
        => n.Kind == NodeKind.Scalar && (ScalarStyle)n.Style is ScalarStyle.Literal or ScalarStyle.Folded && (Chomping)n.Chomp == Chomping.Keep;

    /// <summary>Removes an entry: a mapping value with its key, or a sequence item.</summary>
    /// <param name="s">The current state.</param>
    /// <param name="limits">Limits for the new state.</param>
    /// <param name="node">The value or item to remove.</param>
    /// <returns>The next state.</returns>
    public static DocState Remove(DocState s, YamlLimits limits, int node)
    {
        int parent = s.Node(node).Parent;
        if (parent < 0)
        {
            throw Fail(YamlErrorCategory.PatchInvalid, "The root of a document cannot be removed.", s, node);
        }

        ref readonly CstNode p = ref s.Node(parent);
        if ((p.Flags & NodeFlags.Pair) != 0 && p.Count == 2)
        {
            // The only entry of a single pair in a flow sequence: the item stays, as an empty mapping.
            CheckAnchorsFree(s, s.PrevSibling(node), s.After(node) - s.PrevSibling(node), -1);
            return Replace(s, limits, parent, ValueTree.Empty(true));
        }

        if ((p.Flags & NodeFlags.Pair) != 0)
        {
            throw Fail(YamlErrorCategory.PatchPath, "An entry of a pair inside a flow sequence cannot be removed on its own.", s, node);
        }

        bool map = p.Kind == NodeKind.Mapping;
        int first = map ? s.PrevSibling(node) : node;
        CheckAnchorsFree(s, first, s.After(node) - first, -1);
        if (p.Count == (map ? 2 : 1))
        {
            // The last entry goes: the collection stays, empty.
            return Replace(s, limits, parent, ValueTree.Empty(map));
        }

        ReadOnlySpan<byte> src = s.Text;
        string nl = s.BreakNear(s.Node(node).Start);
        int end = s.Node(node).End;
        int prevEntry = s.PrevSibling(first);
        int nextEntry = s.After(node) < s.After(parent) ? s.After(node) : -1;
        if (p.IsFlow)
        {
            int from;
            int to;
            int entryAt = map ? s.EntryStart(first) : s.PropStart(first);
            int after = end;
            while (after < src.Length && src[after] == (byte)' ')
            {
                after++;
            }

            bool ownComma = false;
            if (after < src.Length && src[after] == (byte)',')
            {
                ownComma = true;
                after++;
                while (after < src.Length && src[after] == (byte)' ')
                {
                    after++;
                }
            }

            // Without a comma of its own an entry that is not the last one is separated from the next by the
            // comma at the start of the next line; taking its line alone would leave that comma first.
            if (s.StartsLine(entryAt) && (ownComma || nextEntry < 0) && (after >= src.Length || DocState.IsBreak(src[after]) || (src[after] == (byte)'#' && s.IsCommentAt(after))))
            {
                // The item has its lines to itself, its own comment included: they go, and nothing else moves.
                using var lines = new ByteBuffer(1);
                return Commit(s, limits, s.OwnedCommentsStart(s.LineStart(entryAt)), s.PastLine(after), lines);
            }

            if (prevEntry < 0)
            {
                from = s.PropStart(first);
                to = s.PropStart(nextEntry);
            }
            else
            {
                from = s.Node(prevEntry).End;
                to = end;
            }

            using var empty = new ByteBuffer(1);
            return Commit(s, limits, from, to, empty);
        }

        int entryStart = map ? s.EntryStart(first) : s.DashOf(node, prevEntry);
        int start;
        int stop;
        if (s.StartsLine(entryStart))
        {
            start = s.OwnedCommentsStart(s.LineStart(entryStart));
            stop = s.PastLine(end);
            if (first > 0 && IsKeepBlock(s.Node(first - 1)))
            {
                // A kept block scalar ends right before this entry. The blank lines after the entry would
                // join it and become part of its value, so they go with the entry.
                stop = BlankLinesEnd(src, stop);
            }

            if (stop == s.Length && stop > 0 && !DocState.IsBreak(src[stop - 1]) && start > 0)
            {
                // The text had no final line break; the new last line should not gain one.
                start -= start >= 2 && src[start - 1] == (byte)'\n' && src[start - 2] == (byte)'\r' ? 2 : 1;
            }
        }
        else
        {
            start = entryStart;
            stop = map ? s.EntryStart(nextEntry) : s.DashOf(nextEntry, node);
        }

        _ = nl;
        using var none = new ByteBuffer(1);
        return Commit(s, limits, start, stop, none);
    }
}
