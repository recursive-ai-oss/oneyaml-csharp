using System;
using System.Buffers;
using System.Collections.Generic;
using OneYaml.Parsing;

namespace OneYaml;

// One delivered event, kept so it can be delivered again after the reader looked ahead. It is packed to 24 bytes:
// a log holds every event of the values a look-ahead passes, and a large value before the key must not cost much per event.
// The fields that are zero for nearly every event (alias entry, anchor entry, tag record) live in a side list.
internal struct LookEvent
{
    public int Start;
    public int End;
    public int ContentStart;
    public int ContentEnd;

    // The indent of a block scalar, or, when the event has extras, the index of its record in the side list.
    public int Indent;
    public YamlTokenType Type;
    public ScalarStyle Style;
    public TagClass Tag;
    public byte Flags;

    public const byte EscapedFlag = 1;
    public const byte ExtrasFlag = 2;

    // The event came out of an alias expansion, whose nodes were charged to the budget when the alias was read.
    public const byte FromAliasFlag = 4;
    public const int ChompingShift = 4;
}

// What a few events have beyond the packed fields.
internal struct LookExtra
{
    public int Indent;
    public int AliasEntry;
    public int NodeEntry;
    public int TagRef;
}

// The events a reader has passed while looking ahead, in the order they were delivered. The cursor says which
// of them the next Read hands out; past the last one the reader reads live and appends. It exists only while a
// converter needs to see ahead of its position, so a document that never does pays for none of it.
// The log grows in fixed chunks that are never copied, and only the first chunk comes from the pool, so a long
// look-ahead neither doubles its memory while growing nor leaves large arrays behind in the shared pool.
internal sealed class LookBuffer
{
    private const int ChunkShift = 10;
    private const int ChunkSize = 1 << ChunkShift;

    private LookEvent[][] _chunks = new LookEvent[4][];
    private int _chunkCount;
    private LookEvent[]? _pooled;
    private LookExtra[] _extras = [];
    private int _extraCount;

    public int Count;
    public int Pos;

    // Looks ahead that have not been rewound yet; the log must be kept while there is one.
    public int Active;

    public LookBuffer()
    {
        _pooled = ArrayPool<LookEvent>.Shared.Rent(ChunkSize);
        _chunks[0] = _pooled;
        _chunkCount = 1;
    }

    public ref LookEvent At(int index) => ref _chunks[index >> ChunkShift][index & (ChunkSize - 1)];

    public ref readonly LookExtra ExtraAt(int index) => ref _extras[index];

    public int AddExtra(in LookExtra extra)
    {
        if (_extraCount == _extras.Length)
        {
            Array.Resize(ref _extras, Math.Max(16, _extras.Length * 2));
        }

        _extras[_extraCount] = extra;
        return _extraCount++;
    }

    public void Append(in LookEvent e)
    {
        int chunk = Count >> ChunkShift;
        if (chunk == _chunkCount)
        {
            if (_chunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunks.Length * 2);
            }

            _chunks[_chunkCount++] = new LookEvent[ChunkSize];
        }

        _chunks[chunk][Count & (ChunkSize - 1)] = e;
        Count++;
    }

    public void Release()
    {
        if (_pooled is not null)
        {
            ArrayPool<LookEvent>.Shared.Return(_pooled);
            _pooled = null;
        }

        _chunks = [];
        _chunkCount = 0;
        _extras = [];
        _extraCount = 0;
    }
}

public ref partial struct Utf8YamlReader
{
    private LookBuffer? _look;

    // Where, in the log, the event is that the last look ahead started from, and how deep the reader was there.
    private int _lookOrigin;
    private int _lookOriginDepth;

    // The log index of the current event; meaningful only while a log exists.
    private int _curIndex;

    // Whether the current event came out of an alias expansion; kept while a log exists.
    private bool _curFromAlias;

    private bool ReadLooked()
    {
        LookBuffer look = _look!;
        if (look.Pos < look.Count)
        {
            Deliver(look, look.Pos);
            _curIndex = look.Pos++;
            return true;
        }

        if (look.Active == 0)
        {
            // Everything logged was delivered and nobody can go back: drop the log.
            look.Release();
            _look = null;
            return Read();
        }

        if (!ReadCore())
        {
            return false;
        }

        look.Append(Snapshot(look));
        _curIndex = look.Count - 1;
        look.Pos = look.Count;
        return true;
    }

    // The current event, as it is now, for the log.
    private LookEvent Snapshot(LookBuffer look)
    {
        int tagRef = 0;
        if (Type is YamlTokenType.Scalar or YamlTokenType.MappingStart or YamlTokenType.SequenceStart)
        {
            if (_replayed)
            {
                tagRef = _tagRef;
            }
            else if (_r.HasTag)
            {
                tagRef = AddTag(_book ??= new AnchorBook());
            }
        }

        int aliasEntry = Type == YamlTokenType.Alias ? AliasEntry : 0;
        byte flags = (byte)((int)Chomping << LookEvent.ChompingShift);
        if (Escaped)
        {
            flags |= LookEvent.EscapedFlag;
        }

        // Counted after the read, so the frame of an expansion is still on the stack for its last event.
        if (_book is { } book && book.FrameCount > (_countReplayed ? 1 : 0))
        {
            flags |= LookEvent.FromAliasFlag;
        }

        _curFromAlias = (flags & LookEvent.FromAliasFlag) != 0;
        int indent = BlockIndent;
        if (aliasEntry != 0 || NodeEntry != -1 || tagRef != 0)
        {
            flags |= LookEvent.ExtrasFlag;
            indent = look.AddExtra(new LookExtra { Indent = BlockIndent, AliasEntry = aliasEntry, NodeEntry = NodeEntry, TagRef = tagRef });
        }

        return new LookEvent
        {
            Type = Type,
            Style = Style,
            Tag = TagKind,
            Flags = flags,
            ContentStart = ContentStart,
            ContentEnd = ContentEnd,
            Indent = indent,
            Start = Start,
            End = End,
        };
    }

    // Puts a logged event back as the current one. Depth is the caller's business.
    private void Restore(LookBuffer look, int index)
    {
        ref LookEvent e = ref look.At(index);
        // The tag of a logged event always comes from the tag records, whichever way the event first arrived.
        _replayed = true;
        Type = e.Type;
        Style = e.Style;
        TagKind = e.Tag;
        Escaped = (e.Flags & LookEvent.EscapedFlag) != 0;
        Chomping = (Chomping)(e.Flags >> LookEvent.ChompingShift);
        _curFromAlias = (e.Flags & LookEvent.FromAliasFlag) != 0;
        ContentStart = e.ContentStart;
        ContentEnd = e.ContentEnd;
        Start = e.Start;
        End = e.End;
        if ((e.Flags & LookEvent.ExtrasFlag) != 0)
        {
            ref readonly LookExtra x = ref look.ExtraAt(e.Indent);
            _tagRef = x.TagRef;
            BlockIndent = x.Indent;
            AliasEntry = x.AliasEntry;
            NodeEntry = x.NodeEntry;
        }
        else
        {
            _tagRef = 0;
            BlockIndent = e.Indent;
            AliasEntry = 0;
            NodeEntry = -1;
        }
    }

    private void Deliver(LookBuffer look, int index)
    {
        Restore(look, index);
        Track(Type);
    }

    /// <summary>
    /// Looks ahead in the mapping whose first key the reader is on, for a scalar key with this text.
    /// Every event it passes is kept, so <see cref="RewindLook"/> can put the reader back where it started.
    /// </summary>
    /// <param name="key">The key's UTF-8 text.</param>
    /// <returns><see langword="true"/> with the reader on the key's value; <see langword="false"/> when the mapping ends without it.</returns>
    internal bool LookForKey(byte[] key)
    {
        LookBuffer? look = _look;
        if (look is null)
        {
            look = _look = new LookBuffer();
            look.Append(Snapshot(look));
            look.Pos = 1;
            _curIndex = 0;
        }

        look.Active++;
        _lookOrigin = _curIndex;
        _lookOriginDepth = _depth;

        // The first key is the caller's to judge; from here each loop turn passes one entry.
        while (true)
        {
            SkipAhead();
            if (!StepAhead())
            {
                throw Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            if (Type == YamlTokenType.MappingEnd)
            {
                return false;
            }

            if (Type == YamlTokenType.Scalar && ConverterGuards.KeyEquals(ref this, key))
            {
                if (!StepAhead())
                {
                    throw Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
                }

                return true;
            }
        }
    }

    // Passes the current event and, when it starts a collection, everything inside it; then the value of a
    // key if the event was a key. Used on the first event of an entry: key, then its value.
    private void SkipAhead()
    {
        PassValue();
        if (!StepAhead())
        {
            throw Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        PassValue();
    }

    // The current event is a scalar, or the start of a collection to be passed whole.
    private void PassValue()
    {
        if (Type is not (YamlTokenType.MappingStart or YamlTokenType.SequenceStart))
        {
            return;
        }

        int open = 1;
        while (open > 0)
        {
            if (!StepAhead())
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

    // One event further while looking ahead. Each step is charged to the same budget as an alias expansion, so a
    // mapping that is looked into again and again by nested derived types cannot cost more than the limits allow.
    // Not charged: the end of a collection, which costs nothing the start did not, and a node out of an alias
    // expansion on its first, live pass, which the alias already paid for. An event handed out again from the log
    // is charged every time, alias or not, so nested look-aheads over one expansion stay within the budget.
    private bool StepAhead()
    {
        bool fromLog = _look is { } log && log.Pos < log.Count;
        if (!Read())
        {
            return false;
        }

        if (Type is not (YamlTokenType.MappingEnd or YamlTokenType.SequenceEnd) && (fromLog || !_curFromAlias))
        {
            _chargedNodes = Sat(_chargedNodes + 1);
            if (_chargedNodes > _limits.AliasNodeBudget(_liveNodes))
            {
                throw Error(YamlErrorCategory.Limit, _limits.AliasBudgetMessage("Looking ahead for the key of a derived type")).AsAliasBudget();
            }
        }

        return true;
    }

    /// <summary>Puts the reader back on the event <see cref="LookForKey"/> started from; the events after it are delivered again.</summary>
    internal void RewindLook()
    {
        LookBuffer look = _look!;
        look.Active--;
        look.Pos = _lookOrigin + 1;
        _curIndex = _lookOrigin;
        _depth = _lookOriginDepth;
        Restore(look, _lookOrigin);
    }

    private void ReleaseLook()
    {
        _look?.Release();
        _look = null;
    }
}
