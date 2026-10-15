using System;
using System.Buffers;
using System.Text;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

internal sealed partial class DocState
{
    // ---- Scalar text ----

    /// <summary>The scalar's text between its quotes or after its block header, before unescaping.</summary>
    public ReadOnlySpan<byte> RawContent(int node)
    {
        ref readonly CstNode n = ref _nodes[node];
        ReadOnlySpan<byte> src = Text;
        switch ((ScalarStyle)n.Style)
        {
            case ScalarStyle.SingleQuoted:
            case ScalarStyle.DoubleQuoted:
                return src.Slice(n.Start + 1, n.End - n.Start - 2);
            case ScalarStyle.Literal:
            case ScalarStyle.Folded:
                {
                    int body = BodyStart(src, n.Start);
                    return body >= n.End ? default : src.Slice(body, n.End - body);
                }

            default:
                return src.Slice(n.Start, n.End - n.Start);
        }
    }

    /// <summary>Whether the scalar's raw content is its value.</summary>
    public bool IsRaw(int node) => (_nodes[node].Flags & NodeFlags.Escaped) == 0;

    /// <summary>The scalar's value as UTF-8 bytes, in a buffer the caller must return with <see cref="ReleaseDecoded"/>.</summary>
    /// <param name="node">A scalar node.</param>
    /// <param name="buffer">The rented buffer, or null when the raw content was used.</param>
    /// <returns>The value's bytes.</returns>
    public ReadOnlySpan<byte> DecodedScalar(int node, out byte[]? buffer)
    {
        ReadOnlySpan<byte> raw = RawContent(node);
        buffer = null;
        if (IsRaw(node))
        {
            return raw;
        }

        ref readonly CstNode n = ref _nodes[node];
        buffer = ArrayPool<byte>.Shared.Rent(ScalarDecoder.MaxDecodedLength(raw.Length));
        int len = ScalarDecoder.Decode(raw, (ScalarStyle)n.Style, n.Aux, (Chomping)n.Chomp, buffer);
        return buffer.AsSpan(0, len);
    }

    public static void ReleaseDecoded(byte[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The scalar's value as a string.</summary>
    /// <param name="node">A scalar node.</param>
    /// <returns>The text.</returns>
    public string ScalarString(int node)
    {
        ReadOnlySpan<byte> v = DecodedScalar(node, out byte[]? rented);
        try
        {
            return v.IsEmpty ? string.Empty : Encoding_Utf8.GetString(v);
        }
        finally
        {
            ReleaseDecoded(rented);
        }
    }

    private static readonly UTF8Encoding Encoding_Utf8 = new(false, false);

    /// <summary>What the scalar is under the Core schema: its tag decides, else its style and text.</summary>
    /// <param name="node">A scalar node.</param>
    /// <param name="valid">False when an explicit Core tag does not fit the text (<c>!!int abc</c>).</param>
    /// <returns>The kind.</returns>
    public ScalarKind KindOf(int node, out bool valid)
    {
        valid = true;
        ref readonly CstNode n = ref _nodes[node];
        ReadOnlySpan<byte> tag = RawTag(node);
        if (!tag.IsEmpty)
        {
            CoreTag core = ExplicitCoreTag(node);
            if (core != CoreTag.Unknown)
            {
                ReadOnlySpan<byte> text = DecodedScalar(node, out byte[]? rented);
                try
                {
                    valid = CoreSchema.TryResolveTagged(core, text, out ScalarKind forced);
                    return valid ? forced : ScalarKind.String;
                }
                finally
                {
                    ReleaseDecoded(rented);
                }
            }

            // An unknown tag keeps the text; the non-specific tag "!" means a string too.
            return ScalarKind.String;
        }

        if ((ScalarStyle)n.Style != ScalarStyle.Plain)
        {
            return ScalarKind.String;
        }

        if ((n.Flags & NodeFlags.Empty) != 0)
        {
            return ScalarKind.Null;
        }

        if (IsRaw(node))
        {
            return CoreSchema.ResolvePlain(RawContent(node));
        }

        ReadOnlySpan<byte> folded = DecodedScalar(node, out byte[]? buffer);
        try
        {
            return CoreSchema.ResolvePlain(folded);
        }
        finally
        {
            ReleaseDecoded(buffer);
        }
    }

    /// <summary>The Core tag a node was given explicitly, or <see cref="CoreTag.Unknown"/>.</summary>
    /// <param name="node">Any node.</param>
    /// <returns>The tag.</returns>
    public CoreTag ExplicitCoreTag(int node)
    {
        return TagClassOf(node) switch
        {
            TagClass.Str => CoreTag.Str,
            TagClass.Int => CoreTag.Int,
            TagClass.Float => CoreTag.Float,
            TagClass.Bool => CoreTag.Bool,
            TagClass.Null => CoreTag.Null,
            _ => CoreTag.Unknown,
        };
    }

    /// <summary>Follows an alias to the node it names. Other nodes are returned as they are.</summary>
    /// <param name="node">Any node.</param>
    /// <returns>The node to read.</returns>
    public int Resolve(int node) => _nodes[node].Kind == NodeKind.Alias ? _nodes[node].Aux : node;

    // ---- Tree navigation ----

    /// <summary>The first child of a collection.</summary>
    public static int FirstChild(int node) => node + 1;

    /// <summary>The node after a node's subtree.</summary>
    public int After(int node) => node + _nodes[node].Size;

    /// <summary>Finds the child at a position.</summary>
    /// <param name="collection">A collection.</param>
    /// <param name="index">The child's position among the children.</param>
    /// <returns>The child's node index.</returns>
    public int ChildAt(int collection, int index)
    {
        int c = collection + 1;
        for (int i = 0; i < index; i++)
        {
            c += _nodes[c].Size;
        }

        return c;
    }

    // ---- Geometry over the text ----

    public static bool IsBreak(byte b) => b is (byte)'\n' or (byte)'\r';

    /// <summary>Whether a node is the root of a document with no text at all, which a first write may give a kind.</summary>
    /// <param name="node">A node.</param>
    /// <returns><see langword="true"/> for such a root.</returns>
    public bool IsEmptyRoot(int node)
        => _nodes[node].Parent < 0 && (_nodes[node].Flags & NodeFlags.Empty) != 0 && _nodes[node].Kind == NodeKind.Scalar && !HasTag(node) && !HasAnchor(node);

    /// <summary>The offset where the line holding <paramref name="pos"/> starts.</summary>
    public int LineStart(int pos)
    {
        ReadOnlySpan<byte> src = Text;
        int i = pos;
        while (i > 0 && !IsBreak(src[i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>The offset of the line break at or after <paramref name="pos"/>, or the text length.</summary>
    public int LineBreakAt(int pos)
    {
        int i = Text.Slice(pos).IndexOfAny((byte)'\n', (byte)'\r');
        return i < 0 ? Length : pos + i;
    }

    /// <summary>The offset just after the line break at <paramref name="pos"/> (CRLF counts as one).</summary>
    public int PastBreak(int pos)
    {
        ReadOnlySpan<byte> src = Text;
        if (pos >= src.Length)
        {
            return pos;
        }

        return pos + (src[pos] == (byte)'\r' && pos + 1 < src.Length && src[pos + 1] == (byte)'\n' ? 2 : 1);
    }

    /// <summary>The column of an offset, in bytes from the start of its line.</summary>
    public int Column(int pos) => pos - LineStart(pos);

    /// <summary>Whether only spaces lie between the start of the line and the offset.</summary>
    public bool StartsLine(int pos)
    {
        ReadOnlySpan<byte> src = Text;
        for (int i = pos - 1; i >= 0 && !IsBreak(src[i]); i--)
        {
            if (src[i] != (byte)' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a comment (as recorded by the reader) starts at the offset.</summary>
    public bool IsCommentAt(int pos)
    {
        int lo = 0;
        int hi = CommentCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int s = _comments[mid * 2];
            if (s == pos)
            {
                return true;
            }

            if (s < pos)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// The offset after the last line of an entry that ends at <paramref name="end"/>: the rest of
    /// its line (a same-line comment included) and the line break. At the end of the text, the length.
    /// </summary>
    /// <param name="end">The end of the entry's text.</param>
    /// <returns>The start of the next line.</returns>
    public int PastLine(int end)
    {
        ReadOnlySpan<byte> src = Text;
        if (end > 0 && end <= src.Length && IsBreak(src[end - 1]) && end > 0)
        {
            return end;
        }

        int b = LineBreakAt(end);
        return b >= Length ? Length : PastBreak(b);
    }

    /// <summary>The line break used near an offset: the one ending its line, else the first in the text, else LF.</summary>
    /// <param name="pos">An offset.</param>
    /// <returns>The break as text.</returns>
    public string BreakNear(int pos)
    {
        ReadOnlySpan<byte> src = Text;
        int b = LineBreakAt(Math.Min(pos, Length));
        if (b < src.Length)
        {
            return BreakText(b);
        }

        int first = src.IndexOfAny((byte)'\n', (byte)'\r');
        return first < 0 ? "\n" : BreakText(first);
    }

    private string BreakText(int at)
    {
        ReadOnlySpan<byte> src = Text;
        if (src[at] == (byte)'\n')
        {
            return "\n";
        }

        return at + 1 < src.Length && src[at + 1] == (byte)'\n' ? "\r\n" : "\r";
    }

    /// <summary>
    /// The start of the first comment-only line of the block of comment lines that sits directly
    /// above the line starting at <paramref name="lineStart"/>, or <paramref name="lineStart"/> itself.
    /// </summary>
    /// <param name="lineStart">The start of an entry's first line.</param>
    /// <returns>Where the entry's own lines begin, comments above included.</returns>
    public int OwnedCommentsStart(int lineStart)
    {
        ReadOnlySpan<byte> src = Text;
        int start = lineStart;
        while (start > 0)
        {
            int prevEnd = start - 1;
            if (src[prevEnd] == (byte)'\n' && prevEnd > 0 && src[prevEnd - 1] == (byte)'\r')
            {
                prevEnd--;
            }

            int prevStart = prevEnd;
            while (prevStart > 0 && !IsBreak(src[prevStart - 1]))
            {
                prevStart--;
            }

            int first = prevStart;
            while (first < prevEnd && src[first] == (byte)' ')
            {
                first++;
            }

            if (first < prevEnd && src[first] == (byte)'#' && IsCommentAt(first))
            {
                start = prevStart;
            }
            else
            {
                break;
            }
        }

        return start;
    }

    /// <summary>The offset just after the colon that follows a mapping key.</summary>
    /// <param name="key">A key node.</param>
    /// <returns>The offset, or the key's end when no colon follows.</returns>
    public int ColonEnd(int key)
    {
        int at = SkipTrivia(Text, _nodes[key].End);
        return at < Length && Text[at] == (byte)':' ? at + 1 : _nodes[key].End;
    }

    /// <summary>The offset of the <c>-</c> that introduces a sequence item.</summary>
    /// <param name="item">An item node of a block sequence.</param>
    /// <param name="previous">The previous item, or -1.</param>
    /// <returns>The dash's offset.</returns>
    public int DashOf(int item, int previous)
    {
        int parent = _nodes[item].Parent;
        int from = previous >= 0 ? _nodes[previous].End : _nodes[parent].Start;
        return SkipTrivia(Text, from);
    }

    /// <summary>The first offset of an entry with this key, taking an explicit-key mark into account.</summary>
    /// <param name="key">A key node.</param>
    /// <returns>The entry's first byte.</returns>
    public int EntryStart(int key)
    {
        ReadOnlySpan<byte> src = Text;
        int prop = PropStart(key);
        int prev = PrevSibling(key);
        int parent = _nodes[key].Parent;
        int from = prev >= 0 ? _nodes[prev].End : (parent >= 0 ? _nodes[parent].Start : prop);
        int at = SkipTrivia(src, from);
        if (at < prop && at < src.Length && src[at] == (byte)'?' && (at + 1 >= src.Length || src[at + 1] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
        {
            return at;
        }

        return prop;
    }

    /// <summary>Computes the 1-based line and column (in characters) and the byte offset of a position.</summary>
    /// <param name="offset">An offset into the text.</param>
    /// <returns>The position.</returns>
    public YamlPosition PositionOf(int offset)
    {
        ReadOnlySpan<byte> src = Text;
        offset = Math.Clamp(offset, 0, src.Length);
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            byte b = src[i];
            if (b == (byte)'\n' || (b == (byte)'\r' && !(i + 1 < src.Length && src[i + 1] == (byte)'\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = 1;
        for (int i = lineStart; i < offset; i++)
        {
            if ((src[i] & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new YamlPosition(line, column, offset);
    }
}
