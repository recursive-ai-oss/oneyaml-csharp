using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OneYaml.Parsing;

namespace OneYaml;

// Reads any value as null, bool, long (else BigInteger), double, string, List<object?> or
// Dictionary<string, object?>. It walks the input with an explicit stack, so nesting costs heap,
// never call depth.
internal sealed class GenericConverter : YamlConverter<object>
{
    public override bool HandleNull => true;

    public override void Write(Utf8YamlEmitter writer, object value, YamlSerializerOptions options) =>
        GenericWriter.Write(writer, value, options);

    public override object? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        GenericLoader.Load(ref reader);
}

internal struct GenericFrame
{
    // The list, or the dictionary once it exists. A small mapping holds its entries in the staging area until it
    // ends, and then gets a dictionary of exactly the right size.
    public object? Container;
    public int Base;
    public string? Key;
    public int KeyOffset;
    public int Entry;
    public bool IsMap;
    public bool HaveKey;
    public bool KeyIsMerge;

    // Sets that few mappings need (merge keys, keys that are not plain text) live apart, so that a frame stays small.
    public FrameSets? Sets;
}

internal sealed class FrameSets
{
    public HashSet<string>? Merged;
    public HashSet<string>? Identities;
}

// The open collections of a load. The first levels sit in the caller's stack space; deeper levels go into
// fixed-size chunks that are rented one at a time and never copied, so a deep document takes exactly the
// room it needs and not the sum of every doubling step on the way.
internal ref struct FrameStack
{
    private const int ChunkShift = 4;
    private const int ChunkSize = 1 << ChunkShift;

    private readonly Span<GenericFrame> _first;
    private GenericFrame[]?[]? _chunks;
    private int _chunkCount;

    public FrameStack(Span<GenericFrame> first)
    {
        _first = first;
        _chunks = null;
        _chunkCount = 0;
        Count = 0;
    }

    public int Count { get; private set; }

    public ref GenericFrame Top => ref At(Count - 1);

    private ref GenericFrame At(int i)
    {
        if ((uint)i < (uint)_first.Length)
        {
            return ref _first[i];
        }

        int j = i - _first.Length;
        return ref _chunks![j >> ChunkShift]![j & (ChunkSize - 1)];
    }

    public ref GenericFrame Push()
    {
        if (Count >= _first.Length + (_chunkCount << ChunkShift))
        {
            if (_chunks is null)
            {
                // The directory is rented too, so a load deeper than the stack space takes nothing from the heap.
                _chunks = ArrayPool<GenericFrame[]?>.Shared.Rent(8);
            }
            else if (_chunkCount == _chunks.Length)
            {
                GenericFrame[]?[] bigger = ArrayPool<GenericFrame[]?>.Shared.Rent(_chunks.Length * 2);
                Array.Copy(_chunks, bigger, _chunkCount);
                ArrayPool<GenericFrame[]?>.Shared.Return(_chunks, clearArray: true);
                _chunks = bigger;
            }

            _chunks[_chunkCount++] = ArrayPool<GenericFrame>.Shared.Rent(ChunkSize);
        }

        return ref At(Count++);
    }

    public ref GenericFrame Pop() => ref At(--Count);

    public void Release()
    {
        for (int i = 0; i < _chunkCount; i++)
        {
            ArrayPool<GenericFrame>.Shared.Return(_chunks![i]!, clearArray: true);
            _chunks[i] = null;
        }

        _chunkCount = 0;
        if (_chunks is not null)
        {
            ArrayPool<GenericFrame[]?>.Shared.Return(_chunks, clearArray: true);
            _chunks = null;
        }
    }
}

// Room for the first levels of nesting on the stack, so that an ordinary document never rents a frame array.
[InlineArray(InlineFrameCount)]
internal struct InlineFrames
{
    public const int InlineFrameCount = 16;

    private GenericFrame _first;
}

// One finished entry of a mapping that is still being read.
internal struct StagedEntry
{
    public string Key;
    public object? Value;
    public int Offset;
}

[InlineArray(16)]
internal struct InlineStaged
{
    private StagedEntry _first;
}

// A stack of entries of the open mappings. The entries of the innermost mapping are on top.
internal ref struct StagedEntries
{
    private Span<StagedEntry> _items;
    private StagedEntry[]? _rented;

    public StagedEntries(Span<StagedEntry> initial)
    {
        _items = initial;
        _rented = null;
        Count = 0;
    }

    public int Count { get; private set; }

    public readonly ReadOnlySpan<StagedEntry> From(int start) => _items.Slice(start, Count - start);

    public void Push(string key, object? value, int offset)
    {
        if (Count == _items.Length)
        {
            StagedEntry[] bigger = ArrayPool<StagedEntry>.Shared.Rent(_items.Length * 2);
            _items[..Count].CopyTo(bigger);
            Release();
            _rented = bigger;
            _items = bigger;
        }

        _items[Count++] = new StagedEntry { Key = key, Value = value, Offset = offset };
    }

    public void Truncate(int start)
    {
        _items.Slice(start, Count - start).Clear();
        Count = start;
    }

    public void Release()
    {
        if (_rented is not null)
        {
            ArrayPool<StagedEntry>.Shared.Return(_rented, clearArray: true);
            _rented = null;
        }
    }
}

internal static class GenericLoader
{
    // A mapping with at most this many entries is built from the staging area; the dictionary sizes are primes, and 7 is the
    // second one, so a mapping of up to 7 entries gets a dictionary that is full, not one that was grown to fit.
    private const int StagedLimit = 7;

    public static object? Load(ref Utf8YamlReader reader)
    {
        InlineFrames inline = default;
        var stack = new FrameStack(inline);
        InlineStaged inlineStaged = default;
        var staged = new StagedEntries(inlineStaged);
        try
        {
            while (true)
            {
                object? value;
                int entry;
                bool expectKey = stack.Count > 0 && stack.Top.IsMap && !stack.Top.HaveKey;
                switch (reader.Type)
                {
                    case YamlTokenType.Scalar:
                        entry = reader.NodeEntry;
                        if (expectKey)
                        {
                            ref GenericFrame kf = ref stack.Top;
                            kf.KeyIsMerge = ConverterGuards.IsMergeKey(in reader);
                            kf.KeyOffset = reader.Start;
                            kf.Key = reader.GetSharedString();
                            kf.HaveKey = true;
                            if (reader.Style == ScalarStyle.Plain || reader.TagKind is not TagClass.None)
                            {
                                // Keys are judged after resolution: 1 and 0x1 are the same key.
                                string? identity = reader.KeyIdentity();
                                if (identity is not null && !((kf.Sets ??= new FrameSets()).Identities ??= []).Add(identity))
                                {
                                    throw reader.ErrorAtKey(YamlErrorCategory.DuplicateKey, "The key '{0}' appears twice in the mapping.", reader.Start);
                                }
                            }

                            if (entry >= 0 && !reader.ExpandsAliases)
                            {
                                reader.SetAnchorValue(entry, reader.ReadGenericScalar());
                            }

                            goto Next;
                        }

                        value = reader.ReadGenericScalar();
                        break;
                    case YamlTokenType.Alias:
                        if (expectKey)
                        {
                            ref GenericFrame af = ref stack.Top;
                            af.KeyIsMerge = false;
                            af.KeyOffset = reader.Start;
                            af.Key = reader.GetAnchorScalarText(reader.AliasEntry)
                                ?? throw reader.CreateException("This mapping key is a collection; read documents with complex keys through YamlDocument.");
                            af.HaveKey = true;
                            goto Next;
                        }

                        value = reader.GetAnchorValue(reader.AliasEntry);
                        entry = -1;
                        break;
                    case YamlTokenType.MappingStart:
                    case YamlTokenType.SequenceStart:
                        {
                            if (expectKey)
                            {
                                throw reader.CreateException("This mapping key is a collection; read documents with complex keys through YamlDocument.");
                            }

                            bool map = reader.Type == YamlTokenType.MappingStart;
                            stack.Push() = new GenericFrame
                            {
                                Container = map ? null : new List<object?>(),
                                Base = staged.Count,
                                IsMap = map,
                                Entry = reader.NodeEntry,
                            };
                            goto Next;
                        }

                    case YamlTokenType.MappingEnd:
                    case YamlTokenType.SequenceEnd:
                        {
                            if (stack.Count == 0)
                            {
                                throw reader.Error(YamlErrorCategory.Syntax, "Unexpected end of a collection.");
                            }

                            ref GenericFrame top = ref stack.Pop();
                            value = top.IsMap ? FinishMap(ref top, ref staged) : top.Container;
                            entry = top.Entry;
                            top = default;
                            break;
                        }

                    default:
                        throw reader.Error(YamlErrorCategory.Syntax, "The document ended inside a value.");
                }

                if (entry >= 0 && !reader.ExpandsAliases)
                {
                    reader.SetAnchorValue(entry, value);
                }

                if (stack.Count == 0)
                {
                    return value;
                }

                Add(reader.Source, ref stack.Top, value, ref staged);

            Next:
                if (!reader.Read())
                {
                    throw reader.Error(YamlErrorCategory.Syntax, "The document ended inside a value.");
                }
            }
        }
        finally
        {
            stack.Release();
            staged.Release();
        }
    }

    private static void Add(ReadOnlySpan<byte> input, ref GenericFrame frame, object? value, ref StagedEntries staged)
    {
        if (!frame.IsMap)
        {
            ((List<object?>)frame.Container!).Add(value);
            return;
        }

        string key = frame.Key!;
        frame.HaveKey = false;
        frame.Key = null;
        if (frame.Container is null)
        {
            ReadOnlySpan<StagedEntry> held = staged.From(frame.Base);
            if (!frame.KeyIsMerge)
            {
                foreach (ref readonly StagedEntry e in held)
                {
                    if (string.Equals(e.Key, key, StringComparison.Ordinal))
                    {
                        throw YamlException.AtKey(YamlErrorCategory.DuplicateKey, "The key '{0}' appears twice in the mapping.", input, frame.KeyOffset);
                    }
                }

                if (held.Length < StagedLimit)
                {
                    staged.Push(key, value, frame.KeyOffset);
                    return;
                }
            }

            // A merge key or a larger mapping: from here on the entries go straight into a dictionary. Its size is the
            // next of the sizes a dictionary that grows by adding would have (3, 7, 17), so it keeps growing along the same
            // chain of primes.
            frame.Container = Materialise(held, held.Length < 3 ? 3 : held.Length < 7 ? 7 : 17);
            staged.Truncate(frame.Base);
        }

        var dict = (Dictionary<string, object?>)frame.Container;
        if (frame.KeyIsMerge)
        {
            Merge(input, dict, ref (frame.Sets ??= new FrameSets()).Merged, value, frame.KeyOffset);
        }
        else if (frame.Sets?.Merged is { } merged && merged.Remove(key))
        {
            dict[key] = value;
        }
        else if (!dict.TryAdd(key, value))
        {
            throw YamlException.AtKey(YamlErrorCategory.DuplicateKey, "The key '{0}' appears twice in the mapping.", input, frame.KeyOffset);
        }
    }

    private static Dictionary<string, object?> Materialise(ReadOnlySpan<StagedEntry> entries, int capacity)
    {
        var dict = capacity == 0 ? new Dictionary<string, object?>() : new Dictionary<string, object?>(capacity);
        foreach (ref readonly StagedEntry e in entries)
        {
            dict.Add(e.Key, e.Value);
        }

        return dict;
    }

    // The mapping ended: its dictionary exists already, or is made now from the staged entries.
    private static Dictionary<string, object?> FinishMap(ref GenericFrame frame, ref StagedEntries staged)
    {
        if (frame.Container is Dictionary<string, object?> made)
        {
            return made;
        }

        ReadOnlySpan<StagedEntry> held = staged.From(frame.Base);
        Dictionary<string, object?> dict = Materialise(held, held.Length);
        staged.Truncate(frame.Base);
        return dict;
    }

    // The merge key copies the entries of a mapping, or of each mapping in a sequence, that are not already there.
    private static void Merge(ReadOnlySpan<byte> input, Dictionary<string, object?> into, ref HashSet<string>? merged, object? source, int offset)
    {
        if (source is Dictionary<string, object?> one)
        {
            Copy(into, ref merged, one);
            return;
        }

        if (source is List<object?> many)
        {
            foreach (object? item in many)
            {
                if (item is not Dictionary<string, object?> d)
                {
                    throw YamlException.Located(YamlErrorCategory.Binding, "A merge key needs a mapping or a sequence of mappings.", input, offset, ErrorPath.Find(input, offset, atCollection: true));
                }

                Copy(into, ref merged, d);
            }

            return;
        }

        throw YamlException.Located(YamlErrorCategory.Binding, "A merge key needs a mapping or a sequence of mappings.", input, offset, ErrorPath.Find(input, offset, atCollection: true));
    }

    private static void Copy(Dictionary<string, object?> into, ref HashSet<string>? merged, Dictionary<string, object?> from)
    {
        foreach (KeyValuePair<string, object?> pair in from)
        {
            if (into.TryAdd(pair.Key, pair.Value))
            {
                (merged ??= []).Add(pair.Key);
            }
        }
    }
}
