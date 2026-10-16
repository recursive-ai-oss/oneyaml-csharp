using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace OneYaml.Parsing;

/// <summary>
/// A pull reader over validated UTF-8 YAML text. It produces events without building a tree,
/// allocates nothing per event, and keeps every position as a byte offset into the source.
/// Scalar text stays in the source until a consumer asks for its value.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal ref partial struct YamlReader
{
    internal enum State : byte
    {
        StreamStart,
        DocumentStart,
        DocumentContent,
        DocumentEnd,
        BlockNode,
        BlockSequenceFirstEntry,
        BlockSequenceEntry,
        IndentlessSequenceEntry,
        BlockMappingFirstKey,
        BlockMappingKey,
        BlockMappingValue,
        FlowSequenceFirstEntry,
        FlowSequenceEntry,
        FlowSequenceEntryMappingKey,
        FlowSequenceEntryMappingValue,
        FlowSequenceEntryMappingEnd,
        FlowMappingFirstKey,
        FlowMappingKey,
        FlowMappingValue,
        FlowMappingEmptyValue,
        End,
    }

    private const int MaxTagDirectives = 64;

    private readonly ReadOnlySpan<byte> _src;
    private readonly YamlLimits _limits;
    private readonly bool _recordComments;

    // Parser state.
    private State _state;
    private Span<State> _states;
    private State[]? _statesRented;
    private int _stateCount;
    private int _depth;
    private int _documents;
    private bool _afterExplicitEnd;
    private int[]? _tagDirectives;
    private int _tagDirectiveCount;

    // Pending node properties while a node event is being built.
    private int _anchorStart;
    private int _anchorLength;
    private int _tagStart;
    private int _tagLength;
    private int _tagHandleLength;
    private bool _tagVerbatim;
    private int _tagSuffixStart;
    private int _tagSuffixEnd;
    private int _tagPrefix;

    // The error that ended reading, kept so that a later Read reports it again.
    private YamlException? _failure;

    // How many events this reader has produced, and a cell that every copy of it shares and that holds the highest
    // count any of them reached. A reader that fails while the cell holds more than its own count has been left
    // behind by a copy that read on (or is the copy and was left behind), so its error blames the use, not the text.
    // The cell is empty for a reader that nobody can copy.
    private readonly Span<int> _cell;
    private int _serial;

    // Scalar details of the current event.
    private int _contentStart;
    private int _contentEnd;
    private bool _escapedOrFolded;
    private int _blockIndent;
    private Chomping _chomping;

    /// <summary>Creates a reader over validated UTF-8 text with the default options.</summary>
    /// <param name="utf8">The text, without a byte order mark. It must already be validated UTF-8.</param>
    public YamlReader(ReadOnlySpan<byte> utf8)
        : this(utf8, default)
    {
    }

    /// <summary>Creates a reader over validated UTF-8 text.</summary>
    /// <param name="utf8">The text, without a byte order mark. It must already be validated UTF-8.</param>
    /// <param name="options">Reader options.</param>
    public YamlReader(ReadOnlySpan<byte> utf8, YamlReaderOptions options)
        : this(utf8, options, default(Common))
    {
        _statesRented = ArrayPool<State>.Shared.Rent(16);
        _states = _statesRented;
        _indentsRented = ArrayPool<int>.Shared.Rent(16);
        _indents = _indentsRented;
        _keysRented = ArrayPool<SimpleKey>.Shared.Rent(16);
        _keys = _keysRented;
        _queueRented = ArrayPool<Token>.Shared.Rent(16);
        _queue = _queueRented;
        _flowKindsRented = ArrayPool<byte>.Shared.Rent(16);
        _flowKinds = _flowKindsRented;
    }

    // Marks the constructor that sets up everything but the working arrays.
    private readonly struct Common;

    /// <summary>
    /// Creates a reader whose first working space is in the caller's stack frame, so that reading a small
    /// text takes nothing from the array pool. The space moves to pooled arrays if the text needs more.
    /// </summary>
    /// <param name="utf8">The text, without a byte order mark. It must already be validated UTF-8.</param>
    /// <param name="options">Reader options.</param>
    /// <param name="scratch">Stack space that outlives the reader.</param>
    internal YamlReader(ReadOnlySpan<byte> utf8, YamlReaderOptions options, ref Scratch scratch)
        : this(utf8, options, default(Common))
    {
        _states = scratch.States;
        _indents = scratch.Indents;
        _keys = scratch.Keys;
        _queue = scratch.Queue;
        _flowKinds = scratch.FlowKinds;
        _cell = scratch.Cell;
        _cell[0] = 0;
    }

    private YamlReader(ReadOnlySpan<byte> utf8, YamlReaderOptions options, Common _)
    {
        _src = utf8;
        _limits = options.Limits ?? YamlLimits.Default;
        _recordComments = options.RecordComments;
        _comments = null;
        _afterExplicitEnd = true;
        _indent = -1;
        if (utf8.Length > _limits.MaxInputBytes)
        {
            throw new YamlException($"The input is larger than the limit MaxInputBytes ({_limits.MaxInputBytes}).", YamlErrorCategory.Limit, 0, 0, 0);
        }

        _hasNonPrintable = ContainsNonPrintable(utf8);
    }

    private const int ScratchSize = 16;

    [InlineArray(ScratchSize)]
    internal struct StateBuffer
    {
        private State _first;
    }

    [InlineArray(ScratchSize)]
    internal struct IntBuffer
    {
        private int _first;
    }

    [InlineArray(ScratchSize)]
    internal struct KeyBuffer
    {
        private SimpleKey _first;
    }

    [InlineArray(ScratchSize)]
    internal struct TokenBuffer
    {
        private Token _first;
    }

    [InlineArray(1)]
    internal struct CellBuffer
    {
        private int _first;
    }

    [InlineArray(ScratchSize)]
    internal struct ByteBuffer
    {
        private byte _first;
    }

    /// <summary>The first working space of a reader, to be kept in a local of the method that owns the reader.</summary>
    internal struct Scratch
    {
        public StateBuffer States;
        public IntBuffer Indents;
        public KeyBuffer Keys;
        public TokenBuffer Queue;
        public ByteBuffer FlowKinds;
        public CellBuffer Cell;
    }

    private static bool ContainsNonPrintable(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (true)
        {
            int rel = s[i..].IndexOfAny(s_nonPrintableLead);
            if (rel < 0)
            {
                return false;
            }

            i += rel;
            if (IsNonPrintableAt(s, i, out _))
            {
                return true;
            }

            i++;
        }
    }

    /// <summary>The kind of the current event.</summary>
    public YamlTokenType TokenType { get; private set; }

    /// <summary>The style of the current scalar.</summary>
    public ScalarStyle ScalarStyle { get; private set; }

    /// <summary>The style of the current collection.</summary>
    public CollectionStyle CollectionStyle { get; private set; }

    /// <summary>For a document start or end: whether the marker (<c>---</c> or <c>...</c>) is absent.</summary>
    public bool IsImplicit { get; private set; }

    /// <summary>The major number of the document's <c>%YAML</c> directive, or 0 when it has none.</summary>
    public int VersionMajor { get; private set; }

    /// <summary>The minor number of the document's <c>%YAML</c> directive.</summary>
    public int VersionMinor { get; private set; }

    /// <summary>The byte offset where the current event's own text starts.</summary>
    public int Start { get; private set; }

    /// <summary>The byte offset just after the current event's own text.</summary>
    public int End { get; private set; }

    /// <summary>
    /// The nesting level of the current node: 0 for a document's root, and one more inside each
    /// open collection. Start and end events of one collection report the same level.
    /// </summary>
    public int Depth { get; private set; }

    /// <summary>The anchor name of the current node (or the target name of an alias), without the indicator.</summary>
    public readonly ReadOnlySpan<byte> Anchor => _src.Slice(_anchorStart, _anchorLength);

    /// <summary>Whether the current node has an anchor, or the event is an alias.</summary>
    public readonly bool HasAnchor => _anchorLength != 0;

    /// <summary>The offset of the anchor or alias name, just after its <c>&amp;</c> or <c>*</c> indicator.</summary>
    public readonly int AnchorStart => _anchorStart;

    /// <summary>The tag exactly as written, for example <c>!!str</c>, <c>!local</c> or <c>!&lt;verbatim&gt;</c>.</summary>
    public readonly ReadOnlySpan<byte> RawTag => _src.Slice(_tagStart, _tagLength);

    /// <summary>Whether the current node has a tag.</summary>
    public readonly bool HasTag => _tagLength != 0;

    /// <summary>The offset where the raw tag starts.</summary>
    public readonly int TagStart => _tagStart;

    /// <summary>The scalar's source text without quotes or block header, before any unescaping or folding.</summary>
    public readonly ReadOnlySpan<byte> RawScalar => _src.Slice(_contentStart, _contentEnd - _contentStart);

    /// <summary>
    /// Whether the scalar's value differs from <see cref="RawScalar"/>: it has escapes, folded line
    /// breaks or doubled quotes. When false the raw span is the value.
    /// </summary>
    public readonly bool ValueIsEscapedOrFolded => _escapedOrFolded;

    /// <summary>For a block scalar: the column its content lines are indented to.</summary>
    public readonly int BlockIndent => _blockIndent;

    /// <summary>For a block scalar: how its trailing line breaks are treated.</summary>
    public readonly Chomping BlockChomping => _chomping;

    /// <summary>The number of comments recorded so far (zero unless comments are recorded).</summary>
    public readonly int CommentCount => _commentCount / 2;

    /// <summary>The offsets of a recorded comment, from its <c>#</c> to the end of the line.</summary>
    /// <param name="index">The comment index, in source order.</param>
    /// <returns>The start and end offsets.</returns>
    public readonly (int Start, int End) GetComment(int index) => (_comments![index * 2], _comments[(index * 2) + 1]);

    /// <summary>Returns pooled buffers. Call it when the reader is no longer used.</summary>
    public void Dispose()
    {
        Return(ref _statesRented);
        Return(ref _indentsRented);
        Return(ref _keysRented);
        Return(ref _queueRented);
        Return(ref _flowKindsRented);
        _states = default;
        _indents = default;
        _keys = default;
        _queue = default;
        _flowKinds = default;
        if (_comments is not null)
        {
            ArrayPool<int>.Shared.Return(_comments);
            _comments = null;
        }

        if (_tagDirectives is not null)
        {
            ArrayPool<int>.Shared.Return(_tagDirectives);
            _tagDirectives = null;
        }

        _state = State.End;
    }

    private static void Return<T>(ref T[]? array)
    {
        T[]? a = array;
        array = null;
        if (a is not null)
        {
            ArrayPool<T>.Shared.Return(a);
        }
    }

    /// <summary>Moves to the next event.</summary>
    /// <returns><see langword="false"/> after the last event.</returns>
    /// <exception cref="YamlException">The text is not valid YAML or exceeds a limit.</exception>
    public bool Read()
    {
        if (_state == State.End)
        {
            TokenType = YamlTokenType.None;
            return false;
        }

        if (_failure is not null)
        {
            // A reader that has failed has no valid position to go on from: it keeps reporting
            // the same error rather than guessing.
            throw _failure;
        }

        _anchorLength = 0;
        _tagLength = 0;
        _escapedOrFolded = false;
        _contentStart = _contentEnd = 0;
        IsImplicit = false;
        VersionMajor = VersionMinor = 0;
        try
        {
            ParseEvent();
        }
        catch (YamlException) when (IsLeftBehind())
        {
            throw new InvalidOperationException("The reader was used after a copy of it read on. A Utf8YamlReader must not be copied: the copy shares the scanner with the original, so reading on either one leaves the other without a valid position. Read ahead with the reader itself, or pass it by ref.");
        }
        catch (YamlException ex) when (_deferred is not null && !ReferenceEquals(ex, _deferred) && ex.Offset >= _deferredFrom)
        {
            // The scanner read ahead and found the real problem; the parser tripped over its effect.
            _failure = _deferred;
            throw _deferred;
        }
        catch (YamlException ex)
        {
            _failure = ex;
            throw;
        }

        _serial++;
        if (_cell.Length != 0)
        {
            _cell[0] = _serial;
        }

        return true;
    }

    // Whether a copy of this reader has read further than this one has.
    private readonly bool IsLeftBehind() => _cell.Length != 0 && _cell[0] != _serial;

    /// <summary>The tag resolved against the document's <c>%TAG</c> directives, or null when there is no tag.</summary>
    /// <remarks>Builds the string when asked, and keeps the last one, so a run of nodes that share a tag shares its string.</remarks>
    /// <returns>The full tag, or <c>!</c> for the non-specific tag.</returns>
    public string? GetResolvedTag()
    {
        if (_tagLength == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> prefix = TagPrefixBytes();
        ReadOnlySpan<byte> suffix = _src[_tagSuffixStart.._tagSuffixEnd];
        int key = PrefixKey();
        if (_resolvedTagCache is not null && _resolvedPrefixKey == key && _resolvedPrefixLength == prefix.Length
            && _src.Slice(_resolvedSuffixStart, _resolvedSuffixLength).SequenceEqual(suffix))
        {
            return _resolvedTagCache;
        }

        byte[] buffer = new byte[prefix.Length + suffix.Length];
        int n = DecodePercent(buffer, prefix, suffix);
        _resolvedTagCache = Encoding.UTF8.GetString(buffer, 0, n);
        _resolvedPrefixKey = key;
        _resolvedPrefixLength = prefix.Length;
        _resolvedSuffixStart = _tagSuffixStart;
        _resolvedSuffixLength = suffix.Length;
        return _resolvedTagCache;
    }

    /// <summary>
    /// Says which Core tag the node's tag resolves to, without building the tag string. A long <c>%TAG</c> prefix
    /// used by many nodes costs nothing here.
    /// </summary>
    /// <returns><see cref="TagClass.None"/> when the node has no tag.</returns>
    public readonly TagClass GetTagClass()
    {
        if (_tagLength == 0)
        {
            return TagClass.None;
        }

        ReadOnlySpan<byte> prefix = TagPrefixBytes();
        ReadOnlySpan<byte> suffix = _src[_tagSuffixStart.._tagSuffixEnd];

        // The longest Core tag is 24 bytes and decoding never lengthens, so anything larger is another tag.
        if (prefix.Length > 24 || suffix.Length > 72)
        {
            return TagClass.Unknown;
        }

        Span<byte> buffer = stackalloc byte[96];
        int n = DecodePercent(buffer, prefix, suffix);
        ReadOnlySpan<byte> resolved = buffer[..n];
        if (resolved.Length == 1 && resolved[0] == (byte)'!')
        {
            return TagClass.Str;
        }

        if (!resolved.StartsWith("tag:yaml.org,2002:"u8))
        {
            return TagClass.Unknown;
        }

        ReadOnlySpan<byte> name = resolved["tag:yaml.org,2002:".Length..];
        if (name.SequenceEqual("str"u8))
        {
            return TagClass.Str;
        }

        if (name.SequenceEqual("int"u8))
        {
            return TagClass.Int;
        }

        if (name.SequenceEqual("float"u8))
        {
            return TagClass.Float;
        }

        if (name.SequenceEqual("bool"u8))
        {
            return TagClass.Bool;
        }

        if (name.SequenceEqual("null"u8))
        {
            return TagClass.Null;
        }

        if (name.SequenceEqual("binary"u8))
        {
            return TagClass.Binary;
        }

        if (name.SequenceEqual("map"u8))
        {
            return TagClass.Map;
        }

        return name.SequenceEqual("seq"u8) ? TagClass.Seq : TagClass.Unknown;
    }

    // The last resolved tag and what it was made from, so repeated tags are not rebuilt.
    private string? _resolvedTagCache;
    private int _resolvedPrefixKey;
    private int _resolvedPrefixLength;
    private int _resolvedSuffixStart;
    private int _resolvedSuffixLength;

    // Names the prefix without comparing its bytes: where a directive's prefix starts in the text, or a negative
    // number for the built-in handles and verbatim tags.
    private readonly int PrefixKey()
    {
        if (_tagVerbatim)
        {
            return -3;
        }

        return _tagPrefix < 0 ? _tagPrefix : _tagDirectives![(_tagPrefix * 4) + 2];
    }

    private readonly ReadOnlySpan<byte> TagPrefixBytes()
    {
        if (_tagVerbatim)
        {
            return default;
        }

        if (_tagPrefix == -2)
        {
            return "tag:yaml.org,2002:"u8;
        }

        if (_tagPrefix == -1)
        {
            return "!"u8;
        }

        return _src[_tagDirectives![(_tagPrefix * 4) + 2].._tagDirectives[(_tagPrefix * 4) + 3]];
    }

    /// <summary>
    /// Says where the current tag's prefix and suffix are, so that a caller can keep four integers per node and build
    /// the full tag only if it is asked for. A prefix start of -1 means the built-in <c>!</c> and -2 means
    /// <c>tag:yaml.org,2002:</c>; otherwise the prefix is the text at that offset.
    /// </summary>
    internal readonly void GetTagParts(out int prefixStart, out int prefixLength, out int suffixStart, out int suffixLength)
    {
        suffixStart = _tagSuffixStart;
        suffixLength = _tagSuffixEnd - _tagSuffixStart;
        if (_tagVerbatim)
        {
            prefixStart = 0;
            prefixLength = 0;
        }
        else if (_tagPrefix < 0)
        {
            prefixStart = _tagPrefix;
            prefixLength = 0;
        }
        else
        {
            prefixStart = _tagDirectives![(_tagPrefix * 4) + 2];
            prefixLength = _tagDirectives[(_tagPrefix * 4) + 3] - prefixStart;
        }
    }

    /// <summary>Builds a full tag from the parts that <see cref="GetTagParts"/> gave.</summary>
    internal static string BuildTag(ReadOnlySpan<byte> src, int prefixStart, int prefixLength, int suffixStart, int suffixLength)
    {
        ReadOnlySpan<byte> prefix = prefixStart switch
        {
            -2 => "tag:yaml.org,2002:"u8,
            -1 => "!"u8,
            _ => src.Slice(prefixStart, prefixLength),
        };
        ReadOnlySpan<byte> suffix = src.Slice(suffixStart, suffixLength);
        byte[] buffer = new byte[prefix.Length + suffix.Length];
        int n = DecodePercent(buffer, prefix, suffix);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    // Writes prefix then suffix with escaped ASCII characters expanded; a UTF-8 sequence stays as written.
    internal static int DecodePercent(Span<byte> buffer, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
    {
        prefix.CopyTo(buffer);
        int n = prefix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            byte b = suffix[i];
            if (b == (byte)'%' && i + 2 < suffix.Length && ((HexValue(suffix[i + 1]) << 4) | HexValue(suffix[i + 2])) < 0x80)
            {
                buffer[n++] = (byte)((HexValue(suffix[i + 1]) << 4) | HexValue(suffix[i + 2]));
                i += 2;
            }
            else
            {
                buffer[n++] = b;
            }
        }

        return n;
    }

    private static int HexValue(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    private readonly YamlException Error(string message, int offset)
        => YamlException.Located(YamlErrorCategory.Syntax, message, _src, offset);

    private readonly YamlException LimitError(string message, int offset)
        => YamlException.Located(YamlErrorCategory.Limit, message, _src, offset);

    private void PushState(State s)
    {
        if (_stateCount == _states.Length)
        {
            Grow(ref _states, ref _statesRented, _stateCount);
        }

        _states[_stateCount++] = s;
    }

    private State PopState() => _states[--_stateCount];

    // The size to grow an array of this length to: double, but never past the largest array, and
    // -1 when it cannot grow. Doubling an int would overflow near 2^30 elements.
    internal static int NextCapacity(int length)
    {
        if (length >= Array.MaxLength)
        {
            return -1;
        }

        return length > Array.MaxLength / 2 ? Array.MaxLength : Math.Max(length * 2, 16);
    }

    private readonly void Grow<T>(ref T[] array, int count)
    {
        int wanted = NextCapacity(array.Length);
        if (wanted < 0)
        {
            throw LimitError("The input needs more working memory than an array can hold.", _src.Length);
        }

        T[] bigger = ArrayPool<T>.Shared.Rent(wanted);
        Array.Copy(array, bigger, count);
        ArrayPool<T>.Shared.Return(array);
        array = bigger;
    }

    private readonly void Grow<T>(ref Span<T> items, ref T[]? rented, int count)
    {
        int wanted = NextCapacity(items.Length);
        if (wanted < 0)
        {
            throw LimitError("The input needs more working memory than an array can hold.", _src.Length);
        }

        T[] bigger = ArrayPool<T>.Shared.Rent(wanted);
        items[..count].CopyTo(bigger);
        if (rented is not null)
        {
            ArrayPool<T>.Shared.Return(rented);
        }

        rented = bigger;
        items = bigger;
    }

    // ---- Events ----

    private void ParseEvent()
    {
        switch (_state)
        {
            case State.StreamStart:
                PeekKind();
                SkipToken();
                TokenType = YamlTokenType.StreamStart;
                Start = End = 0;
                Depth = 0;
                _state = State.DocumentStart;
                break;
            case State.DocumentStart:
                ParseDocumentStart();
                break;
            case State.DocumentContent:
                ParseDocumentContent();
                break;
            case State.DocumentEnd:
                ParseDocumentEnd();
                break;
            case State.BlockNode:
                ParseNode(block: true, indentless: false);
                break;
            case State.BlockSequenceFirstEntry:
                ParseBlockSequenceEntry(first: true);
                break;
            case State.BlockSequenceEntry:
                ParseBlockSequenceEntry(first: false);
                break;
            case State.IndentlessSequenceEntry:
                ParseIndentlessSequenceEntry();
                break;
            case State.BlockMappingFirstKey:
                ParseBlockMappingKey(first: true);
                break;
            case State.BlockMappingKey:
                ParseBlockMappingKey(first: false);
                break;
            case State.BlockMappingValue:
                ParseBlockMappingValue();
                break;
            case State.FlowSequenceFirstEntry:
                ParseFlowSequenceEntry(first: true);
                break;
            case State.FlowSequenceEntry:
                ParseFlowSequenceEntry(first: false);
                break;
            case State.FlowSequenceEntryMappingKey:
                ParseFlowSequenceEntryMappingKey();
                break;
            case State.FlowSequenceEntryMappingValue:
                ParseFlowSequenceEntryMappingValue();
                break;
            case State.FlowSequenceEntryMappingEnd:
                _state = State.FlowSequenceEntry;
                PeekKind();
                EmitCollectionEnd(YamlTokenType.MappingEnd, _tok.Start, _tok.Start, CollectionStyle.Flow);
                break;
            case State.FlowMappingFirstKey:
                ParseFlowMappingKey(first: true);
                break;
            case State.FlowMappingKey:
                ParseFlowMappingKey(first: false);
                break;
            case State.FlowMappingValue:
                ParseFlowMappingValue(empty: false);
                break;
            case State.FlowMappingEmptyValue:
                ParseFlowMappingValue(empty: true);
                break;
            default:
                throw new InvalidOperationException("The reader is in an invalid state.");
        }
    }

    private void ParseDocumentStart()
    {
        TokenKind kind = PeekKind();
        while (kind == TokenKind.DocumentEnd)
        {
            SkipToken();
            _afterExplicitEnd = true;
            kind = PeekKind();
        }

        if (kind == TokenKind.StreamEnd)
        {
            TokenType = YamlTokenType.StreamEnd;
            Start = End = _tok.Start;
            Depth = 0;
            SkipToken();
            _state = State.End;
            return;
        }

        if (++_documents > _limits.MaxDocuments)
        {
            throw LimitError($"The stream has more documents than the limit MaxDocuments ({_limits.MaxDocuments}).", _tok.Start);
        }

        if (kind is TokenKind.VersionDirective or TokenKind.TagDirective or TokenKind.ReservedDirective)
        {
            if (!_afterExplicitEnd)
            {
                throw Error("A directive needs a document end marker before it.", _tok.Start);
            }

            ProcessDirectives(out int major, out int minor);
            if (PeekKind() != TokenKind.DocumentStart)
            {
                throw Error("A directive must be followed by a document start marker.", _tok.Start);
            }

            TokenType = YamlTokenType.DocumentStart;
            Start = _tok.Start;
            End = _tok.End;
            VersionMajor = major;
            VersionMinor = minor;
            SkipToken();
            EnterDocument(implicitStart: false);
            return;
        }

        _tagDirectiveCount = 0;
        if (kind == TokenKind.DocumentStart)
        {
            TokenType = YamlTokenType.DocumentStart;
            Start = _tok.Start;
            End = _tok.End;
            SkipToken();
            EnterDocument(implicitStart: false);
            return;
        }

        if (!_afterExplicitEnd)
        {
            throw Error("Expected a document start marker.", _tok.Start);
        }

        TokenType = YamlTokenType.DocumentStart;
        Start = End = _tok.Start;
        IsImplicit = true;
        PushState(State.DocumentEnd);
        _state = State.BlockNode;
        _afterExplicitEnd = false;
        Depth = 0;
    }

    private void EnterDocument(bool implicitStart)
    {
        IsImplicit = implicitStart;
        PushState(State.DocumentEnd);
        _state = State.DocumentContent;
        _afterExplicitEnd = false;
        Depth = 0;
    }

    private void ParseDocumentContent()
    {
        TokenKind kind = PeekKind();
        if (kind is TokenKind.VersionDirective or TokenKind.TagDirective or TokenKind.ReservedDirective)
        {
            throw Error("A directive must come before the document start marker.", _tok.Start);
        }

        if (kind is TokenKind.DocumentStart or TokenKind.DocumentEnd or TokenKind.StreamEnd)
        {
            _state = PopState();
            EmitEmptyScalar(_tok.Start);
            return;
        }

        ParseNode(block: true, indentless: false);
    }

    private void ParseDocumentEnd()
    {
        TokenKind kind = PeekKind();
        TokenType = YamlTokenType.DocumentEnd;
        Depth = 0;
        if (kind == TokenKind.DocumentEnd)
        {
            Start = _tok.Start;
            End = _tok.End;
            SkipToken();
            _afterExplicitEnd = true;
        }
        else if (kind is TokenKind.DocumentStart or TokenKind.StreamEnd)
        {
            Start = End = _tok.Start;
            IsImplicit = true;
        }
        else
        {
            // The document is still open: only a marker or the end of the stream can end it.
            throw Error("Expected the end of the document.", _tok.Start);
        }

        _state = State.DocumentStart;
    }

    private void ProcessDirectives(out int major, out int minor)
    {
        major = 0;
        minor = 0;
        _tagDirectiveCount = 0;
        bool haveVersion = false;
        while (true)
        {
            TokenKind kind = PeekKind();
            if (kind == TokenKind.VersionDirective)
            {
                if (haveVersion)
                {
                    throw Error("A document has more than one %YAML directive.", _tok.Start);
                }

                if (_tok.X != 1)
                {
                    throw Error("Only YAML version 1 is supported.", _tok.Start);
                }

                haveVersion = true;
                major = _tok.X;
                minor = _tok.Y;
                SkipToken();
            }
            else if (kind == TokenKind.TagDirective)
            {
                AddTagDirective();
                SkipToken();
            }
            else if (kind == TokenKind.ReservedDirective)
            {
                SkipToken();
            }
            else
            {
                return;
            }
        }
    }

    private void AddTagDirective()
    {
        ReadOnlySpan<byte> handle = _src[_tok.ContentStart.._tok.ContentEnd];
        _tagDirectives ??= ArrayPool<int>.Shared.Rent(MaxTagDirectives * 4);
        for (int i = 0; i < _tagDirectiveCount; i++)
        {
            ReadOnlySpan<byte> other = _src[_tagDirectives[i * 4]..(_tagDirectives[i * 4] + _tagDirectives[(i * 4) + 1])];
            if (other.SequenceEqual(handle))
            {
                throw Error("A tag handle is declared twice.", _tok.Start);
            }
        }

        if (_tagDirectiveCount == MaxTagDirectives)
        {
            throw LimitError("A document declares too many tag handles.", _tok.Start);
        }

        int at = _tagDirectiveCount++ * 4;
        _tagDirectives[at] = _tok.ContentStart;
        _tagDirectives[at + 1] = _tok.ContentEnd - _tok.ContentStart;
        _tagDirectives[at + 2] = _tok.X;
        _tagDirectives[at + 3] = _tok.Y;
    }

    private void ParseNode(bool block, bool indentless)
    {
        TokenKind kind = PeekKind();
        if (kind == TokenKind.Alias)
        {
            TokenType = YamlTokenType.Alias;
            Start = _tok.Start;
            End = _tok.End;
            _anchorStart = _tok.Start;
            _anchorLength = _tok.ContentEnd - _tok.ContentStart;
            _anchorStart = _tok.ContentStart;
            Depth = _depth;
            SkipToken();
            _state = PopState();
            return;
        }

        int propsStart = -1;
        if (kind == TokenKind.Anchor)
        {
            propsStart = _tok.Start;
            TakeAnchor();
            kind = PeekKind();
            if (kind == TokenKind.Tag)
            {
                TakeTag();
                kind = PeekKind();
            }
        }
        else if (kind == TokenKind.Tag)
        {
            propsStart = _tok.Start;
            TakeTag();
            kind = PeekKind();
            if (kind == TokenKind.Anchor)
            {
                TakeAnchor();
                kind = PeekKind();
            }
        }

        if (indentless && kind == TokenKind.BlockEntry)
        {
            EmitCollectionStart(YamlTokenType.SequenceStart, _tok.Start, _tok.Start, CollectionStyle.Block);
            _state = State.IndentlessSequenceEntry;
            return;
        }

        switch (kind)
        {
            case TokenKind.Scalar:
                EmitScalar(in _tok);
                SkipToken();
                _state = PopState();
                return;
            case TokenKind.FlowSequenceStart:
                EmitCollectionStart(YamlTokenType.SequenceStart, _tok.Start, _tok.End, CollectionStyle.Flow);
                _state = State.FlowSequenceFirstEntry;
                return;
            case TokenKind.FlowMappingStart:
                EmitCollectionStart(YamlTokenType.MappingStart, _tok.Start, _tok.End, CollectionStyle.Flow);
                _state = State.FlowMappingFirstKey;
                return;
            case TokenKind.BlockSequenceStart when block:
                EmitCollectionStart(YamlTokenType.SequenceStart, _tok.Start, _tok.Start, CollectionStyle.Block);
                _state = State.BlockSequenceFirstEntry;
                return;
            case TokenKind.BlockMappingStart when block:
                EmitCollectionStart(YamlTokenType.MappingStart, _tok.Start, _tok.Start, CollectionStyle.Block);
                _state = State.BlockMappingFirstKey;
                return;
        }

        if (propsStart >= 0)
        {
            _state = PopState();
            EmitEmptyScalar(_tok.Start);
            return;
        }

        throw Error("Expected a node but found something else.", _tok.Start);
    }

    private void TakeAnchor()
    {
        _anchorStart = _tok.ContentStart;
        _anchorLength = _tok.ContentEnd - _tok.ContentStart;
        SkipToken();
        PeekKind();
    }

    private void TakeTag()
    {
        _tagStart = _tok.Start;
        _tagLength = _tok.End - _tok.Start;
        _tagHandleLength = _tok.X;
        _tagVerbatim = (_tok.Flags & 1) != 0;
        _tagSuffixStart = _tok.ContentStart;
        _tagSuffixEnd = _tok.ContentEnd;
        if (!_tagVerbatim)
        {
            ReadOnlySpan<byte> handle = _src.Slice(_tagStart, _tagHandleLength);
            int found = -3;
            for (int i = 0; i < _tagDirectiveCount; i++)
            {
                int hs = _tagDirectives![i * 4];
                if (_src.Slice(hs, _tagDirectives[(i * 4) + 1]).SequenceEqual(handle))
                {
                    found = i;
                    break;
                }
            }

            if (found == -3)
            {
                if (handle.Length == 1)
                {
                    found = -1;
                }
                else if (handle.Length == 2 && handle[1] == (byte)'!')
                {
                    found = -2;
                }
                else
                {
                    throw Error("The tag handle is not declared.", _tok.Start);
                }
            }

            _tagPrefix = found;
        }

        SkipToken();
        PeekKind();
    }

    private void EmitScalar(in Token t)
    {
        TokenType = YamlTokenType.Scalar;
        Start = t.Start;
        End = t.End;
        ScalarStyle = (ScalarStyle)t.Style;
        _contentStart = t.ContentStart;
        _contentEnd = t.ContentEnd;
        _escapedOrFolded = (t.Flags & 1) != 0;
        _blockIndent = t.X;
        _chomping = (Chomping)t.Y;
        Depth = _depth;
    }

    private void EmitEmptyScalar(int at)
    {
        TokenType = YamlTokenType.Scalar;
        Start = End = at;
        ScalarStyle = ScalarStyle.Plain;
        _contentStart = _contentEnd = at;
        _escapedOrFolded = false;
        Depth = _depth;
    }

    private void EmitCollectionStart(YamlTokenType type, int start, int end, CollectionStyle style)
    {
        if (_depth >= _limits.MaxDepth)
        {
            throw LimitError($"The nesting depth is larger than the limit MaxDepth ({_limits.MaxDepth}).", start);
        }

        TokenType = type;
        Start = start;
        End = end;
        CollectionStyle = style;
        Depth = _depth++;
        if (end > start)
        {
            // A flow indicator belongs to the event; the parser state skips the token itself later.
        }
    }

    private void EmitCollectionEnd(YamlTokenType type, int start, int end, CollectionStyle style)
    {
        TokenType = type;
        Start = start;
        End = end;
        CollectionStyle = style;
        Depth = --_depth;
    }

    private void ParseBlockSequenceEntry(bool first)
    {
        if (first)
        {
            PeekKind();
            SkipToken();
        }

        TokenKind kind = PeekKind();
        if (kind == TokenKind.BlockEntry)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.BlockEntry or TokenKind.BlockEnd))
            {
                PushState(State.BlockSequenceEntry);
                ParseNode(block: true, indentless: false);
                return;
            }

            _state = State.BlockSequenceEntry;
            EmitEmptyScalar(_tok.Start);
            return;
        }

        if (kind == TokenKind.BlockEnd)
        {
            _state = PopState();
            EmitCollectionEnd(YamlTokenType.SequenceEnd, _tok.Start, _tok.Start, CollectionStyle.Block);
            SkipToken();
            return;
        }

        throw Error("Expected '-' to continue the block sequence.", _tok.Start);
    }

    private void ParseIndentlessSequenceEntry()
    {
        TokenKind kind = PeekKind();
        if (kind == TokenKind.BlockEntry)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.BlockEntry or TokenKind.Key or TokenKind.Value or TokenKind.BlockEnd))
            {
                PushState(State.IndentlessSequenceEntry);
                ParseNode(block: true, indentless: false);
                return;
            }

            _state = State.IndentlessSequenceEntry;
            EmitEmptyScalar(_tok.Start);
            return;
        }

        _state = PopState();
        EmitCollectionEnd(YamlTokenType.SequenceEnd, _tok.Start, _tok.Start, CollectionStyle.Block);
    }

    private void ParseBlockMappingKey(bool first)
    {
        if (first)
        {
            PeekKind();
            SkipToken();
        }

        TokenKind kind = PeekKind();
        if (kind == TokenKind.Key)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.Key or TokenKind.Value or TokenKind.BlockEnd))
            {
                PushState(State.BlockMappingValue);
                ParseNode(block: true, indentless: true);
                return;
            }

            _state = State.BlockMappingValue;
            EmitEmptyScalar(_tok.Start);
            return;
        }

        if (kind == TokenKind.BlockEntry)
        {
            throw Error("A sequence entry cannot follow a mapping at the same indentation.", _tok.Start + 1);
        }

        if (kind == TokenKind.Value)
        {
            // A value with no key: the key is empty.
            _state = State.BlockMappingValue;
            EmitEmptyScalar(_tok.Start);
            return;
        }

        if (kind == TokenKind.BlockEnd)
        {
            _state = PopState();
            EmitCollectionEnd(YamlTokenType.MappingEnd, _tok.Start, _tok.Start, CollectionStyle.Block);
            SkipToken();
            return;
        }

        throw Error("Expected a mapping key.", _tok.Start);
    }

    private void ParseBlockMappingValue()
    {
        TokenKind kind = PeekKind();
        if (kind == TokenKind.Value)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.Key or TokenKind.Value or TokenKind.BlockEnd))
            {
                PushState(State.BlockMappingKey);
                ParseNode(block: true, indentless: true);
                return;
            }
        }

        _state = State.BlockMappingKey;
        EmitEmptyScalar(_tok.Start);
    }

    private void ParseFlowSequenceEntry(bool first)
    {
        if (first)
        {
            PeekKind();
            SkipToken();
        }

        TokenKind kind = PeekKind();
        if (kind != TokenKind.FlowSequenceEnd)
        {
            if (!first)
            {
                if (kind != TokenKind.FlowEntry)
                {
                    throw Error("Expected ',' or ']' in the flow sequence.", _tok.Start);
                }

                SkipToken();
                kind = PeekKind();
            }

            if (kind is TokenKind.Key or TokenKind.Value)
            {
                _state = State.FlowSequenceEntryMappingKey;
                EmitCollectionStart(YamlTokenType.MappingStart, _tok.Start, _tok.Start, CollectionStyle.Flow);
                if (kind == TokenKind.Key)
                {
                    SkipToken();
                }

                return;
            }

            if (kind != TokenKind.FlowSequenceEnd)
            {
                PushState(State.FlowSequenceEntry);
                ParseNode(block: false, indentless: false);
                return;
            }
        }

        _state = PopState();
        EmitCollectionEnd(YamlTokenType.SequenceEnd, _tok.Start, _tok.End, CollectionStyle.Flow);
        SkipToken();
    }

    private void ParseFlowSequenceEntryMappingKey()
    {
        TokenKind kind = PeekKind();
        if (kind is not (TokenKind.Value or TokenKind.FlowEntry or TokenKind.FlowSequenceEnd))
        {
            PushState(State.FlowSequenceEntryMappingValue);
            ParseNode(block: false, indentless: false);
            return;
        }

        _state = State.FlowSequenceEntryMappingValue;
        EmitEmptyScalar(_tok.Start);
    }

    private void ParseFlowSequenceEntryMappingValue()
    {
        TokenKind kind = PeekKind();
        if (kind == TokenKind.Value)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.FlowEntry or TokenKind.FlowSequenceEnd))
            {
                PushState(State.FlowSequenceEntryMappingEnd);
                ParseNode(block: false, indentless: false);
                return;
            }
        }

        _state = State.FlowSequenceEntryMappingEnd;
        EmitEmptyScalar(_tok.Start);
    }

    private void ParseFlowMappingKey(bool first)
    {
        if (first)
        {
            PeekKind();
            SkipToken();
        }

        TokenKind kind = PeekKind();
        if (kind != TokenKind.FlowMappingEnd)
        {
            if (!first)
            {
                if (kind != TokenKind.FlowEntry)
                {
                    throw Error("Expected ',' or '}' in the flow mapping.", _tok.Start);
                }

                SkipToken();
                kind = PeekKind();
            }

            if (kind == TokenKind.Key)
            {
                SkipToken();
                TokenKind next = PeekKind();
                if (next is not (TokenKind.Value or TokenKind.FlowEntry or TokenKind.FlowMappingEnd))
                {
                    PushState(State.FlowMappingValue);
                    ParseNode(block: false, indentless: false);
                    return;
                }

                _state = State.FlowMappingValue;
                EmitEmptyScalar(_tok.Start);
                return;
            }

            if (kind == TokenKind.Value)
            {
                _state = State.FlowMappingValue;
                EmitEmptyScalar(_tok.Start);
                return;
            }

            if (kind != TokenKind.FlowMappingEnd)
            {
                PushState(State.FlowMappingEmptyValue);
                ParseNode(block: false, indentless: false);
                return;
            }
        }

        _state = PopState();
        EmitCollectionEnd(YamlTokenType.MappingEnd, _tok.Start, _tok.End, CollectionStyle.Flow);
        SkipToken();
    }

    private void ParseFlowMappingValue(bool empty)
    {
        TokenKind kind = PeekKind();
        if (!empty && kind == TokenKind.Value)
        {
            SkipToken();
            TokenKind next = PeekKind();
            if (next is not (TokenKind.FlowEntry or TokenKind.FlowMappingEnd))
            {
                PushState(State.FlowMappingKey);
                ParseNode(block: false, indentless: false);
                return;
            }
        }

        _state = State.FlowMappingKey;
        EmitEmptyScalar(_tok.Start);
    }
}
