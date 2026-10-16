using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using OneYaml.Parsing;
using OneYaml.Serialization.Metadata;

namespace OneYaml;

// A set of member indexes already seen. Up to 64 members need no allocation.
internal struct MemberSet
{
    private ulong _low;
    private ulong[]? _high;

    public MemberSet(int count) => _high = count > 64 ? new ulong[(count - 1) / 64] : null;

    public bool Contains(int index) =>
        index < 64 ? (_low & (1UL << index)) != 0 : (_high![(index / 64) - 1] & (1UL << (index % 64))) != 0;

    public void Add(int index)
    {
        if (index < 64)
        {
            _low |= 1UL << index;
        }
        else
        {
            _high![(index / 64) - 1] |= 1UL << (index % 64);
        }
    }
}

// The keys a mapping holds that no member takes. They are kept so that a repeated one is still an error:
// the same key twice is invalid whether or not the type has a member for it.
internal struct SkippedKeys
{
    private const int Inline = 16;

    private SkipKey[]? _items;
    private int _count;
    private HashSet<string>? _many;

    public static SkipKey Capture(ref Utf8YamlReader reader)
    {
        if (!reader.Escaped)
        {
            return new SkipKey(reader.ContentStart, reader.ContentEnd - reader.ContentStart, null);
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[128];
            return new SkipKey(0, 0, reader.Text(scratch, ref rented).ToArray());
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Returns false when the key was seen before.
    public bool Add(SkipKey key, ReadOnlySpan<byte> source)
    {
        if (_many is not null)
        {
            return _many.Add(Encoding.UTF8.GetString(key.Bytes(source)));
        }

        _items ??= ArrayPool<SkipKey>.Shared.Rent(Inline);
        ReadOnlySpan<byte> bytes = key.Bytes(source);
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Bytes(source).SequenceEqual(bytes))
            {
                return false;
            }
        }

        if (_count == Inline)
        {
            // Many unmapped keys: switch to a hash set so the check stays linear in the mapping size.
            _many = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _count; i++)
            {
                _many.Add(Encoding.UTF8.GetString(_items[i].Bytes(source)));
            }

            Release();
            return _many.Add(Encoding.UTF8.GetString(bytes));
        }

        _items[_count++] = key;
        return true;
    }

    public void Release()
    {
        if (_items is not null)
        {
            ArrayPool<SkipKey>.Shared.Return(_items, clearArray: true);
            _items = null;
            _count = 0;
        }
    }
}

internal readonly struct SkipKey(int start, int length, byte[]? decoded)
{
    public ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> source) => decoded ?? source.Slice(start, length);
}

// Binds a mapping to a class or struct through the members and constructor in its metadata.
internal sealed class ObjectConverter<T> : YamlConverter<T>
{
    private readonly YamlTypeInfo<T> _info;

    public ObjectConverter(YamlTypeInfo<T> info) => _info = info;

    public override void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options)
    {
        YamlTypeInfo<T> info = _info;
        info.EnsureConfigured();
        writer.Begin(isMapping: true, typeof(T).IsValueType ? null : value);
        foreach (YamlPropertyInfo<T> p in info.WriteProps)
        {
            p.Write(writer, value);
        }

        info.Extension?.WriteExtension(writer, value);
        writer.WriteEndMapping();
    }

    // Writes the value as the derived type of a polymorphic base whose discriminator is a key: the key comes first.
    internal void WriteDiscriminated(Utf8YamlEmitter writer, T value, string key, string discriminator)
    {
        YamlTypeInfo<T> info = _info;
        info.EnsureConfigured();
        writer.Begin(isMapping: true, typeof(T).IsValueType ? null : value);
        writer.WritePropertyName(key);
        writer.WriteStringValue(discriminator);
        foreach (YamlPropertyInfo<T> p in info.WriteProps)
        {
            p.Write(writer, value);
        }

        info.Extension?.WriteExtension(writer, value);
        writer.WriteEndMapping();
    }

    public override T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.MappingStart)
        {
            throw ConverterGuards.Expected(in reader, "a mapping", typeof(T));
        }

        // The common case is spelled out here, not shared with ReadBody: a frame fewer for every level of a nested document.
        ConverterGuards.EnsureStack(in reader);
        YamlTypeInfo<T> info = _info;
        if (!info.CanCreate)
        {
            throw NotCreatable(in reader);
        }

        info.EnsureConfigured();
        int start = reader.Start;
        var seen = new MemberSet(info.Props.Length);
        var merged = new MemberSet(info.Props.Length);
        if (info.UsesConstructor)
        {
            return ReadThroughConstructor(ref reader, options, info, start, ReadNormal, ref seen, ref merged);
        }

        T instance = info.CreateInstance();
        if (info.Extension is null)
        {
            ReadMembers(ref reader, options, ref instance, null, ref seen, ref merged, merging: false);
        }
        else
        {
            ReadMembersGeneral(ref reader, options, ref instance, null, ref seen, ref merged, ReadNormal);
        }

        if (info.RequiredCount > 0)
        {
            CheckRequired(in reader, start, ref seen, ref merged);
        }

        return instance;
    }

    // The mapping start is behind the reader, which is on the first key (or the end of an empty mapping). A polymorphic
    // base that is read as itself looked at the first key to see whether it names a derived type, and found it did not.
    internal T? ReadFromFirstKey(ref Utf8YamlReader reader, YamlSerializerOptions options, int start) =>
        ReadBody(ref reader, options, start, ReadResume);

    // The mapping start and the discriminator key are behind the reader, which is on the discriminator's value.
    internal T? ReadAfterDiscriminator(ref Utf8YamlReader reader, YamlSerializerOptions options, int start) =>
        ReadBody(ref reader, options, start, ReadNormal);

    private T? ReadBody(ref Utf8YamlReader reader, YamlSerializerOptions options, int start, int mode)
    {
        ConverterGuards.EnsureStack(in reader);
        YamlTypeInfo<T> info = _info;
        if (!info.CanCreate)
        {
            throw NotCreatable(in reader);
        }

        info.EnsureConfigured();
        var seen = new MemberSet(info.Props.Length);
        var merged = new MemberSet(info.Props.Length);
        if (info.UsesConstructor)
        {
            return ReadThroughConstructor(ref reader, options, info, start, mode, ref seen, ref merged);
        }

        T instance = info.CreateInstance();
        ReadMembersGeneral(ref reader, options, ref instance, null, ref seen, ref merged, mode);
        if (info.RequiredCount > 0)
        {
            CheckRequired(in reader, start, ref seen, ref merged);
        }

        return instance;
    }

    // Kept apart from Read so that the frame of the common case, which every level of a nested document passes through,
    // holds only what that case needs.
    private T? ReadThroughConstructor(ref Utf8YamlReader reader, YamlSerializerOptions options, YamlTypeInfo<T> info, int start, int mode, ref MemberSet seen, ref MemberSet merged)
    {
        object?[] slots = info.NewArguments();
        T instance = default!;
        ReadMembersGeneral(ref reader, options, ref instance, slots, ref seen, ref merged, mode);
        CheckRequired(in reader, start, ref seen, ref merged);
        instance = info.CreateWith(slots);
        if (info.Extension is { ParameterIndex: < 0 } extension && slots[extension.Slot] is { } collected)
        {
            extension.Assign(ref instance, collected);
        }

        foreach (YamlPropertyInfo<T> p in info.Props)
        {
            if (p.ParameterIndex < 0 && (seen.Contains(p.Index) || merged.Contains(p.Index)))
            {
                p.Assign(ref instance, slots[p.Slot]);
            }
        }

        return instance;
    }

    // What follows the members, out of line so that its locals are not in the frame that recurses.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T? Construct(in Utf8YamlReader reader, YamlTypeInfo<T> info, int start, object?[] slots, ref MemberSet seen, ref MemberSet merged)
    {
        foreach (YamlPropertyInfo<T> p in info.Props)
        {
            if (p.IsRequired && !seen.Contains(p.Index) && !merged.Contains(p.Index))
            {
                throw reader.ErrorAt(YamlErrorCategory.Binding, $"The required key '{p.Name}' is missing.", start);
            }
        }

        T instance = info.CreateWith(slots);
        foreach (YamlPropertyInfo<T> p in info.Props)
        {
            if (p.ParameterIndex < 0 && (seen.Contains(p.Index) || merged.Contains(p.Index)))
            {
                p.Assign(ref instance, slots[p.Slot]);
            }
        }

        return instance;
    }
    private const int ReadNormal = 0;
    private const int ReadMerging = 1;
    private const int ReadResume = 2;

    // The loop of the constructor path: it hands each value to its member and does nothing else.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ReadMembersBoxed(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[] slots,
        ref MemberSet seen,
        ref MemberSet merged)
    {
        var skipped = default(SkippedKeys);
        YamlPropertyInfo<T>? p;
        while ((p = NextBoxed(ref reader, options, ref instance, slots, ref seen, ref merged, ref skipped)) is not null)
        {
            p.ReadBoxed(ref reader, slots);
        }
    }

    // Moves to the value of the next key that a member takes and returns the member; null at the end of the mapping.
    // The same walk as ReadMembers makes, apart from the frame it leaves to the loop above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private YamlPropertyInfo<T>? NextBoxed(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[] slots,
        ref MemberSet seen,
        ref MemberSet merged,
        ref SkippedKeys skipped)
    {
        YamlTypeInfo<T> info = _info;
        while (reader.Read() && reader.Type != YamlTokenType.MappingEnd)
        {
            if (reader.Type != YamlTokenType.Scalar)
            {
                throw CollectionKey(in reader);
            }

            if (ConverterGuards.IsMergeKey(in reader))
            {
                if (!reader.Read())
                {
                    throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
                }

                ReadMerge(ref reader, options, ref instance, slots, ref seen, ref merged);
                continue;
            }

            int keyStart = reader.Start;
            int index = FindMember(ref reader, info);
            if (index < 0)
            {
                SkipUnmapped(ref reader, options, ref skipped, keyStart);
                continue;
            }

            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            if (Claim(ref reader, ref seen, ref merged, index, merging: false, keyStart))
            {
                return info.Props[index];
            }
        }

        skipped.Release();
        return null;
    }

    // The loop of the common case, with nothing for extension data or a resumed first key: it is on the stack once for every
    // level of a nested document, so it holds only what the plain case needs.
    private void ReadMembers(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[]? slots,
        ref MemberSet seen,
        ref MemberSet merged,
        bool merging)
    {
        YamlTypeInfo<T> info = _info;
        var skipped = default(SkippedKeys);
        while (reader.Read() && reader.Type != YamlTokenType.MappingEnd)
        {
            if (reader.Type != YamlTokenType.Scalar)
            {
                throw CollectionKey(in reader);
            }

            if (ConverterGuards.IsMergeKey(in reader))
            {
                if (!reader.Read())
                {
                    throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
                }

                ReadMerge(ref reader, options, ref instance, slots, ref seen, ref merged);
                continue;
            }

            int keyStart = reader.Start;
            int index = FindMember(ref reader, info);
            if (index < 0)
            {
                SkipUnmapped(ref reader, options, ref skipped, keyStart);
                continue;
            }

            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            if (!Claim(ref reader, ref seen, ref merged, index, merging, keyStart))
            {
                continue;
            }

            YamlPropertyInfo<T> p = info.Props[index];
            if (slots is null)
            {
                p.Read(ref reader, ref instance);
            }
            else
            {
                p.ReadBoxed(ref reader, slots);
            }
        }

        skipped.Release();
    }

    // The same walk for the less common cases: extension data, a first key that was read already, or a merged mapping of such a type.
    private void ReadMembersGeneral(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[]? slots,
        ref MemberSet seen,
        ref MemberSet merged,
        int mode)
    {
        YamlTypeInfo<T> info = _info;
        bool merging = mode == ReadMerging;
        var skipped = default(SkippedKeys);
        Dictionary<string, object?>? extra = null;
        while ((mode != ReadResume ? reader.Read() : Resumed(ref mode)) && reader.Type != YamlTokenType.MappingEnd)
        {
            if (reader.Type != YamlTokenType.Scalar)
            {
                throw CollectionKey(in reader);
            }

            if (ConverterGuards.IsMergeKey(in reader))
            {
                if (!reader.Read())
                {
                    throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
                }

                ReadMerge(ref reader, options, ref instance, slots, ref seen, ref merged);
                continue;
            }

            int keyStart = reader.Start;
            int index = FindMember(ref reader, info);
            if (index < 0)
            {
                if (info.Extension is not null)
                {
                    ReadExtra(ref reader, options, ref skipped, ref extra, keyStart);
                }
                else
                {
                    SkipUnmapped(ref reader, options, ref skipped, keyStart);
                }

                continue;
            }

            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            if (!Claim(ref reader, ref seen, ref merged, index, merging, keyStart))
            {
                continue;
            }

            YamlPropertyInfo<T> p = info.Props[index];
            if (slots is null)
            {
                p.Read(ref reader, ref instance);
            }
            else
            {
                p.ReadBoxed(ref reader, slots);
            }
        }

        if (extra is not null)
        {
            FlushExtra(ref instance, slots, extra, merging);
        }

        skipped.Release();
    }

    // The first key was read already: the loop starts on it instead of reading one.
    private static bool Resumed(ref int mode)
    {
        mode = ReadNormal;
        return true;
    }

    // A key no member takes goes to the extension member. Like a skipped key it is remembered, so a repeat is still an error.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadExtra(ref Utf8YamlReader reader, YamlSerializerOptions options, ref SkippedKeys skipped, ref Dictionary<string, object?>? extra, int keyStart)
    {
        SkipKey key = SkippedKeys.Capture(ref reader);
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (!skipped.Add(key, reader.Source))
        {
            throw DuplicateKey(in reader, keyStart);
        }

        string name = Encoding.UTF8.GetString(key.Bytes(reader.Source));
        object? value = _info.Extension!.ReadExtensionValue(ref reader, options);
        (extra ??= new Dictionary<string, object?>(StringComparer.Ordinal))[name] = value;
    }

    // Hands the collected keys to the member. Entries from a merged mapping give way to a key the mapping has itself.
    private void FlushExtra(ref T instance, object?[]? slots, Dictionary<string, object?> collected, bool merging)
    {
        YamlPropertyInfo<T> extension = _info.Extension!;
        foreach (KeyValuePair<string, object?> entry in collected)
        {
            extension.AddExtension(ref instance, slots, entry.Key, entry.Value, overwrite: !merging);
        }
    }

    // The bookkeeping of a key no member takes, out of line like the other rare cases of ReadMembers: its frame is
    // there once for every level of a nested document.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SkipUnmapped(ref Utf8YamlReader reader, YamlSerializerOptions options, ref SkippedKeys skipped, int keyStart)
    {
        SkipKey skippedKey = SkippedKeys.Capture(ref reader);
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (options.UnmappedMemberHandling == YamlUnmappedMemberHandling.Disallow)
        {
            throw UnmappedKey(in reader, keyStart);
        }

        if (!skipped.Add(skippedKey, reader.Source))
        {
            throw DuplicateKey(in reader, keyStart);
        }

        reader.Skip();
    }

    // Records that a member got a value, or says the value is to be skipped because an earlier one wins. Small enough for
    // the optimised code to inline; the code compiled first does not inline, and keeps the frame of ReadMembers small.
    private static bool Claim(ref Utf8YamlReader reader, ref MemberSet seen, ref MemberSet merged, int index, bool merging, int keyStart)
    {
        if (merging)
        {
            // A merged entry never replaces an explicit key or an entry merged before it.
            if (seen.Contains(index) || merged.Contains(index))
            {
                reader.Skip();
                return false;
            }

            merged.Add(index);
            return true;
        }

        if (seen.Contains(index))
        {
            throw DuplicateKey(in reader, keyStart);
        }

        seen.Add(index);
        return true;
    }

    private void ReadMergedMapping(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[]? slots,
        ref MemberSet seen,
        ref MemberSet merged)
    {
        if (_info.Extension is null)
        {
            ReadMembers(ref reader, options, ref instance, slots, ref seen, ref merged, merging: true);
        }
        else
        {
            ReadMembersGeneral(ref reader, options, ref instance, slots, ref seen, ref merged, ReadMerging);
        }
    }

    private void ReadMerge(
        ref Utf8YamlReader reader,
        YamlSerializerOptions options,
        ref T instance,
        object?[]? slots,
        ref MemberSet seen,
        ref MemberSet merged)
    {
        // A merge value may itself hold a merge key, so this recurses with the input.
        ConverterGuards.EnsureStack(in reader);
        if (reader.Type == YamlTokenType.MappingStart)
        {
            ReadMergedMapping(ref reader, options, ref instance, slots, ref seen, ref merged);
        }
        else if (reader.Type == YamlTokenType.SequenceStart)
        {
            while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
            {
                if (reader.Type != YamlTokenType.MappingStart)
                {
                    throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
                }

                ReadMergedMapping(ref reader, options, ref instance, slots, ref seen, ref merged);
            }
        }
        else
        {
            throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
        }
    }

    // The messages are built out of line: a message builder in the reading methods would enlarge their frames, and the
    // frames of the levels above add up on a deeply nested input.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static YamlException NotCreatable(in Utf8YamlReader reader) =>
        reader.CreateException($"{ConverterGuards.DescribeType(typeof(T))} cannot be created: it has no public constructor to bind through.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static YamlException CollectionKey(in Utf8YamlReader reader) =>
        reader.CreateException($"This mapping key is a collection; {ConverterGuards.DescribeType(typeof(T))} binds scalar keys only. Read documents with complex keys through YamlDocument.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static YamlException DuplicateKey(in Utf8YamlReader reader, int keyStart) =>
        reader.ErrorAtKey(YamlErrorCategory.DuplicateKey, "The key '{0}' appears twice in the mapping.", keyStart);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static YamlException UnmappedKey(in Utf8YamlReader reader, int keyStart) =>
        reader.ErrorAtKey(YamlErrorCategory.Binding, $"The key '{{0}}' is not a member of {ConverterGuards.DescribeType(typeof(T))}.", keyStart);

    private static int FindMember(ref Utf8YamlReader reader, YamlTypeInfo<T> info)
    {
        if (!reader.Escaped)
        {
            return info.Find(reader.Source.Slice(reader.ContentStart, reader.ContentEnd - reader.ContentStart));
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[128];
            return info.Find(reader.Text(scratch, ref rented));
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private void CheckRequired(in Utf8YamlReader reader, int start, ref MemberSet seen, ref MemberSet merged)
    {
        foreach (YamlPropertyInfo<T> p in _info.Props)
        {
            if (p.IsRequired && !seen.Contains(p.Index) && !merged.Contains(p.Index))
            {
                throw reader.ErrorAt(YamlErrorCategory.Binding, $"The required key '{p.Name}' is missing.", start);
            }
        }
    }
}

// Binds a mapping to a type through its constructor. Every level's arguments are held until the innermost value is read, so
// this is the path that nests deepest on the stack. The runtime compiles a method with a loop first as instrumented code whose
// frame is twice the size, and a document that is read once never gets past that code, so the methods that recurse here are
// compiled fully optimised from the start, and each holds only what its own work needs. It is a class of its own, not a
// subclass of ObjectConverter: a class that can be derived from looks up its type arguments through its parent, at a cost
// for every call.
internal sealed class ConstructedObjectConverter<T> : YamlConverter<T>
{
    private readonly YamlTypeInfo<T> _info;
    private readonly ObjectConverter<T> _plain;

    public ConstructedObjectConverter(YamlTypeInfo<T> info)
    {
        _info = info;
        _plain = new ObjectConverter<T>(info);
    }

    internal ObjectConverter<T> Plain => _plain;

    public override void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options) => _plain.Write(writer, value, options);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.MappingStart)
        {
            throw ConverterGuards.Expected(in reader, "a mapping", typeof(T));
        }

        ConverterGuards.EnsureStack(in reader);
        YamlTypeInfo<T> info = _info;
        if (!info.CanCreate)
        {
            throw ObjectConverter<T>.NotCreatable(in reader);
        }

        info.EnsureConfigured();
        if (info.Extension is not null)
        {
            // Collecting unmapped keys is the general path; the lean loop below has no room for it.
            return _plain.Read(ref reader, typeToConvert, options);
        }

        int start = reader.Start;
        var seen = new MemberSet(info.Props.Length);
        var merged = new MemberSet(info.Props.Length);
        object?[] slots = info.NewArguments();
        T instance = default!;
        _plain.ReadMembersBoxed(ref reader, options, ref instance, slots, ref seen, ref merged);
        return ObjectConverter<T>.Construct(in reader, info, start, slots, ref seen, ref merged);
    }
}
