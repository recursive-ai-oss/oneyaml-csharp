using System;
using OneYaml.Compat.YamlDotNet.Core;

namespace OneYaml.Compat.YamlDotNet.Internal;

internal enum EvKind : byte
{
    Scalar,
    SequenceStart,
    SequenceEnd,
    MappingStart,
    MappingEnd,
    Alias,
}

// One output event. The serializer fills a buffer of these first so that an object reached twice can be
// written as an alias to its first appearance, then the renderer turns the buffer into text.
internal struct Ev
{
    public EvKind Kind;
    public ScalarStyle Style;

    // The text is a .NET string value: the renderer decides whether it needs quotes. Otherwise the text is
    // already a YAML form (a number, a name, or a scalar a converter produced) and is written as it is when it can be.
    public bool IsString;
    public bool Flow;
    public bool NeedsAnchor;
    public string? Text;
    public string? Tag;
    public string? Anchor;

    // For an alias: the index of the event that carries the anchor, or -1 when the alias names an anchor by Text.
    public int Target;
}

internal sealed class EventBuffer
{
    private Ev[] _items = new Ev[64];

    public int Count { get; private set; }

    public ref Ev this[int index] => ref _items[index];

    public int Add(in Ev item)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, Count * 2);
        }

        _items[Count] = item;
        return Count++;
    }

    public void Clear() => Count = 0;
}
