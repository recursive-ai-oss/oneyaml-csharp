using System;
using System.Buffers;
using System.Collections.Generic;
using OneYaml.Parsing;

namespace OneYaml;

// One delivered event, kept so it can be delivered again after the reader looked ahead.
internal struct LookEvent
{
    public YamlTokenType Type;
    public ScalarStyle Style;
    public TagClass Tag;
    public Chomping Chomping;
    public bool Escaped;
    public int ContentStart;
    public int ContentEnd;
    public int BlockIndent;
    public int Start;
    public int End;
    public int AliasEntry;
    public int NodeEntry;
    public int TagRef;
}

// The events a reader has passed while looking ahead, in the order they were delivered. The cursor says which
// of them the next Read hands out; past the last one the reader reads live and appends. It exists only while a
// converter needs to see ahead of its position, so a document that never does pays for none of it.
internal sealed class LookBuffer
{
    public LookEvent[] Events = ArrayPool<LookEvent>.Shared.Rent(64);
    public int Count;
    public int Pos;

    // Looks ahead that have not been rewound yet; the log must be kept while there is one.
    public int Active;

    public void Append(in LookEvent e)
    {
        if (Count == Events.Length)
        {
            LookEvent[] bigger = ArrayPool<LookEvent>.Shared.Rent(Events.Length * 2);
            Array.Copy(Events, bigger, Count);
            ArrayPool<LookEvent>.Shared.Return(Events);
            Events = bigger;
        }

        Events[Count++] = e;
    }

    public void Release()
    {
        if (Events.Length != 0)
        {
            ArrayPool<LookEvent>.Shared.Return(Events);
            Events = [];
        }
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

    private bool ReadLooked()
    {
        LookBuffer look = _look!;
        if (look.Pos < look.Count)
        {
            Deliver(in look.Events[look.Pos]);
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

        look.Append(Snapshot());
        _curIndex = look.Count - 1;
        look.Pos = look.Count;
        return true;
    }

    // The current event, as it is now, for the log.
    private LookEvent Snapshot()
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

        return new LookEvent
        {
            Type = Type,
            Style = Style,
            Tag = TagKind,
            Chomping = Chomping,
            Escaped = Escaped,
            ContentStart = ContentStart,
            ContentEnd = ContentEnd,
            BlockIndent = BlockIndent,
            Start = Start,
            End = End,
            AliasEntry = AliasEntry,
            NodeEntry = NodeEntry,
            TagRef = tagRef,
        };
    }

    private void Deliver(in LookEvent e)
    {
        // The tag of a logged event always comes from the tag records, whichever way the event first arrived.
        _replayed = true;
        _tagRef = e.TagRef;
        Type = e.Type;
        Style = e.Style;
        TagKind = e.Tag;
        Escaped = e.Escaped;
        Chomping = e.Chomping;
        ContentStart = e.ContentStart;
        ContentEnd = e.ContentEnd;
        BlockIndent = e.BlockIndent;
        Start = e.Start;
        End = e.End;
        AliasEntry = e.AliasEntry;
        NodeEntry = e.NodeEntry;
        Track(e.Type);
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
            look.Append(Snapshot());
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
    private bool StepAhead()
    {
        _chargedNodes = Sat(_chargedNodes + 1);
        if (_chargedNodes > _limits.AliasNodeBudget(_liveNodes))
        {
            throw Error(YamlErrorCategory.Limit, $"Looking ahead for the key of a derived type exceeds the budget set by YamlLimits (AliasNodeFactor {_limits.AliasNodeFactor}, AliasNodeFloor {_limits.AliasNodeFloor}).");
        }

        return Read();
    }

    /// <summary>Puts the reader back on the event <see cref="LookForKey"/> started from; the events after it are delivered again.</summary>
    internal void RewindLook()
    {
        LookBuffer look = _look!;
        look.Active--;
        look.Pos = _lookOrigin + 1;
        _curIndex = _lookOrigin;
        _depth = _lookOriginDepth;
        LookEvent e = look.Events[_lookOrigin];
        _replayed = true;
        _tagRef = e.TagRef;
        Type = e.Type;
        Style = e.Style;
        TagKind = e.Tag;
        Escaped = e.Escaped;
        Chomping = e.Chomping;
        ContentStart = e.ContentStart;
        ContentEnd = e.ContentEnd;
        BlockIndent = e.BlockIndent;
        Start = e.Start;
        End = e.End;
        AliasEntry = e.AliasEntry;
        NodeEntry = e.NodeEntry;
    }

    private void ReleaseLook()
    {
        _look?.Release();
        _look = null;
    }
}
