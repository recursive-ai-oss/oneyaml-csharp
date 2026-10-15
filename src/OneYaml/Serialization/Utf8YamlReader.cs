using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using OneYaml.Parsing;

namespace OneYaml;

/// <summary>What <see cref="Utf8YamlReader"/> is positioned on.</summary>
public enum YamlTokenKind : byte
{
    /// <summary>Not positioned on a value yet, or past the end of the document.</summary>
    None = 0,

    /// <summary>A scalar.</summary>
    Scalar,

    /// <summary>The start of a mapping.</summary>
    MappingStart,

    /// <summary>The end of a mapping.</summary>
    MappingEnd,

    /// <summary>The start of a sequence.</summary>
    SequenceStart,

    /// <summary>The end of a sequence.</summary>
    SequenceEnd,
}

// What a tag on a node means to the binder. Only Core schema tags change how a value is read;
// every other tag is kept out of the way and never constructs anything.
internal enum TagClass : byte
{
    None = 0,
    Unknown,
    Str,
    Int,
    Float,
    Bool,
    Null,
    Binary,
    Map,
    Seq,
}

// One recorded event of an anchored node, enough to deliver it again from the source text.
internal struct TapeEvent
{
    public YamlTokenType Type;
    public ScalarStyle Style;
    public TagClass Tag;
    public Chomping Chomping;
    public bool Escaped;
    public int Start;
    public int End;
    public int ContentStart;
    public int ContentEnd;
    public int BlockIndent;

    // For an alias event, the index of the anchor entry it refers to.
    public int Alias;
}

// An anchor definition. Names are offsets into the source, so nothing is allocated per anchor.
internal struct AnchorEntry
{
    public int NameStart;
    public int NameLength;
    public uint Hash;
    public int TapeStart;
    public int TapeEnd;
    public long Nodes;
    public long Bytes;

    // Whether the anchored node is a single scalar. The tape range alone cannot say: when aliases are not
    // expanded, only scalars are kept on it, so a collection holding one anchored scalar looks the same.
    public bool IsScalar;
}

internal struct ReplayFrame
{
    public int Start;
    public int Pos;
    public int End;
    public int DepthBefore;
}

internal struct OpenAnchor
{
    public int Entry;
    public int LiveDepth;
    public long NodesAt;
    public long BytesAt;
}

// Everything that only exists once a document uses anchors, so documents without them pay nothing.
internal sealed class AnchorBook
{
    public AnchorEntry[] Entries = new AnchorEntry[8];
    public int Count;
    public int[] Buckets = new int[16];
    public object?[]? Values;
    public TapeEvent[] Tape = [];
    public int TapeCount;
    public OpenAnchor[] Open = new OpenAnchor[4];
    public int OpenCount;
    public ReplayFrame[] Frames = new ReplayFrame[4];
    public int FrameCount;

    public void Reset()
    {
        Count = 0;
        Array.Clear(Buckets);
        if (Values is not null)
        {
            Array.Clear(Values);
        }

        TapeCount = 0;
        OpenCount = 0;
        FrameCount = 0;
    }

    public void ReturnBuffers()
    {
        if (Tape.Length != 0)
        {
            ArrayPool<TapeEvent>.Shared.Return(Tape);
            Tape = [];
        }
    }

    public static uint HashOf(ReadOnlySpan<byte> name)
    {
        uint h = 2166136261;
        foreach (byte b in name)
        {
            h = (h ^ b) * 16777619;
        }

        return h;
    }

    public int Find(ReadOnlySpan<byte> src, ReadOnlySpan<byte> name, uint hash)
    {
        int mask = Buckets.Length - 1;
        int i = (int)(hash & (uint)mask);
        while (true)
        {
            int slot = Buckets[i];
            if (slot == 0)
            {
                return -1;
            }

            ref AnchorEntry e = ref Entries[slot - 1];
            if (e.Hash == hash && src.Slice(e.NameStart, e.NameLength).SequenceEqual(name))
            {
                return slot - 1;
            }

            i = (i + 1) & mask;
        }
    }

    // Makes the entry the one the name refers to, replacing an earlier definition of the same name.
    public void Publish(ReadOnlySpan<byte> src, int entry)
    {
        if ((Count + 1) * 2 > Buckets.Length)
        {
            Rebuild(src);
        }

        int mask = Buckets.Length - 1;
        ref AnchorEntry mine = ref Entries[entry];
        int i = (int)(mine.Hash & (uint)mask);
        ReadOnlySpan<byte> name = src.Slice(mine.NameStart, mine.NameLength);
        while (true)
        {
            int slot = Buckets[i];
            if (slot == 0)
            {
                Buckets[i] = entry + 1;
                return;
            }

            ref AnchorEntry e = ref Entries[slot - 1];
            if (e.Hash == mine.Hash && src.Slice(e.NameStart, e.NameLength).SequenceEqual(name))
            {
                Buckets[i] = entry + 1;
                return;
            }

            i = (i + 1) & mask;
        }
    }

    private void Rebuild(ReadOnlySpan<byte> src)
    {
        int[] old = Buckets;
        Buckets = new int[old.Length * 2];
        int mask = Buckets.Length - 1;
        foreach (int slot in old)
        {
            if (slot == 0)
            {
                continue;
            }

            ref AnchorEntry e = ref Entries[slot - 1];
            int i = (int)(e.Hash & (uint)mask);
            while (Buckets[i] != 0)
            {
                i = (i + 1) & mask;
            }

            Buckets[i] = slot;
        }
    }
}

/// <summary>
/// Reads the values of one YAML document for converters. It is positioned on a scalar or on the
/// start or end of a collection, never on a document marker or an alias: aliases are followed
/// for you, and every expansion counts against the limits.
/// </summary>
/// <remarks>
/// A converter's <c>Read</c> is called with the reader on the first token of its value and must
/// leave it on the last token of that value: the scalar itself, or the matching end token.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public ref partial struct Utf8YamlReader
{
    private const long Cap = 1L << 60;

    private YamlReader _r;
    private readonly ReadOnlySpan<byte> _src;
    private readonly YamlSerializerOptions _options;
    private readonly YamlLimits _limits;
    private readonly bool _expand;
    private AnchorBook? _book;
    private string?[]? _strings;

    private bool _started;
    private int _depth;
    private int _liveDepth;
    private long _liveNodes;
    private long _expNodes;
    private long _expBytes;
    private long _chargedNodes;
    private long _chargedBytes;

    // The current event.
    internal YamlTokenType Type;
    internal ScalarStyle Style;
    internal TagClass Tag;
    internal bool Escaped;
    internal Chomping Chomping;
    internal int ContentStart;
    internal int ContentEnd;
    internal int BlockIndent;
    internal int Start;
    internal int End;
    internal int AliasEntry;

    // The anchor entry defined on the current node, or -1.
    internal int NodeEntry;

    internal Utf8YamlReader(ReadOnlySpan<byte> utf8, YamlSerializerOptions options, bool expandAliases)
    {
        _options = options;
        _limits = options.Limits;
        _src = utf8;
        _expand = expandAliases;
        _r = new YamlReader(utf8, new YamlReaderOptions { Limits = _limits });
        NodeEntry = -1;
    }

    /// <summary>The kind of the current token.</summary>
    public readonly YamlTokenKind TokenType => Type switch
    {
        YamlTokenType.Scalar => YamlTokenKind.Scalar,
        YamlTokenType.MappingStart => YamlTokenKind.MappingStart,
        YamlTokenType.MappingEnd => YamlTokenKind.MappingEnd,
        YamlTokenType.SequenceStart => YamlTokenKind.SequenceStart,
        YamlTokenType.SequenceEnd => YamlTokenKind.SequenceEnd,
        _ => YamlTokenKind.None,
    };

    /// <summary>The byte offset of the current token in the UTF-8 text.</summary>
    public readonly int Offset => Start;

    internal readonly ReadOnlySpan<byte> Source => _src;

    internal readonly YamlSerializerOptions Options => _options;

    internal readonly bool ExpandsAliases => _expand;

    internal readonly int Depth => _depth;

    /// <summary>Creates a binding error that points at the current token.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>The exception, ready to throw.</returns>
    public readonly YamlException CreateException(string message) =>
        YamlException.At(YamlErrorCategory.Binding, message, _src, Start);

    internal readonly YamlException Error(YamlErrorCategory category, string message) =>
        YamlException.At(category, message, _src, Start);

    /// <summary>Returns pooled buffers. The serializer calls it; so must anyone who creates a reader.</summary>
    internal void Dispose()
    {
        _r.Dispose();
        _book?.ReturnBuffers();
    }

    // ---- Documents ----

    // Moves to the first value of the next document. Returns false at the end of the stream.
    internal bool BeginDocument(bool synthesizeEmptyStream)
    {
        _book?.Reset();
        _depth = 0;
        _liveDepth = 0;
        _liveNodes = 0;
        _expNodes = 0;
        _expBytes = 0;
        _chargedNodes = 0;
        _chargedBytes = 0;
        bool sawDocument = false;
        while (_r.Read())
        {
            switch (_r.TokenType)
            {
                case YamlTokenType.DocumentStart:
                    sawDocument = true;
                    _started = true;
                    return Read();
                case YamlTokenType.StreamEnd:
                    if (synthesizeEmptyStream && !sawDocument && !_started)
                    {
                        // An empty stream reads as one empty document.
                        _started = true;
                        Type = YamlTokenType.Scalar;
                        Style = ScalarStyle.Plain;
                        Tag = TagClass.None;
                        Escaped = false;
                        ContentStart = ContentEnd = 0;
                        Start = End = 0;
                        return true;
                    }

                    Type = YamlTokenType.StreamEnd;
                    return false;
                default:
                    break;
            }
        }

        Type = YamlTokenType.StreamEnd;
        return false;
    }

    // After the root value: reads up to the end of the document.
    internal void EndDocument()
    {
        if (Type == YamlTokenType.DocumentEnd)
        {
            return;
        }

        if (Read() || Type != YamlTokenType.DocumentEnd && Type != YamlTokenType.StreamEnd)
        {
            throw Error(YamlErrorCategory.Syntax, "The document holds more than one value.");
        }
    }

    // After the first document: the rest of the stream must be well formed and empty of documents.
    internal void EndStreamAfterSingleDocument()
    {
        while (_r.Read())
        {
            if (_r.TokenType == YamlTokenType.DocumentStart)
            {
                Start = _r.Start;
                throw Error(YamlErrorCategory.Syntax, "The stream holds more than one document; read them with DeserializeAll.");
            }
        }
    }

    // ---- Events ----

    /// <summary>Moves to the next token of the document.</summary>
    /// <returns><see langword="false"/> at the end of the document.</returns>
    public bool Read()
    {
        if (_book is { FrameCount: > 0 } && ReadReplay())
        {
            return true;
        }

        return ReadLive();
    }

    private bool ReadReplay()
    {
        AnchorBook book = _book!;
        while (book.FrameCount > 0)
        {
            ref ReplayFrame f = ref book.Frames[book.FrameCount - 1];
            if (f.Pos >= f.End)
            {
                book.FrameCount--;
                continue;
            }

            ref TapeEvent e = ref book.Tape[f.Pos++];
            if (_countReplayed && book.FrameCount == 1)
            {
                CountReplayed(in e);
            }

            if (e.Type == YamlTokenType.Alias)
            {
                // The charge for this expansion was taken with the outer alias.
                PushReplay(e.Alias);
                continue;
            }

            Type = e.Type;
            Style = e.Style;
            Tag = e.Tag;
            Escaped = e.Escaped;
            Chomping = e.Chomping;
            ContentStart = e.ContentStart;
            ContentEnd = e.ContentEnd;
            BlockIndent = e.BlockIndent;
            Start = e.Start;
            End = e.End;
            NodeEntry = -1;
            Track(e.Type);
            return true;
        }

        return false;
    }

    private void Track(YamlTokenType type)
    {
        switch (type)
        {
            case YamlTokenType.MappingStart:
            case YamlTokenType.SequenceStart:
                if (++_depth > _limits.MaxDepth)
                {
                    throw Error(YamlErrorCategory.Limit, "The nesting is deeper than the configured limit.");
                }

                break;
            case YamlTokenType.MappingEnd:
            case YamlTokenType.SequenceEnd:
                _depth--;
                break;
            default:
                break;
        }
    }

    private void PushReplay(int entry)
    {
        AnchorBook book = _book!;
        ref AnchorEntry e = ref book.Entries[entry];
        if (book.FrameCount == book.Frames.Length)
        {
            Array.Resize(ref book.Frames, book.Frames.Length * 2);
        }

        book.Frames[book.FrameCount++] = new ReplayFrame { Start = e.TapeStart, Pos = e.TapeStart, End = e.TapeEnd, DepthBefore = _depth };
    }

    private bool ReadLive()
    {
        while (true)
        {
            if (!_r.Read())
            {
                Type = YamlTokenType.StreamEnd;
                return false;
            }

            YamlTokenType t = _r.TokenType;
            switch (t)
            {
                case YamlTokenType.DocumentEnd:
                case YamlTokenType.StreamEnd:
                    Type = t;
                    return false;
                case YamlTokenType.DocumentStart:
                case YamlTokenType.StreamStart:
                    continue;
                case YamlTokenType.Alias:
                    if (LoadAlias())
                    {
                        return true;
                    }

                    continue;
                default:
                    LoadLive(t);
                    return true;
            }
        }
    }

    private void LoadLive(YamlTokenType t)
    {
        Type = t;
        Start = _r.Start;
        End = _r.End;
        NodeEntry = -1;
        bool anchored = _r.HasAnchor;
        bool record = _expand && (anchored || (_book is { OpenCount: > 0 }));

        switch (t)
        {
            case YamlTokenType.Scalar:
                Style = _r.ScalarStyle;
                Escaped = _r.ValueIsEscapedOrFolded;
                _r.GetScalarDetails(out ContentStart, out ContentEnd, out BlockIndent, out Chomping);
                Tag = _r.HasTag ? ClassifyTag(_r.GetResolvedTag()) : TagClass.None;
                _liveNodes++;
                _expNodes = Sat(_expNodes + 1);
                _expBytes = Sat(_expBytes + (ContentEnd - ContentStart) + 1);
                if (anchored)
                {
                    int entry = NewEntry();
                    NodeEntry = entry;
                    _book!.Entries[entry].IsScalar = true;

                    // An anchored scalar is always kept: an alias used as a key reads its text from here.
                    Record();
                    FinishEntry(entry, 1, (ContentEnd - ContentStart) + 1);
                }
                else if (record)
                {
                    Record();
                }

                break;
            case YamlTokenType.MappingStart:
            case YamlTokenType.SequenceStart:
                Tag = _r.HasTag ? ClassifyTag(_r.GetResolvedTag()) : TagClass.None;
                Track(t);
                _liveNodes++;
                if (anchored)
                {
                    int entry = NewEntry();
                    NodeEntry = entry;
                    AnchorBook book = _book!;
                    book.Entries[entry].TapeStart = book.TapeCount;
                    if (book.OpenCount == book.Open.Length)
                    {
                        Array.Resize(ref book.Open, book.Open.Length * 2);
                    }

                    book.Open[book.OpenCount++] = new OpenAnchor { Entry = entry, LiveDepth = _liveDepth, NodesAt = _expNodes, BytesAt = _expBytes };
                }

                if (record)
                {
                    Record();
                }

                _liveDepth++;
                _expNodes = Sat(_expNodes + 1);
                _expBytes = Sat(_expBytes + 1);
                break;
            default:
                // MappingEnd or SequenceEnd.
                Track(t);
                if (record)
                {
                    Record();
                }

                _liveDepth--;
                if (_book is { OpenCount: > 0 } b && b.Open[b.OpenCount - 1].LiveDepth == _liveDepth)
                {
                    OpenAnchor open = b.Open[--b.OpenCount];
                    FinishEntry(open.Entry, _expNodes - open.NodesAt, _expBytes - open.BytesAt);
                }

                break;
        }
    }

    private bool LoadAlias()
    {
        _liveNodes++;
        Start = _r.Start;
        End = _r.End;
        ReadOnlySpan<byte> name = _r.Anchor;
        int entry = _book is null ? -1 : _book.Find(_src, name, AnchorBook.HashOf(name));
        if (entry < 0)
        {
            throw Error(YamlErrorCategory.Syntax, "The alias refers to an anchor that is not defined before it.");
        }

        AnchorBook book = _book!;
        ref AnchorEntry e = ref book.Entries[entry];
        _chargedNodes = Sat(_chargedNodes + e.Nodes);
        _chargedBytes = Sat(_chargedBytes + e.Bytes);
        if (_chargedNodes > _limits.AliasNodeBudget(_liveNodes) || _chargedBytes > _limits.AliasByteBudget(_src.Length))
        {
            throw Error(YamlErrorCategory.Limit, "Expanding the aliases exceeds the configured limit.");
        }

        _expNodes = Sat(_expNodes + e.Nodes);
        _expBytes = Sat(_expBytes + e.Bytes);
        if (!_expand)
        {
            Type = YamlTokenType.Alias;
            AliasEntry = entry;
            NodeEntry = -1;
            return true;
        }

        if (book.OpenCount > 0)
        {
            Type = YamlTokenType.Alias;
            AliasEntry = entry;
            Record();
        }

        PushReplay(entry);
        return ReadReplay();
    }

    private int NewEntry()
    {
        AnchorBook book = _book ??= new AnchorBook();
        if (book.Tape.Length == 0)
        {
            book.Tape = ArrayPool<TapeEvent>.Shared.Rent(256);
        }

        if (book.Count == book.Entries.Length)
        {
            Array.Resize(ref book.Entries, book.Entries.Length * 2);
        }

        ReadOnlySpan<byte> name = _r.Anchor;
        int idx = book.Count++;
        book.Entries[idx] = new AnchorEntry
        {
            NameStart = _r.AnchorStart,
            NameLength = name.Length,
            Hash = AnchorBook.HashOf(name),
            TapeStart = book.TapeCount,
            TapeEnd = book.TapeCount,
        };
        if (book.Values is not null && book.Values.Length < book.Entries.Length)
        {
            Array.Resize(ref book.Values, book.Entries.Length);
        }

        return idx;
    }

    // An anchor takes effect when its node is complete, so a node cannot refer to itself.
    private void FinishEntry(int entry, long nodes, long bytes)
    {
        AnchorBook book = _book!;
        ref AnchorEntry e = ref book.Entries[entry];
        e.TapeEnd = book.TapeCount;
        e.Nodes = Math.Min(nodes, Cap);
        e.Bytes = Math.Min(bytes, Cap);
        book.Publish(_src, entry);
    }

    private void Record()
    {
        AnchorBook book = _book!;
        if (book.TapeCount == book.Tape.Length)
        {
            TapeEvent[] bigger = ArrayPool<TapeEvent>.Shared.Rent(book.Tape.Length * 2);
            Array.Copy(book.Tape, bigger, book.TapeCount);
            ArrayPool<TapeEvent>.Shared.Return(book.Tape);
            book.Tape = bigger;
        }

        book.Tape[book.TapeCount++] = new TapeEvent
        {
            Type = Type,
            Style = Style,
            Tag = Tag,
            Chomping = Chomping,
            Escaped = Escaped,
            Start = Start,
            End = End,
            ContentStart = ContentStart,
            ContentEnd = ContentEnd,
            BlockIndent = BlockIndent,
            Alias = AliasEntry,
        };
    }

    private static long Sat(long v) => v > Cap ? Cap : v;

    private static TagClass ClassifyTag(string? tag) => tag switch
    {
        null => TagClass.None,
        "!" => TagClass.Str,
        "tag:yaml.org,2002:str" => TagClass.Str,
        "tag:yaml.org,2002:int" => TagClass.Int,
        "tag:yaml.org,2002:float" => TagClass.Float,
        "tag:yaml.org,2002:bool" => TagClass.Bool,
        "tag:yaml.org,2002:null" => TagClass.Null,
        "tag:yaml.org,2002:binary" => TagClass.Binary,
        "tag:yaml.org,2002:map" => TagClass.Map,
        "tag:yaml.org,2002:seq" => TagClass.Seq,
        _ => TagClass.Unknown,
    };

    // The generic loader keeps the value it built for an anchored node, so an alias shares it.
    internal void SetAnchorValue(int entry, object? value)
    {
        AnchorBook book = _book!;
        book.Values ??= new object?[book.Entries.Length];
        if (book.Values.Length < book.Entries.Length)
        {
            Array.Resize(ref book.Values, book.Entries.Length);
        }

        book.Values[entry] = value;
    }

    internal readonly object? GetAnchorValue(int entry) => _book!.Values?[entry];

    // The text of an anchored scalar, or null when the anchor holds a collection.
    internal readonly string? GetAnchorScalarText(int entry)
    {
        AnchorBook book = _book!;
        ref AnchorEntry e = ref book.Entries[entry];
        if (!e.IsScalar)
        {
            return null;
        }

        ref TapeEvent t = ref book.Tape[e.TapeStart];
        ReadOnlySpan<byte> raw = _src.Slice(t.ContentStart, t.ContentEnd - t.ContentStart);
        if (!t.Escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[ScalarDecoder.MaxDecodedLength(raw.Length)];
        int n = ScalarDecoder.Decode(raw, t.Style, t.BlockIndent, t.Chomping, buffer);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    /// <summary>Skips the value the reader is on, including all of a collection.</summary>
    public void Skip()
    {
        if (_book is { FrameCount: > 0 } b)
        {
            ref ReplayFrame f = ref b.Frames[b.FrameCount - 1];
            if (f.Pos == f.Start + 1)
            {
                // The value is an alias that was just entered: drop it without walking it.
                _depth = f.DepthBefore;
                b.FrameCount--;
                return;
            }
        }

        if (Type is YamlTokenType.MappingStart or YamlTokenType.SequenceStart)
        {
            int open = 1;
            while (open > 0)
            {
                if (!Read())
                {
                    throw Error(YamlErrorCategory.Syntax, "The collection is not closed.");
                }

                if (Type is YamlTokenType.MappingStart or YamlTokenType.SequenceStart)
                {
                    open++;
                }
                else if (Type is YamlTokenType.MappingEnd or YamlTokenType.SequenceEnd)
                {
                    open--;
                }
            }
        }
    }
}
