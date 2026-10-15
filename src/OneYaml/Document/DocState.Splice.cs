using System;
using System.Buffers;
using OneYaml.Parsing;

namespace OneYaml.Documents;

internal sealed partial class DocState
{
    // An edit is tried in at most this many enclosing collections, innermost first, before the whole text
    // is parsed again.
    private const int MaxSpliceAttempts = 4;

    // A block region is read standalone behind spaces that stand for its column; refuse absurd columns.
    private const int MaxSpliceColumn = 4096;

    private byte _directives;

    // The last line break found after a position, so that a long line is not searched again by every edit
    // of a patch. It is kept per thread, for one state, and moves with the edits made in place.
    // The owner is an identity number, not the state itself, so that the cache keeps nothing alive.
    [ThreadStatic]
    private static long s_breakOwner;

    private static long s_nextId;

    private readonly long _id = System.Threading.Interlocked.Increment(ref s_nextId);

    [ThreadStatic]
    private static int s_breakFrom;

    [ThreadStatic]
    private static int s_breakAt;

    /// <summary>
    /// Called with every state an edit produces: the state, the limits it was built with, and how many
    /// bytes were parsed to make it (the whole text for a full parse). Tests use it to compare the state
    /// with a fresh parse of the same bytes.
    /// </summary>
    internal static Action<DocState, YamlLimits, int>? RebuildObserver { get; set; }

    /// <summary>Makes edits on this thread parse the whole text again: the reference the incremental path is compared with.</summary>
    [ThreadStatic]
    internal static bool NoSplice;

    /// <summary>
    /// Whether the state may be changed in place. A patch holds the states it made on the way and says so,
    /// so that its next operation edits the one it owns instead of copying it.
    /// </summary>
    internal bool Exclusive { get; set; }

    /// <summary>Whether a directive may precede a document: tags are then not resolved with the default handles.</summary>
    private bool HasDirectives
    {
        get
        {
            if (_directives == 0)
            {
                _directives = 1;
                ReadOnlySpan<byte> src = Text;
                for (int d = 0; d < DocCount; d++)
                {
                    if (!_docs[d].ExplicitStart)
                    {
                        continue;
                    }

                    int from = d == 0 ? 0 : _docs[d - 1].EndPos;
                    int to = _docs[d].StartPos;
                    if (to > from && src.Slice(from, to - from).Contains((byte)'%'))
                    {
                        _directives = 2;
                        break;
                    }
                }
            }

            return _directives == 2;
        }
    }

    // The state the region is parsed into is kept for the next edit on this thread; the arrays it holds
    // come from the pool and go back after each use.
    [ThreadStatic]
    private static DocState? s_scratch;

    [ThreadStatic]
    private static YamlLimits? s_limitsSource;

    // The limits for a region by the number of levels above it, for the limits last used.
    [ThreadStatic]
    private static YamlLimits?[]? s_limitsDerived;

    private static DocState RentScratch(byte[] buf, int length, DocState like)
    {
        DocState? d = s_scratch;
        if (d is null)
        {
            return new DocState(buf, length, like.Encoding, like.HasByteOrderMark, like.AllowDuplicateKeys);
        }

        s_scratch = null;
        d._buf = buf;
        d.Length = length;
        d.Encoding = like.Encoding;
        d.HasByteOrderMark = like.HasByteOrderMark;
        d.AllowDuplicateKeys = like.AllowDuplicateKeys;
        d.NodeCount = 0;
        d.DocCount = 0;
        d.NoRealDocument = false;
        d.CommentCount = 0;
        d.AliasCount = 0;
        d.PropCountRows = 0;
        d._directives = 0;
        d.Exclusive = false;
        return d;
    }

    // The limits for a region that starts that many levels down: the same, with less depth left.
    private static YamlLimits LimitsForDepth(YamlLimits limits, int depth)
    {
        if (depth == limits.MaxDepth)
        {
            return limits;
        }

        int levels = limits.MaxDepth - depth;
        if (levels >= 64)
        {
            return limits with { MaxDepth = depth };
        }

        if (!ReferenceEquals(s_limitsSource, limits) || s_limitsDerived is null)
        {
            s_limitsDerived = new YamlLimits?[64];
            s_limitsSource = limits;
        }

        return s_limitsDerived[levels] ??= limits with { MaxDepth = depth };
    }

    // The indentation of the first line with content (not blank, not a comment) among the lines that
    // the text starts, or -1 when there is none. The text starts a line when the edit is at a line start;
    // otherwise its first line continues the old one and is skipped.
    private static int FirstContentColumn(ReadOnlySpan<byte> src, int at, ReadOnlySpan<byte> text, out int contentAt)
    {
        contentAt = 0;
        int i = 0;
        if (at > 0 && !IsBreak(src[at - 1]))
        {
            int b = text.IndexOfAny((byte)'\n', (byte)'\r');
            if (b < 0)
            {
                return -1;
            }

            i = b + 1;
        }

        while (i < text.Length)
        {
            int j = i;
            while (j < text.Length && text[j] == (byte)' ')
            {
                j++;
            }

            if (j == text.Length)
            {
                return -1;
            }

            if (IsBreak(text[j]))
            {
                i = j + 1;
                continue;
            }

            if (text[j] == (byte)'#')
            {
                int b = text.Slice(j).IndexOfAny((byte)'\n', (byte)'\r');
                if (b < 0)
                {
                    return -1;
                }

                i = j + b + 1;
                continue;
            }

            contentAt = j;
            return j - i;
        }

        return -1;
    }

    private static bool IsDash(ReadOnlySpan<byte> text, int at)
        => text[at] == (byte)'-' && (at + 1 == text.Length || text[at + 1] is (byte)' ' or (byte)'\n' or (byte)'\r');

    private static bool IsTriviaByte(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'#';

    /// <summary>Whether a collection can stand as the region that an edit is parsed again in.</summary>
    private bool IsRegionRoot(int node)
    {
        ref readonly CstNode n = ref _nodes[node];
        return n.Kind is NodeKind.Mapping or NodeKind.Sequence && (n.Flags & (NodeFlags.Pair | NodeFlags.IsKey)) == 0;
    }

    /// <summary>
    /// Whether the edit lies inside the collection's own text: between its first byte and its last, or
    /// strictly between the brackets of a flow collection.
    /// </summary>
    private bool Encloses(int node, int s, int e)
    {
        ref readonly CstNode n = ref _nodes[node];
        if (n.Kind is not (NodeKind.Mapping or NodeKind.Sequence))
        {
            return false;
        }

        if (n.IsFlow)
        {
            return (n.Flags & NodeFlags.Pair) == 0 && s > n.Start && e <= n.Aux;
        }

        // The text after the last entry, up to the next token, is where lines are added at the end.
        return s >= n.Start && e <= n.Aux;
    }

    // The last node in a range of the node array that starts before a position, or -1. Nodes are stored in
    // document order, so their starts do not decrease and a binary search finds it.
    private int LastStartingBefore(int from, int to, int position)
    {
        int lo = from;
        int hi = to - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_nodes[mid].Start < position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    // The child of a collection that a node below it belongs to.
    private int ChildContaining(int node, int collection)
    {
        while (node >= 0 && _nodes[node].Parent != collection)
        {
            node = _nodes[node].Parent;
        }

        return node;
    }

    /// <summary>Finds the innermost collection that encloses an edit, or -1.</summary>
    private int InnermostEnclosing(int s, int e)
    {
        int root = -1;
        for (int d = 0; d < DocCount && root < 0; d++)
        {
            if (Encloses(_docs[d].Root, s, e))
            {
                root = _docs[d].Root;
            }
        }

        if (root < 0)
        {
            return -1;
        }

        // The collection around the last node that starts before the edit, or around a node it contains.
        int cur = LastStartingBefore(root + 1, root + _nodes[root].Size, s);
        if (cur < 0)
        {
            cur = root;
        }

        while (cur != root && !Encloses(cur, s, e))
        {
            cur = _nodes[cur].Parent;
        }

        return cur;
    }

    private static DocState? TrySplice(DocState old, ReadOnlySpan<TextEdit> edits, ReadOnlySpan<byte> texts, YamlLimits limits, out int reparsed)
    {
        reparsed = 0;
        if (NoSplice || edits.Length == 0 || old.NoRealDocument || old.HasDirectives)
        {
            return null;
        }

        int s = edits[0].Start;
        int e = edits[edits.Length - 1].End;
        byte[]? composed = null;
        try
        {
            ReadOnlySpan<byte> text;
            if (edits.Length == 1)
            {
                text = texts.Slice(edits[0].TextStart, edits[0].TextLength);
            }
            else
            {
                // Several edits become one replacement of the span between the first and the last.
                long total = e - s;
                foreach (TextEdit ed in edits)
                {
                    total += ed.TextLength;
                }

                composed = ArrayPool<byte>.Shared.Rent((int)Math.Min(total, int.MaxValue - 64));
                int w = 0;
                int r = s;
                ReadOnlySpan<byte> src = old.Text;
                foreach (TextEdit ed in edits)
                {
                    src.Slice(r, ed.Start - r).CopyTo(composed.AsSpan(w));
                    w += ed.Start - r;
                    texts.Slice(ed.TextStart, ed.TextLength).CopyTo(composed.AsSpan(w));
                    w += ed.TextLength;
                    r = ed.End;
                }

                text = composed.AsSpan(0, w);
            }

            long newLength = (long)old.Length + text.Length - (e - s);
            if (newLength > int.MaxValue - 64 || newLength > limits.MaxInputBytes)
            {
                return null;
            }

            int cur = old.InnermostEnclosing(s, e);
            for (int attempt = 0; cur >= 0 && attempt < MaxSpliceAttempts; attempt++, cur = old._nodes[cur].Parent)
            {
                if (old.IsRegionRoot(cur))
                {
                    DocState? result = TryRegion(old, cur, s, e, text, limits, out reparsed);
                    if (result is not null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }
        finally
        {
            if (composed is not null)
            {
                ArrayPool<byte>.Shared.Return(composed);
            }
        }
    }

    // Where the first byte of an item is: for a block collection the first token of its line, for a flow
    // collection the item's own start (or just after the opening bracket for the first item).
    private int ItemStart(int collection, int index, int firstNode, int previousEnd)
    {
        if (_nodes[collection].IsFlow)
        {
            return index == 0 ? _nodes[collection].Start + 1 : PropStart(firstNode);
        }

        return index == 0 ? _nodes[collection].Start : SkipTrivia(Text, previousEnd);
    }

    // The first node of the item that a node of a collection belongs to: the key of a mapping's pair.
    private int ItemFirst(int collection, int child)
    {
        return _nodes[collection].Kind == NodeKind.Mapping && (_nodes[child].Flags & NodeFlags.IsKey) == 0 ? PrevSibling(child) : child;
    }

    private static DocState? TryRegion(DocState old, int n, int s, int e, ReadOnlySpan<byte> text, YamlLimits limits, out int reparsed)
    {
        reparsed = 0;
        ReadOnlySpan<byte> src = old.Text;
        ref readonly CstNode nn = ref old._nodes[n];
        bool flow = nn.IsFlow;
        bool map = nn.Kind == NodeKind.Mapping;
        int step = map ? 2 : 1;
        if (flow && text.IndexOfAny((byte)'\n', (byte)'\r') >= 0)
        {
            // A line break inside a flow collection depends on the indentation around it.
            return null;
        }

        int ancestors = 0;
        for (int p = nn.Parent; p >= 0; p = old._nodes[p].Parent)
        {
            if ((old._nodes[p].Flags & (NodeFlags.IsKey | NodeFlags.Pair)) != 0)
            {
                return null;
            }

            ancestors++;
        }

        int subDepth = limits.MaxDepth - ancestors;
        if (subDepth <= 0)
        {
            return null;
        }

        // Find the items the edit touches: the one around the first byte of the edit and the one around its last.
        // Text put in front of an item, or in the gap before it, may belong to the item before it, so the region
        // starts there (a value for a bare key goes in front of the next key).
        int end = n + nn.Size;
        int items = nn.Count / step;
        int kFirst;
        int kStart;
        int tNode;
        int regionEnd;
        int oldKids;
        if (items == 0)
        {
            if (!flow)
            {
                return null;
            }

            kFirst = n + 1;
            kStart = nn.Start + 1;
            tNode = n + 1;
            regionEnd = nn.Aux;
            oldKids = 0;
        }
        else
        {
            int xs = old.LastStartingBefore(n + 1, end, s);
            kFirst = xs < 0 ? n + 1 : old.ItemFirst(n, old.ChildContaining(xs, n));
            while (true)
            {
                bool first = kFirst == n + 1;
                kStart = old.ItemStart(n, first ? 0 : 1, kFirst, first ? -1 : old._nodes[old.PrevSibling(kFirst)].End);
                if (s > kStart || first)
                {
                    break;
                }

                kFirst = old.ItemFirst(n, old.PrevSibling(kFirst));
            }

            int xe = old.LastStartingBefore(n + 1, end, e);
            int jFirst = xe < 0 ? n + 1 : old.ItemFirst(n, old.ChildContaining(xe, n));
            if (jFirst < kFirst)
            {
                jFirst = kFirst;
            }

            int jLast = map ? jFirst + old._nodes[jFirst].Size : jFirst;
            tNode = jLast + old._nodes[jLast].Size;
            int jEnd = old._nodes[jLast].End;
            if (e > jEnd && tNode < end)
            {
                // The edit ends in the gap before the next item, which then belongs to the region.
                jFirst = tNode;
                jLast = map ? jFirst + old._nodes[jFirst].Size : jFirst;
                tNode = jLast + old._nodes[jLast].Size;
                jEnd = old._nodes[jLast].End;
                if (e > jEnd)
                {
                    return null;
                }
            }

            // For the last item, the edit may reach past its end: into the blanks and comments after the last
            // entry of a block collection, or the gap before a closing bracket.
            // Only a block scalar at the end can read the comment lines after it as its own, so only then are they all included.
            regionEnd = tNode == end ? (flow ? nn.Aux : TrailingTriviaEnd(src, Math.Max(jEnd, e), EndsInBlockScalar(old, kFirst, tNode, text))) : jEnd;
            oldKids = 0;
            for (int c = kFirst; c < tNode; c += old._nodes[c].Size)
            {
                oldKids++;
            }
        }

        bool touchesEnd = !flow && tNode == end;
        if (kStart > s || regionEnd < e || (items > 0 && kStart > old.PropStart(kFirst)))
        {
            return null;
        }

        // The region's text, read on its own: a block region behind spaces for its column, a flow region
        // between fresh brackets.
        int prefix;
        if (flow)
        {
            prefix = 1;
            int back = kStart;
            while (back > 0 && src[back - 1] is (byte)' ' or (byte)'\t')
            {
                back--;
            }

            if (kFirst != n + 1 && back > 0 && src[back - 1] == (byte)'?')
            {
                return null;
            }
        }
        else
        {
            int lineStart = kStart;
            while (lineStart > 0 && src[lineStart - 1] is not ((byte)'\n' or (byte)'\r'))
            {
                lineStart--;
            }

            prefix = kStart - lineStart;
            if (prefix > MaxSpliceColumn)
            {
                return null;
            }

            foreach (byte b in src.Slice(lineStart, prefix))
            {
                if (b is not ((byte)' ' or (byte)'-' or (byte)'?' or (byte)':'))
                {
                    return null;
                }
            }

            // New lines that start left of this collection (or, for a sequence, at its column without a dash)
            // belong to the collection around it. Saying so here saves a parse that would fail.
            int column = FirstContentColumn(src, s, text, out int contentAt);
            if (column >= 0 && (column < prefix || (!map && column == prefix && !IsDash(text, contentAt))))
            {
                return null;
            }
        }

        // Aliases and anchors in the old region may reach outside it; the whole text decides those.
        for (int i = kFirst; i < tNode; i++)
        {
            ref readonly CstNode o = ref old._nodes[i];
            if (o.Kind == NodeKind.Alias || old.HasAnchor(i))
            {
                return null;
            }
        }

        int subLength = prefix + (s - kStart) + text.Length + (regionEnd - e) + (flow ? 1 : 0);
        byte[] subBuf = ArrayPool<byte>.Shared.Rent(Math.Max(subLength, 1));
        DocState sub = RentScratch(subBuf, subLength, old);
        try
        {
            Span<byte> w = subBuf.AsSpan(0, subLength);
            int at = 0;
            if (flow)
            {
                w[at++] = map ? (byte)'{' : (byte)'[';
            }
            else
            {
                w.Slice(0, prefix).Fill((byte)' ');
                at = prefix;
            }

            src.Slice(kStart, s - kStart).CopyTo(w.Slice(at));
            at += s - kStart;
            text.CopyTo(w.Slice(at));
            at += text.Length;
            src.Slice(e, regionEnd - e).CopyTo(w.Slice(at));
            at += regionEnd - e;
            if (flow)
            {
                w[at++] = map ? (byte)'}' : (byte)']';
            }
            else if (subLength > prefix && IsTriviaByte(w[prefix]))
            {
                // The token before the region would then start somewhere else.
                return null;
            }

            try
            {
                sub.Parse(LimitsForDepth(limits, subDepth));
            }
            catch (YamlException)
            {
                // The full parse says what is wrong, and with the right position.
                return null;
            }

            reparsed = subLength;
            if (!sub.IsSingleRegion(nn.Kind, flow, prefix, subLength))
            {
                return null;
            }

            int newKids = sub._nodes[0].Count;
            bool checkKeys = map && !old.AllowDuplicateKeys && nn.Count - oldKids + newKids > 2 && old.KeysDiffer(sub, kFirst, oldKids, newKids);
            return old.Splice(sub, n, kFirst, tNode, oldKids, newKids, kStart, regionEnd, s, e, text, flow ? kStart - 1 : kStart - prefix, checkKeys, touchesEnd);
        }
        finally
        {
            sub.Dispose();
            s_scratch = sub;
        }
    }

    // Whether the last value of the region, before or after the edit, may be a block scalar. The new text is
    // judged by its indicator characters, which is cheap and errs towards yes. The old nodes are all checked,
    // not only the last: when the edit removes the last items, an earlier block scalar of the region becomes the
    // last value and reads the lines after it as its own.
    private static bool EndsInBlockScalar(DocState old, int firstNode, int endNode, ReadOnlySpan<byte> text)
    {
        if (text.IndexOfAny((byte)'|', (byte)'>') >= 0)
        {
            return true;
        }

        for (int i = firstNode; i < endNode; i++)
        {
            if (old._nodes[i].Kind == NodeKind.Scalar && (ScalarStyle)old._nodes[i].Style is ScalarStyle.Literal or ScalarStyle.Folded)
            {
                return true;
            }
        }

        return false;
    }

    // Past the end of a block collection come the rest of its last line and the blank and comment lines up to
    // the next line with content. A block scalar at the end of the region reads them as its own (a kept one
    // keeps the blank lines, any other reads a more indented comment line as text), so the region must
    // see them too, or it would read the scalar differently from the whole text.
    internal static int TrailingTriviaEnd(ReadOnlySpan<byte> src, int pos, bool followingLines)
    {
        int p = pos;
        if (p > 0 && !IsBreak(src[p - 1]))
        {
            // The rest of the entry's last line: blanks and a comment, then the break.
            int q = SkipBlanks(src, p);
            if (q < src.Length && src[q] == (byte)'#')
            {
                q = LineEndOf(src, q);
            }

            if (q >= src.Length)
            {
                return src.Length;
            }

            if (!IsBreak(src[q]))
            {
                return pos;
            }

            p = AfterBreak(src, q);
        }

        if (!followingLines)
        {
            return p;
        }

        while (p < src.Length)
        {
            int q = SkipBlanks(src, p);
            if (q < src.Length && src[q] == (byte)'#')
            {
                q = LineEndOf(src, q);
            }

            if (q >= src.Length)
            {
                return src.Length;
            }

            if (!IsBreak(src[q]))
            {
                return p;
            }

            p = AfterBreak(src, q);
        }

        return p;
    }

    private static int SkipBlanks(ReadOnlySpan<byte> src, int from)
    {
        int q = from;
        while (q < src.Length && src[q] is (byte)' ' or (byte)'\t')
        {
            q++;
        }

        return q;
    }

    private static int LineEndOf(ReadOnlySpan<byte> src, int from)
    {
        int i = src.Slice(from).IndexOfAny((byte)'\n', (byte)'\r');
        return i < 0 ? src.Length : from + i;
    }

    private static int AfterBreak(ReadOnlySpan<byte> src, int at)
        => at + (src[at] == (byte)'\r' && at + 1 < src.Length && src[at + 1] == (byte)'\n' ? 2 : 1);

    // The region read on its own must be one collection of the kind it replaces, with nothing beyond it,
    // and must bring no anchors or aliases that would reach outside it.
    private bool IsSingleRegion(NodeKind kind, bool flow, int prefix, int length)
    {
        if (DocCount != 1 || NoRealDocument || _docs[0].ExplicitStart || _docs[0].Root != 0 || AliasCount != 0 || NodeCount < 1)
        {
            return false;
        }

        ref readonly CstNode root = ref _nodes[0];
        if (root.Kind != kind || root.IsFlow != flow || (root.Flags & NodeFlags.Pair) != 0 || (root.Count == 0 && !flow))
        {
            return false;
        }

        // A block region may be followed by the blanks and comments that were after the last entry.
        if (root.Start != (flow ? 0 : prefix) || (root.End != length && (flow || SkipTrivia(Text, root.End) != length)))
        {
            return false;
        }

        for (int r = 0; r < PropCountRows; r++)
        {
            if (_props[r * PropWidth] >= 0)
            {
                return false;
            }
        }

        return true;
    }

    // Whether the keys of a mapping's region differ from the keys that replace them.
    private bool KeysDiffer(DocState sub, int first, int oldKids, int newKids)
    {
        if (oldKids != newKids)
        {
            return true;
        }

        int o = first;
        int m = 1;
        ReadOnlySpan<byte> oldText = Text;
        ReadOnlySpan<byte> subText = sub.Text;
        for (int i = 0; i < oldKids / 2; i++)
        {
            ref readonly CstNode a = ref _nodes[o];
            ref readonly CstNode b = ref sub._nodes[m];
            if (a.Kind != b.Kind || a.Style != b.Style || a.Props >= 0 || b.Props >= 0 || a.End - a.Start != b.End - b.Start
                || !oldText.Slice(a.Start, a.End - a.Start).SequenceEqual(subText.Slice(b.Start, b.End - b.Start)))
            {
                return true;
            }

            o += a.Size;
            o += _nodes[o].Size;
            m += b.Size;
            m += sub._nodes[m].Size;
        }

        return false;
    }

    private static void Ensure<T>(ref T[] array, int used, int needed)
    {
        if (array.Length < needed)
        {
            Grow(ref array, used, needed);
        }
    }

    // A copy that has room for the changes about to be made, taken so that the original stays whole.
    private DocState CloneForSplice(int extraBytes, int extraNodes, int extraProps, int extraComments)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(Length + Math.Max(extraBytes, 0) + 16, 1));
        _buf.AsSpan(0, Length).CopyTo(buf);
        var c = new DocState(buf, Length, Encoding, HasByteOrderMark, AllowDuplicateKeys)
        {
            NodeCount = NodeCount,
            DocCount = DocCount,
            NoRealDocument = NoRealDocument,
            CommentCount = CommentCount,
            AliasCount = AliasCount,
            PropCountRows = PropCountRows,
            _directives = _directives,
        };
        try
        {
            c._nodes = ArrayPool<CstNode>.Shared.Rent(Math.Max(NodeCount + Math.Max(extraNodes, 0) + 16, 1));
            _nodes.AsSpan(0, NodeCount).CopyTo(c._nodes);
            c._props = ArrayPool<int>.Shared.Rent(Math.Max(((PropCountRows + extraProps) * PropWidth) + PropWidth, 1));
            _props.AsSpan(0, PropCountRows * PropWidth).CopyTo(c._props);
            c._docs = ArrayPool<DocInfo>.Shared.Rent(Math.Max(DocCount, 1));
            _docs.AsSpan(0, DocCount).CopyTo(c._docs);
            c._comments = ArrayPool<int>.Shared.Rent(Math.Max(((CommentCount + Math.Max(extraComments, 0)) * 2) + 2, 1));
            _comments.AsSpan(0, CommentCount * 2).CopyTo(c._comments);
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private DocState Splice(DocState sub, int n, int first, int end, int oldKids, int newKids, int regionStart, int regionEnd, int s, int e, ReadOnlySpan<byte> text, int baseOffset, bool checkKeys, bool touchesEnd)
    {
        int newCount = sub.NodeCount - 1;
        int d = newCount - (end - first);
        int bd = text.Length - (e - s);
        int subComments = sub.CommentCount;
        DocState t = Exclusive ? this : CloneForSplice(bd, d, sub.PropCountRows, subComments);
        try
        {
            t.ApplySplice(sub, n, first, end, oldKids, newKids, regionStart, regionEnd, s, e, text, baseOffset, d, bd, touchesEnd);
            if (checkKeys)
            {
                t.CheckDuplicateKeys(n);
            }

            return t;
        }
        catch
        {
            if (!ReferenceEquals(t, this))
            {
                t.Dispose();
            }

            throw;
        }
    }

    private void ApplySplice(DocState sub, int n, int first, int end, int oldKids, int newKids, int regionStart, int regionEnd, int s, int e, ReadOnlySpan<byte> text, int baseOffset, int d, int bd, bool touchesEnd)
    {
        // Sizes worked out for the text before the edit no longer fit it.
        ExpansionCache?.Release();
        ExpansionCache = null;
        int oldCount = NodeCount;
        int newCount = sub.NodeCount - 1;
        int propBase = PropCountRows;

        // The collections around the region get a different size, and every offset beyond the region moves.
        int oldEnd = _nodes[n].End;
        int newEnd = sub._nodes[0].End + baseOffset;
        for (int a = n; a >= 0; a = _nodes[a].Parent)
        {
            ref CstNode an = ref _nodes[a];
            an.Size += d;
            if (touchesEnd && an.End == oldEnd)
            {
                // A block collection ends where its last entry does.
                an.End = newEnd;
            }
            else if (an.End >= regionEnd)
            {
                an.End += bd;
            }

            if (an.IsFlow && an.Aux >= regionEnd)
            {
                an.Aux += bd;
            }
        }

        _nodes[n].Count += newKids - oldKids;

        Ensure(ref _nodes, oldCount, oldCount + d);
        if (d != 0)
        {
            Array.Copy(_nodes, end, _nodes, end + d, oldCount - end);
        }

        Span<CstNode> nodes = _nodes.AsSpan(0, oldCount + d);
        for (int i = first + newCount; i < nodes.Length; i++)
        {
            ref CstNode x = ref nodes[i];
            if (x.Parent >= end)
            {
                x.Parent += d;
            }

            if (x.Kind == NodeKind.Alias && x.Aux >= end)
            {
                x.Aux += d;
            }

            // The empty root made up for a document without any node is kept at the end of the array,
            // whichever document it belongs to: only those after the region move.
            if (x.Start < regionEnd)
            {
                continue;
            }

            x.Start += bd;
            x.End += bd;
            if (x.Kind is NodeKind.Mapping or NodeKind.Sequence || (x.Kind == NodeKind.Scalar && (x.Flags & NodeFlags.Empty) != 0))
            {
                x.Aux += bd;
            }
        }

        // The old property rows keep their numbers; offsets beyond the region move.
        Ensure(ref _props, propBase * PropWidth, (propBase + sub.PropCountRows) * PropWidth);
        for (int r = 0; r < propBase; r++)
        {
            int at = r * PropWidth;
            for (int f = 0; f <= 4; f += 2)
            {
                if (_props[at + f] >= regionEnd)
                {
                    _props[at + f] += bd;
                }
            }
        }

        for (int r = 0; r < sub.PropCountRows; r++)
        {
            int from = r * PropWidth;
            int to = (propBase + r) * PropWidth;
            _props[to] = sub._props[from] >= 0 ? sub._props[from] + baseOffset : -1;
            _props[to + 1] = sub._props[from + 1];
            _props[to + 2] = sub._props[from + 2] >= 0 ? sub._props[from + 2] + baseOffset : -1;
            _props[to + 3] = sub._props[from + 3];
            _props[to + 4] = sub._props[from + 4] + baseOffset;
            _props[to + 5] = sub._props[from + 5];
        }

        PropCountRows = propBase + sub.PropCountRows;

        // The new nodes: offsets move from the region's own text into the document's.
        for (int m = 1; m < sub.NodeCount; m++)
        {
            CstNode x = sub._nodes[m];
            x.Parent = x.Parent == 0 ? n : x.Parent + first - 1;
            x.Start += baseOffset;
            x.End += baseOffset;
            if (x.Props >= 0)
            {
                x.Props += propBase;
            }

            if (x.Kind is NodeKind.Mapping or NodeKind.Sequence)
            {
                if (x.IsFlow)
                {
                    x.Aux += baseOffset;
                }
            }
            else if ((x.Flags & NodeFlags.Empty) != 0)
            {
                x.Aux = 0;
            }

            _nodes[first + m - 1] = x;
        }

        NodeCount = oldCount + d;

        // Documents after the region move with their nodes.
        for (int k = 0; k < DocCount; k++)
        {
            ref DocInfo di = ref _docs[k];
            if (di.Root >= end)
            {
                di.Root += d;
            }

            if (di.StartPos >= regionEnd)
            {
                di.StartPos += bd;
            }

            if (di.StartEnd >= regionEnd)
            {
                di.StartEnd += bd;
            }

            if (di.EndPos >= regionEnd)
            {
                di.EndPos += bd;
            }
        }

        // Comments: the ones inside the region are the new ones; later ones move.
        int lo = 0;
        while (lo < CommentCount && _comments[lo * 2] < regionStart)
        {
            lo++;
        }

        int hi = lo;
        while (hi < CommentCount && _comments[hi * 2] < regionEnd)
        {
            hi++;
        }

        int newComments = CommentCount - (hi - lo) + sub.CommentCount;
        Ensure(ref _comments, CommentCount * 2, (newComments * 2) + 2);
        if (newComments != CommentCount)
        {
            Array.Copy(_comments, hi * 2, _comments, (lo + sub.CommentCount) * 2, (CommentCount - hi) * 2);
        }

        for (int c = lo + sub.CommentCount; c < newComments; c++)
        {
            _comments[c * 2] += bd;
            _comments[(c * 2) + 1] += bd;
        }

        for (int c = 0; c < sub.CommentCount; c++)
        {
            _comments[(lo + c) * 2] = sub._comments[c * 2] + baseOffset;
            _comments[((lo + c) * 2) + 1] = sub._comments[(c * 2) + 1] + baseOffset;
        }

        CommentCount = newComments;

        // The cached line break stays right when the edit is on that line and adds no break.
        if (s_breakOwner == _id)
        {
            if (s_breakFrom <= s && e <= s_breakAt && text.IndexOfAny((byte)'\n', (byte)'\r') < 0)
            {
                s_breakAt += bd;
            }
            else
            {
                s_breakOwner = 0;
            }
        }

        // The text.
        int newLength = Length + bd;
        if (_buf.Length < newLength)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(newLength + (newLength >> 3));
            _buf.AsSpan(0, s).CopyTo(bigger);
            _buf.AsSpan(e, Length - e).CopyTo(bigger.AsSpan(s + text.Length));
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = bigger;
        }
        else if (bd != 0)
        {
            _buf.AsSpan(e, Length - e).CopyTo(_buf.AsSpan(s + text.Length));
        }

        text.CopyTo(_buf.AsSpan(s));
        Length = newLength;

        // Where the next token starts after a node can only be told from the text that follows it.
        ReadOnlySpan<byte> all = Text;
        for (int a = n; a >= 0; a = _nodes[a].Parent)
        {
            ref CstNode an = ref _nodes[a];
            if (!an.IsFlow)
            {
                an.Aux = SkipTrivia(all, an.End);
            }
        }

        for (int i = first; i < first + newCount; i++)
        {
            ref CstNode x = ref _nodes[i];
            if ((x.Kind is NodeKind.Mapping or NodeKind.Sequence && !x.IsFlow) || (x.Kind == NodeKind.Scalar && (x.Flags & NodeFlags.Empty) != 0))
            {
                x.Aux = SkipTrivia(all, x.End);
            }
        }
    }
}
