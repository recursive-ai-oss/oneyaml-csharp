using System;
using System.Buffers;
using OneYaml.Emitting;
using OneYaml.Parsing;
using OneYaml.Text;

namespace OneYaml.Documents;

/// <summary>
/// The text of a YAML stream and its lossless tree. A state is not changed once it is built: an edit
/// builds the next state from the bytes it wants, so a failed edit leaves this one intact. The one
/// exception is a state a patch has marked <see cref="Exclusive"/>: the patch made it, nobody else can
/// see it yet, and its next operation edits it in place.
/// The arrays come from the shared pool and go back on <see cref="Dispose"/>.
/// </summary>
internal sealed partial class DocState : IDisposable
{
    private const int PropWidth = 6;

    private byte[] _buf;

    // The integer digit limit of the parse that built this state; duplicate-key checks refuse longer integer keys.
    private int _maxIntegerDigits = YamlLimits.Default.MaxIntegerDigits;

    internal int MaxIntegerDigits => _maxIntegerDigits;
    private CstNode[] _nodes;
    private int[] _props;
    private DocInfo[] _docs;
    private int[] _comments;

    private DocState(byte[] buf, int length, YamlEncoding encoding, bool hasBom, bool allowDuplicateKeys)
    {
        AllowDuplicateKeys = allowDuplicateKeys;
        _buf = buf;
        Length = length;
        Encoding = encoding;
        HasByteOrderMark = hasBom;
        _nodes = [];
        _props = [];
        _docs = [];
        _comments = [];
    }

    /// <summary>Where the edits made on the way to the next state are noted, when views of the document exist.</summary>
    public EditTrace? Trace { get; set; }

    /// <summary>How strictly plain text the edit engine writes is checked.</summary>
    internal StyleMode EditMode { get; private set; } = StyleMode.Edit;

    /// <summary>Whether a mapping may hold the same key twice.</summary>
    public bool AllowDuplicateKeys { get; private set; }

    /// <summary>The number of bytes of text.</summary>
    public int Length { get; private set; }

    /// <summary>The encoding the stream arrived in, written back on output.</summary>
    public YamlEncoding Encoding { get; private set; }

    /// <summary>Whether the stream began with a byte order mark.</summary>
    public bool HasByteOrderMark { get; private set; }

    public int NodeCount { get; private set; }

    /// <summary>The length of the node array, which is what the state holds on to for nodes.</summary>
    internal int NodeCapacity => _nodes.Length;

    public int DocCount { get; private set; }

    /// <summary>Whether the stream holds no document and <see cref="DocCount"/> counts the empty one made up for edits.</summary>
    public bool NoRealDocument { get; private set; }

    public int CommentCount { get; private set; }

    /// <summary>Whether any alias occurs in the stream.</summary>
    public int AliasCount { get; private set; }

    public ReadOnlySpan<byte> Text => _buf.AsSpan(0, Length);

    public ReadOnlySpan<CstNode> Nodes => NodeSpan(0, NodeCount);

    /// <summary>
    /// The node at an index, read as it is. For code that only runs on states readers share, where no move of offsets is
    /// held back; the edit engine reads through <see cref="NodeH"/>.
    /// </summary>
    public ref readonly CstNode Node(int index)
    {
        AssertNotHeld();
        return ref _nodes[index];
    }

    /// <summary>
    /// The node at an index, for reading what an edit never holds back: kind, flags, style, size, count, parent and
    /// properties. Its offsets may be behind.
    /// </summary>
    public ref readonly CstNode NodeS(int index) => ref _nodes[index];

    /// <summary>The node at an index with true offsets, for the edit engine, which may meet a state with moves held back.</summary>
    public ref readonly CstNode NodeH(int index) => ref N(index);

    public ref readonly DocInfo Doc(int index) => ref _docs[index];

    public (int Start, int End) Comment(int index) => (_comments[index * 2], _comments[(index * 2) + 1]);

    public bool HasAnchor(int node) => _nodes[node].Props >= 0 && _props[(_nodes[node].Props * PropWidth)] >= 0;

    public bool HasTag(int node) => _nodes[node].Props >= 0 && _props[(_nodes[node].Props * PropWidth) + 2] >= 0;

    /// <summary>What the node's tag means once its handle is resolved, <see cref="TagClass.None"/> when it has no tag.</summary>
    public TagClass TagClassOf(int node)
    {
        int p = _nodes[node].Props;
        return p < 0 || _props[(p * PropWidth) + 2] < 0 ? TagClass.None : (TagClass)_props[(p * PropWidth) + 5];
    }

    /// <summary>The anchor name (without <c>&amp;</c>) of a node, empty when it has none.</summary>
    public ReadOnlySpan<byte> AnchorName(int node)
    {
        int p = _nodes[node].Props;
        if (p < 0 || _props[p * PropWidth] < 0)
        {
            return default;
        }

        return Text.Slice(_props[p * PropWidth], _props[(p * PropWidth) + 1]);
    }

    /// <summary>The tag as written (including the leading <c>!</c>), empty when the node has none.</summary>
    public ReadOnlySpan<byte> RawTag(int node)
    {
        int p = _nodes[node].Props;
        if (p < 0 || _props[(p * PropWidth) + 2] < 0)
        {
            return default;
        }

        return Text.Slice(_props[(p * PropWidth) + 2], _props[(p * PropWidth) + 3]);
    }

    /// <summary>Where the node's tag, as written, starts; the node must have one.</summary>
    public int TagStartOf(int node) => _props[(_nodes[node].Props * PropWidth) + 2];

    /// <summary>Says where a node's tag is written and where its prefix and suffix are, without building the full tag.</summary>
    /// <param name="node">A node.</param>
    /// <param name="start">Where the tag is written.</param>
    /// <param name="length">How long it is as written.</param>
    /// <param name="prefixStart">The prefix offset in the text, or -1 for <c>!</c> and -2 for <c>tag:yaml.org,2002:</c>.</param>
    /// <param name="prefixLength">The prefix length, zero for the built-in ones.</param>
    /// <param name="suffixStart">Where the suffix starts.</param>
    /// <param name="suffixLength">How long the suffix is.</param>
    /// <returns><see langword="false"/> when the node has no tag.</returns>
    public bool TryTagParts(int node, out int start, out int length, out int prefixStart, out int prefixLength, out int suffixStart, out int suffixLength)
    {
        start = length = prefixStart = prefixLength = suffixStart = suffixLength = 0;
        int p = _nodes[node].Props;
        if (p < 0 || _props[(p * PropWidth) + 2] < 0)
        {
            return false;
        }

        start = _props[(p * PropWidth) + 2];
        length = _props[(p * PropWidth) + 3];
        ReadOnlySpan<byte> raw = Text.Slice(start, length);
        if (raw.Length >= 3 && raw[1] == (byte)'<')
        {
            suffixStart = start + 2;
            suffixLength = raw.Length - 3;
            return true;
        }

        int bang = raw.Length > 1 ? raw[1..].IndexOf((byte)'!') : -1;
        int handleLength = bang < 0 ? 1 : bang + 2;
        suffixStart = start + handleLength;
        suffixLength = raw.Length - handleLength;
        prefixStart = handleLength == 2 && raw[1] == (byte)'!' ? -2 : handleLength == 1 ? -1 : 0;
        if (HasDirectives)
        {
            FindDirectivePrefix(node, raw[..handleLength], ref prefixStart, ref prefixLength);
        }

        return true;
    }

    // Looks for the document's %TAG directive naming the handle; the directives sit between the previous
    // document and the "---" that starts this one.
    private void FindDirectivePrefix(int node, ReadOnlySpan<byte> handle, ref int prefixStart, ref int prefixLength)
    {
        int d = 0;
        while (d + 1 < DocCount && _docs[d + 1].Root <= node)
        {
            d++;
        }

        if (!_docs[d].ExplicitStart)
        {
            return;
        }

        ReadOnlySpan<byte> src = Text;
        int from = d == 0 ? 0 : _docs[d - 1].EndPos;
        int pos = from;
        int end = from + Math.Max(0, _docs[d].StartPos - from);
        while (pos < end)
        {
            ReadOnlySpan<byte> rest0 = src[pos..end];
            int eol = rest0.IndexOf((byte)'\n');
            int lineLength = eol < 0 ? rest0.Length : eol;
            ReadOnlySpan<byte> line = rest0[..lineLength];
            int lineStart = pos;
            pos += eol < 0 ? rest0.Length : eol + 1;
            if (!line.StartsWith("%TAG"u8))
            {
                continue;
            }

            ReadOnlySpan<byte> rest = line[4..].TrimStart(" \t"u8);
            int h = rest.IndexOfAny((byte)' ', (byte)'\t');
            if (h <= 0 || !rest[..h].SequenceEqual(handle))
            {
                continue;
            }

            ReadOnlySpan<byte> after = rest[h..].TrimStart(" \t"u8);
            int e = after.IndexOfAny((byte)' ', (byte)'\t', (byte)'\r');
            prefixLength = e < 0 ? after.Length : e;
            prefixStart = lineStart + (line.Length - after.Length);
            return;
        }
    }

    /// <summary>Where the node's first property starts, or its own start when it has none.</summary>
    public int PropStart(int node)
    {
        int p = _nodes[node].Props;
        return p < 0 ? N(node).Start : _props[(p * PropWidth) + 4];
    }

    /// <summary>Just after the node's last property, or its start when it has none.</summary>
    public int PropEnd(int node)
    {
        int p = _nodes[node].Props;
        if (p < 0)
        {
            return N(node).Start;
        }

        int end = 0;
        int row = p * PropWidth;
        if (_props[row] >= 0)
        {
            end = _props[row] + _props[row + 1];
        }

        if (_props[row + 2] >= 0)
        {
            end = Math.Max(end, _props[row + 2] + _props[row + 3]);
        }

        return end;
    }

    /// <summary>The expansion sizes a read through aliases worked out for this state, returned to the pool on <see cref="Dispose"/>.</summary>
    internal NodeReplay.ExpansionSizes? ExpansionCache
    {
        get => _cache?.Expansion;
        set
        {
            // Both live in one object that exists only once either is needed, so a state that is only read carries a single reference.
            if (value is not null || _cache is not null)
            {
                EnsureCache().Expansion = value;
            }
        }
    }

    public void Dispose()
    {
        ExpansionCache?.Release();
        ExpansionCache = null;
        ReleaseCache();
        Return(ref _buf);
        _held = false;
        Return(ref _nodes);
        Return(ref _props);
        Return(ref _docs);
        Return(ref _comments);
        NodeCount = 0;
    }

    private static void Return<T>(ref T[] array)
    {
        T[] a = array;
        array = [];
        if (a.Length != 0)
        {
            ArrayPool<T>.Shared.Return(a);
        }
    }

    /// <summary>
    /// Copies the text into a pooled buffer and builds the tree.
    /// </summary>
    /// <param name="utf8">Validated UTF-8 text without a byte order mark.</param>
    /// <param name="encoding">The encoding the stream was read in.</param>
    /// <param name="hasBom">Whether the stream had a byte order mark.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="allowDuplicateKeys">Whether a mapping may hold the same key twice.</param>
    /// <param name="quoteYaml11Lookalikes">Whether new plain text that a YAML 1.1 reader would misread is quoted.</param>
    /// <returns>The new state.</returns>
    public static DocState Build(ReadOnlySpan<byte> utf8, YamlEncoding encoding, bool hasBom, YamlLimits limits, bool allowDuplicateKeys, bool quoteYaml11Lookalikes = false)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(utf8.Length, 1));
        utf8.CopyTo(buf);
        var state = new DocState(buf, utf8.Length, encoding, hasBom, allowDuplicateKeys) { EditMode = quoteYaml11Lookalikes ? StyleMode.EditQuoting : StyleMode.Edit };
        try
        {
            state.Parse(limits);
            return state;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    private static void Grow<T>(ref T[] array, int used, int minimum)
    {
        int size = Math.Max(Math.Max(array.Length * 2, minimum), 16);
        T[] bigger = ArrayPool<T>.Shared.Rent(size);
        if (used > 0)
        {
            Array.Copy(array, bigger, used);
        }

        if (array.Length != 0)
        {
            ArrayPool<T>.Shared.Return(array);
        }

        array = bigger;
    }

    private const int MaxFirstNodeGuess = 1 << 20;
    private const int MinTrimmedNodeArray = 1 << 14;

    private int AddNode()
    {
        if (NodeCount == _nodes.Length)
        {
            Grow(ref _nodes, NodeCount, NodeCount + 1);
        }

        _nodes[NodeCount] = default;
        return NodeCount++;
    }

    private void Parse(YamlLimits limits)
    {
        _maxIntegerDigits = limits.MaxIntegerDigits;
        ReadOnlySpan<byte> src = Text;
        // Guess the node count from the text so the array rarely has to grow, and so a text of comments or
        // long scalars does not hold storage it will never fill (a document keeps this array for as long as
        // the caller holds it). Lines, colons and commas track the nodes closely: a block entry is a line,
        // a mapping entry is a key and a value, a flow entry is a comma. Those bytes also occur inside scalars,
        // where they say nothing about the nodes: commas only count when the text has a flow collection at all,
        // and a third of the text size bounds the guess (a flow entry of one character and a separator is the
        // densest thing that is common). A text of denser nodes makes the array grow once or twice. The cap keeps
        // a huge text of newlines from reserving more than a node array of a million, and whatever is left over
        // when the parse ends is trimmed.
        int estimate = src.Count((byte)'\n') + src.Count((byte)':');
        if (src.IndexOfAny((byte)'[', (byte)'{') >= 0)
        {
            estimate += 2 * src.Count((byte)',');
        }

        estimate = Math.Min(estimate, src.Length / 3);
        _nodes = ArrayPool<CstNode>.Shared.Rent(Math.Clamp(estimate + 16, 16, MaxFirstNodeGuess));
        _docs = ArrayPool<DocInfo>.Shared.Rent(4);
        var anchors = new AnchorMap();
        int[] openNode = ArrayPool<int>.Shared.Rent(32);
        int[] openLast = ArrayPool<int>.Shared.Rent(32);
        int open = 0;
        bool inDocument = false;
        YamlReader.Scratch scratch = default;
        var reader = new YamlReader(src, new YamlReaderOptions { Limits = limits, RecordComments = true }, ref scratch);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case YamlTokenType.DocumentStart:
                        {
                            if (DocCount == _docs.Length)
                            {
                                Grow(ref _docs, DocCount, DocCount + 1);
                            }

                            _docs[DocCount] = new DocInfo
                            {
                                Root = -1,
                                StartPos = reader.Start,
                                StartEnd = reader.End,
                                ExplicitStart = !reader.IsImplicit,
                            };
                            DocCount++;
                            anchors.Clear();
                            inDocument = true;
                            break;
                        }

                    case YamlTokenType.DocumentEnd:
                        _docs[DocCount - 1].EndPos = reader.Start;
                        inDocument = false;
                        break;

                    case YamlTokenType.MappingStart:
                    case YamlTokenType.SequenceStart:
                    case YamlTokenType.Scalar:
                    case YamlTokenType.Alias:
                        {
                            if (!inDocument)
                            {
                                break;
                            }

                            int parent = open > 0 ? openNode[open - 1] : -1;
                            int prev = open > 0 ? openLast[open - 1] : -1;
                            int idx = AddNode();
                            if (parent < 0)
                            {
                                _docs[DocCount - 1].Root = idx;
                            }
                            else
                            {
                                openLast[open - 1] = idx;
                                _nodes[parent].Count++;
                            }

                            ref CstNode n = ref _nodes[idx];
                            n.Parent = parent;
                            n.Props = -1;
                            n.Size = 1;
                            n.Start = reader.Start;
                            n.End = reader.End;
                            bool isKey = parent >= 0 && _nodes[parent].Kind == NodeKind.Mapping && (_nodes[parent].Count & 1) == 1;
                            if (isKey)
                            {
                                n.Flags |= NodeFlags.IsKey;
                            }

                            if (reader.TokenType == YamlTokenType.Alias)
                            {
                                n.Kind = NodeKind.Alias;
                                int target = anchors.Find(src, reader.Anchor, out bool inside);
                                if (target < 0)
                                {
                                    throw YamlException.At(YamlErrorCategory.Syntax, "The alias refers to an anchor that is not defined.", src, reader.Start);
                                }

                                if (inside)
                                {
                                    throw YamlException.At(YamlErrorCategory.Syntax, "The alias refers to the node that contains it, a cycle that cannot be read.", src, reader.Start);
                                }

                                n.Aux = target;
                                _nodes[target].Flags |= NodeFlags.HasAliases;
                                AliasCount++;
                                break;
                            }

                            if (reader.HasAnchor || reader.HasTag)
                            {
                                n.Props = AddProps(ref reader, src);
                                if (reader.HasAnchor && reader.TokenType == YamlTokenType.Scalar)
                                {
                                    anchors.Set(src, reader.AnchorStart, reader.Anchor.Length, idx);
                                }
                            }

                            if (reader.TokenType == YamlTokenType.Scalar)
                            {
                                n.Kind = NodeKind.Scalar;
                                n.Style = (byte)reader.ScalarStyle;
                                if (reader.ValueIsEscapedOrFolded)
                                {
                                    n.Flags |= NodeFlags.Escaped;
                                }

                                if (reader.ScalarStyle is ScalarStyle.Literal or ScalarStyle.Folded)
                                {
                                    n.Aux = reader.BlockIndent;
                                    n.Chomp = (byte)reader.BlockChomping;
                                    if (reader.BlockChomping != Chomping.Keep)
                                    {
                                        n.End = TrimBlankLines(src, n.Start, n.End, n.Aux);
                                    }
                                }
                                else if (reader.ScalarStyle == ScalarStyle.Plain && reader.End == reader.Start)
                                {
                                    n.Flags |= NodeFlags.Empty;
                                    n.Aux = reader.Start;
                                    PlaceEmpty(src, idx, parent, prev, isKey);
                                }
                            }
                            else
                            {
                                n.Kind = reader.TokenType == YamlTokenType.MappingStart ? NodeKind.Mapping : NodeKind.Sequence;
                                if (reader.CollectionStyle == CollectionStyle.Flow)
                                {
                                    n.Flags |= NodeFlags.Flow;
                                    if (reader.End == reader.Start)
                                    {
                                        // A flow mapping with no braces: the pair inside a flow sequence.
                                        n.Flags |= NodeFlags.Pair;
                                    }
                                }

                                if (open == openNode.Length)
                                {
                                    Grow(ref openNode, open, open + 1);
                                    Grow(ref openLast, open, open + 1);
                                }

                                openNode[open] = idx;
                                openLast[open] = -1;
                                open++;
                                if (reader.HasAnchor)
                                {
                                    // The name means this node from here on, so an alias inside it names the node that holds it:
                                    // a cycle, which is refused, never an older definition of the same name.
                                    anchors.Set(src, reader.AnchorStart, reader.Anchor.Length, idx, open: true);
                                }
                            }

                            break;
                        }

                    case YamlTokenType.MappingEnd:
                    case YamlTokenType.SequenceEnd:
                        {
                            if (!inDocument)
                            {
                                break;
                            }

                            open--;
                            int idx = openNode[open];
                            ref CstNode n = ref _nodes[idx];
                            n.Size = NodeCount - idx;
                            n.Aux = reader.Start;
                            if (n.IsFlow && (n.Flags & NodeFlags.Pair) == 0)
                            {
                                n.End = reader.End;
                            }
                            else
                            {
                                int last = openLast[open];
                                n.End = last >= 0 ? _nodes[last].End : n.Start;
                            }

                            if (HasAnchor(idx))
                            {
                                int row = n.Props * PropWidth;
                                anchors.Close(src, _props[row], _props[row + 1], idx);
                            }

                            if (!AllowDuplicateKeys && n.Kind == NodeKind.Mapping && n.Count > 2)
                            {
                                CheckDuplicateKeys(idx);
                            }

                            break;
                        }
                }
            }

            if (DocCount == 0)
            {
                // An empty stream, or one that holds only comments, has no document. One empty document is
                // made up so that a write has somewhere to go; the public list hides it.
                _docs[0] = new DocInfo { Root = -1, StartPos = src.Length, StartEnd = src.Length, EndPos = src.Length };
                DocCount = 1;
                NoRealDocument = true;
            }

            for (int d = 0; d < DocCount; d++)
            {
                if (_docs[d].Root < 0)
                {
                    int at = AddNode();
                    ref CstNode e = ref _nodes[at];
                    e.Parent = -1;
                    e.Props = -1;
                    e.Size = 1;
                    e.Flags = NodeFlags.Empty;
                    e.Start = e.End = _docs[d].StartEnd;
                    e.Aux = NoRealDocument ? 0 : _docs[d].StartEnd;
                    _docs[d].Root = at;
                }
            }

            CommentCount = reader.CommentCount;
            if (CommentCount > 0)
            {
                _comments = ArrayPool<int>.Shared.Rent(CommentCount * 2);
                for (int i = 0; i < CommentCount; i++)
                {
                    (_comments[i * 2], _comments[(i * 2) + 1]) = reader.GetComment(i);
                }
            }

            if (_nodes.Length >= MinTrimmedNodeArray && _nodes.Length > 4 * NodeCount)
            {
                // The guess was far too high for this text. Keep only what the nodes need. Small arrays stay as they
                // are: the pool keeps the one given back, and a second one would cost more than the slack.
                CstNode[] fitted = ArrayPool<CstNode>.Shared.Rent(NodeCount);
                if (fitted.Length < _nodes.Length)
                {
                    Array.Copy(_nodes, fitted, NodeCount);
                    ArrayPool<CstNode>.Shared.Return(_nodes);
                    _nodes = fitted;
                }
                else
                {
                    ArrayPool<CstNode>.Shared.Return(fitted);
                }
            }
        }
        finally
        {
            reader.Dispose();
            anchors.Dispose();
            ArrayPool<int>.Shared.Return(openNode);
            ArrayPool<int>.Shared.Return(openLast);
        }
    }

    // A block scalar the reader ends after its blank lines. They are not part of the value unless
    // the chomping keeps them, and a later edit of the value must not swallow them.
    internal static int TrimBlankLines(ReadOnlySpan<byte> src, int start, int end, int indent)
    {
        int bodyStart = BodyStart(src, start);
        int e = end;
        while (e > bodyStart)
        {
            int t = e;
            if (src[t - 1] == (byte)'\n')
            {
                t--;
                if (t > bodyStart && src[t - 1] == (byte)'\r')
                {
                    t--;
                }
            }
            else if (src[t - 1] == (byte)'\r')
            {
                t--;
            }

            int lineStart = t;
            while (lineStart > bodyStart && src[lineStart - 1] is not ((byte)'\n' or (byte)'\r'))
            {
                lineStart--;
            }

            if (t - lineStart > indent || src.Slice(lineStart, t - lineStart).IndexOfAnyExcept((byte)' ') >= 0)
            {
                break;
            }

            e = lineStart;
        }

        return e;
    }

    /// <summary>The offset of the first line of a block scalar's content, after the header line.</summary>
    /// <param name="src">The text.</param>
    /// <param name="start">The offset of the <c>|</c> or <c>&gt;</c>.</param>
    /// <returns>The offset just after the header's line break.</returns>
    public static int BodyStart(ReadOnlySpan<byte> src, int start)
    {
        int i = src.Slice(start).IndexOfAny((byte)'\n', (byte)'\r');
        if (i < 0)
        {
            return src.Length;
        }

        i += start;
        return i + (src[i] == (byte)'\r' && i + 1 < src.Length && src[i + 1] == (byte)'\n' ? 2 : 1);
    }

    private int AddProps(ref YamlReader reader, ReadOnlySpan<byte> src)
    {
        int row = PropCountRows;
        if ((row + 1) * PropWidth > _props.Length)
        {
            Grow(ref _props, row * PropWidth, (row + 1) * PropWidth);
        }

        int a = reader.HasAnchor ? reader.AnchorStart : -1;
        int aLen = reader.HasAnchor ? reader.Anchor.Length : 0;
        int t = reader.HasTag ? reader.TagStart : -1;
        int tagClass = reader.HasTag ? (int)reader.GetTagClass() : 0;
        int tLen = reader.HasTag ? reader.RawTag.Length : 0;
        int first = int.MaxValue;
        if (a >= 0)
        {
            first = a - 1;
        }

        if (t >= 0)
        {
            first = Math.Min(first, t);
        }

        int at = row * PropWidth;
        _props[at] = a;
        _props[at + 1] = aLen;
        _props[at + 2] = t;
        _props[at + 3] = tLen;
        _props[at + 4] = first;
        _props[at + 5] = tagClass;
        PropCountRows++;
        _ = src;
        return row;
    }

    private int PropCountRows { get; set; }

    // The reader reports an omitted node where the next token starts, which is often a later line.
    // For the edit engine the useful place is right after the indicator that introduces it.
    private void PlaceEmpty(ReadOnlySpan<byte> src, int idx, int parent, int prev, bool isKey)
    {
        ref CstNode n = ref _nodes[idx];
        if (n.Props >= 0)
        {
            n.Start = n.End = PropEnd(idx);
            return;
        }

        if (isKey)
        {
            // The reader reports the key where the next token starts. Right after the "?" is where the
            // key is, so text added for it cannot land in the indentation of the line below.
            int q = n.Start;
            while (parent >= 0 && !_nodes[parent].IsFlow && q > 0 && src[q - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                q--;
            }

            // A "?" that sits before the end of the entry before belongs to that entry's last key (an empty key
            // at the end of a nested mapping), not to this one.
            int floor = parent < 0 ? 0 : prev >= 0 ? _nodes[prev].End : _nodes[parent].Start;
            if (q > floor && src[q - 1] == (byte)'?' && (q == 1 || src[q - 2] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'-'))
            {
                n.Start = n.End = q;
            }
            else if (parent >= 0 && !_nodes[parent].IsFlow)
            {
                // A comment after the "?" hides it from the walk back: look for it forward from the entry's start.
                int at = SkipTrivia(src, prev >= 0 ? _nodes[prev].End : _nodes[parent].Start);
                if (at < n.Start && src[at] == (byte)'?' && (at + 1 >= src.Length || src[at + 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                {
                    n.Start = n.End = at + 1;
                }
            }

            return;
        }

        if (parent < 0)
        {
            // An empty document goes right after its start marker when it has one.
            if (DocCount > 0 && _docs[DocCount - 1].ExplicitStart)
            {
                n.Start = n.End = _docs[DocCount - 1].StartEnd;
            }

            return;
        }

        ref CstNode p = ref _nodes[parent];
        if (p.Kind == NodeKind.Mapping && prev >= 0)
        {
            int at = SkipTrivia(src, _nodes[prev].End);
            if (at < src.Length && src[at] == (byte)':' && IsValueIndicator(prev, at) && (p.IsFlow || ColonBelongsTo(src, parent, prev, at)))
            {
                n.Start = n.End = at + 1;
            }
            else
            {
                n.Start = n.End = _nodes[prev].End;
                n.Flags |= NodeFlags.Bare;
            }
        }
        else if (p.Kind == NodeKind.Sequence && !p.IsFlow)
        {
            int from = prev >= 0 ? _nodes[prev].End : p.Start;
            int at = SkipTrivia(src, from);
            if (at < src.Length && src[at] == (byte)'-')
            {
                n.Start = n.End = at + 1;
            }
        }
    }

    // A colon on a later line than the key before it belongs to this block mapping only when it stands in the
    // mapping's own column; further left it belongs to a mapping around it (a nested "? k", then ":" at the margin).
    private bool ColonBelongsTo(ReadOnlySpan<byte> src, int mapping, int key, int colon)
    {
        int lineStart = colon;
        while (lineStart > 0 && src[lineStart - 1] is not ((byte)'\n' or (byte)'\r'))
        {
            lineStart--;
        }

        // A block scalar key ends with its last line break, so a colon at the start of the next line is not on the key's line.
        bool blockKey = _nodes[key].Kind == NodeKind.Scalar && (ScalarStyle)_nodes[key].Style is ScalarStyle.Literal or ScalarStyle.Folded;
        if (blockKey ? lineStart < _nodes[key].End : lineStart <= _nodes[key].End)
        {
            return true;
        }

        int mappingStart = _nodes[mapping].Start;
        int mappingLine = mappingStart;
        while (mappingLine > 0 && src[mappingLine - 1] is not ((byte)'\n' or (byte)'\r'))
        {
            mappingLine--;
        }

        return colon - lineStart == mappingStart - mappingLine;
    }

    /// <summary>Skips blanks, line breaks and comments.</summary>
    /// <param name="src">The text.</param>
    /// <param name="from">Where to start.</param>
    /// <returns>The offset of the next byte that is none of them.</returns>
    public static int SkipTrivia(ReadOnlySpan<byte> src, int from)
    {
        int i = from;
        while (i < src.Length)
        {
            byte b = src[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                i++;
            }
            else if (b == (byte)'#' && (i == 0 || src[i - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                int e = src.Slice(i).IndexOfAny((byte)'\n', (byte)'\r');
                if (e < 0)
                {
                    return src.Length;
                }

                i += e;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    // A small hash map from anchor names to nodes, with open addressing, on pooled arrays. Anchors are
    // rare, so nothing is rented until the first one.
    private sealed class AnchorMap : IDisposable
    {
        private int[]? _table;
        private int _tableSize; // a power of two; the rented array may be longer
        private const int Stride = 6;
        private int[]? _entries; // per entry: hash, start, length, node, start of the defining name, whether the node is still open
        private int _count;

        public void Clear()
        {
            if (_count != 0)
            {
                Array.Clear(_table!, 0, _tableSize);
                _count = 0;
            }
        }

        public int Find(ReadOnlySpan<byte> src, ReadOnlySpan<byte> name, out bool open)
        {
            open = false;
            if (_count == 0)
            {
                return -1;
            }

            int hash = Hash(name);
            int mask = _tableSize - 1;
            for (int slot = hash & mask; ; slot = (slot + 1) & mask)
            {
                int e = _table![slot];
                if (e == 0)
                {
                    return -1;
                }

                int at = (e - 1) * Stride;
                if (_entries![at] == hash && src.Slice(_entries[at + 1], _entries[at + 2]).SequenceEqual(name))
                {
                    open = _entries[at + 5] != 0;
                    return _entries[at + 3];
                }
            }
        }

        // The node of a collection is complete: aliases may name it from here on.
        public void Close(ReadOnlySpan<byte> src, int start, int length, int node)
        {
            ReadOnlySpan<byte> name = src.Slice(start, length);
            int hash = Hash(name);
            int mask = _tableSize - 1;
            for (int slot = hash & mask; ; slot = (slot + 1) & mask)
            {
                int e = _table![slot];
                if (e == 0)
                {
                    return;
                }

                int at = (e - 1) * Stride;
                if (_entries![at] == hash && src.Slice(_entries[at + 1], _entries[at + 2]).SequenceEqual(name))
                {
                    // A node written later with the same name may hold the entry now; then there is nothing to close.
                    if (_entries[at + 3] == node)
                    {
                        _entries[at + 5] = 0;
                    }

                    return;
                }
            }
        }

        public void Set(ReadOnlySpan<byte> src, int start, int length, int node, bool open = false)
        {
            ReadOnlySpan<byte> name = src.Slice(start, length);
            if (_table is null)
            {
                _tableSize = 16;
                _table = ArrayPool<int>.Shared.Rent(_tableSize);
                Array.Clear(_table, 0, _tableSize);
                _entries = ArrayPool<int>.Shared.Rent(16 * Stride);
            }

            int hash = Hash(name);
            int mask = _tableSize - 1;
            for (int slot = hash & mask; ; slot = (slot + 1) & mask)
            {
                int e = _table[slot];
                if (e == 0)
                {
                    break;
                }

                int at = (e - 1) * Stride;
                if (_entries![at] == hash && src.Slice(_entries[at + 1], _entries[at + 2]).SequenceEqual(name))
                {
                    // An anchor may be redefined; aliases after this point mean the new node.
                    // The definition whose name appears last in the text wins, even when a node that started earlier
                    // completes after it.
                    if (start > _entries[at + 4])
                    {
                        _entries[at + 3] = node;
                        _entries[at + 4] = start;
                        _entries[at + 5] = open ? 1 : 0;
                    }

                    return;
                }
            }

            if ((_count + 1) * 2 > _tableSize)
            {
                Rehash();
                mask = _tableSize - 1;
            }

            if ((_count + 1) * Stride > _entries!.Length)
            {
                int[] bigger = ArrayPool<int>.Shared.Rent(_entries.Length * 2);
                Array.Copy(_entries, bigger, _count * Stride);
                ArrayPool<int>.Shared.Return(_entries);
                _entries = bigger;
            }

            int w = _count * Stride;
            _entries[w] = hash;
            _entries[w + 1] = start;
            _entries[w + 2] = length;
            _entries[w + 3] = node;
            _entries[w + 4] = start;
            _entries[w + 5] = open ? 1 : 0;
            _count++;
            for (int slot = hash & mask; ; slot = (slot + 1) & mask)
            {
                if (_table[slot] == 0)
                {
                    _table[slot] = _count;
                    return;
                }
            }
        }

        private void Rehash()
        {
            int size = _tableSize * 2;
            int[] bigger = ArrayPool<int>.Shared.Rent(size);
            Array.Clear(bigger, 0, size);
            int mask = size - 1;
            for (int i = 0; i < _count; i++)
            {
                for (int slot = _entries![i * Stride] & mask; ; slot = (slot + 1) & mask)
                {
                    if (bigger[slot] == 0)
                    {
                        bigger[slot] = i + 1;
                        break;
                    }
                }
            }

            ArrayPool<int>.Shared.Return(_table!);
            _table = bigger;
            _tableSize = size;
        }

        private static int Hash(ReadOnlySpan<byte> name)
        {
            // A per-process random seed: with a fixed hash an input can pick anchor names
            // that all collide and turn every lookup into a linear scan.
            HashCode h = default;
            h.AddBytes(name);
            return h.ToHashCode() & 0x7FFFFFFF;
        }

        public void Dispose()
        {
            if (_table is not null)
            {
                ArrayPool<int>.Shared.Return(_table);
                ArrayPool<int>.Shared.Return(_entries!);
                _table = null;
                _entries = null;
            }
        }
    }
}
