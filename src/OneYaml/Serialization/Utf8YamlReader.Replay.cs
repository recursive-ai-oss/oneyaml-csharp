using System;
using OneYaml.Parsing;

namespace OneYaml;

// A way to hand the binder a value that is already parsed: the events come from a tape the
// caller recorded (a lossless document replays its tree this way) instead of from the text.
public ref partial struct Utf8YamlReader
{
    // Set when the root tape stands in for a whole parse, so its events are counted and its aliases charged
    // the way the live reader does it.
    private bool _countReplayed;

    // Classifies a tag whose handle is resolved, as the live reader does for every tagged node.
    internal static TagClass ClassifyResolvedTag(string? resolvedTag) => ClassifyTag(resolvedTag);

    // Makes a reader that delivers the first rootCount events of the book's tape, and the anchored
    // segments after them when an alias event names one. The reader owns the book's tape afterwards
    // and returns it to the pool in Dispose. Positions and error messages refer to source, the text the
    // events were recorded from.
    internal Utf8YamlReader(ReadOnlySpan<byte> source, YamlSerializerOptions options, AnchorBook book, int rootCount, bool expandAliases = true)
    {
        _options = options;
        _limits = options.Limits;
        _src = source;
        _expand = expandAliases;
        _r = new YamlReader(default, new YamlReaderOptions { Limits = _limits });
        NodeEntry = -1;
        book.Frames[0] = new ReplayFrame { Start = 0, Pos = 0, End = rootCount, DepthBefore = 0 };
        book.FrameCount = 1;
        _book = book;
        _started = true;
        _countReplayed = true;
    }

    // Moves to the first event, as a converter expects for the single value of a document.
    internal void BeginReplay() => Read();

    // After the value: nothing may follow it.
    internal void EndReplay()
    {
        if (Read())
        {
            throw Error(YamlErrorCategory.Syntax, "The value holds more than one node.");
        }
    }

    private void CountReplayed(in TapeEvent e)
    {
        switch (e.Type)
        {
            case YamlTokenType.Alias:
                {
                    _liveNodes++;
                    Start = e.Start;
                    End = e.End;
                    ref AnchorEntry a = ref _book!.Entries[e.Alias];
                    _chargedNodes = Sat(_chargedNodes + a.Nodes);
                    _chargedBytes = Sat(_chargedBytes + a.Bytes);
                    if (_chargedNodes > _limits.AliasNodeBudget(_liveNodes) || _chargedBytes > _limits.AliasByteBudget(_src.Length))
                    {
                        throw Error(YamlErrorCategory.Limit, $"Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor {_limits.AliasNodeFactor}, AliasNodeFloor {_limits.AliasNodeFloor}, AliasByteFactor {_limits.AliasByteFactor}, AliasByteFloor {_limits.AliasByteFloor}).");
                    }

                    break;
                }

            case YamlTokenType.Scalar:
            case YamlTokenType.MappingStart:
            case YamlTokenType.SequenceStart:
                _liveNodes++;
                break;
            default:
                break;
        }
    }
}
