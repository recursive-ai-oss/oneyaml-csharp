using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using OneYaml.Parsing;

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

        writer.WriteEndMapping();
    }

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
            throw reader.CreateException($"{ConverterGuards.DescribeType(typeof(T))} cannot be created: it has no public constructor to bind through.");
        }

        info.EnsureConfigured();
        int start = reader.Start;
        var seen = new MemberSet(info.Props.Length);
        var merged = new MemberSet(info.Props.Length);
        T instance;
        if (info.UsesConstructor)
        {
            object?[] slots = info.NewArguments();
            instance = default!;
            ReadMembers(ref reader, options, ref instance, slots, ref seen, ref merged, merging: false);
            CheckRequired(in reader, start, ref seen, ref merged);
            instance = info.CreateWith(slots);
            foreach (YamlPropertyInfo<T> p in info.Props)
            {
                if (p.ParameterIndex < 0 && (seen.Contains(p.Index) || merged.Contains(p.Index)))
                {
                    p.Assign(ref instance, slots[p.Slot]);
                }
            }

            return instance;
        }

        instance = info.CreateInstance();
        ReadMembers(ref reader, options, ref instance, null, ref seen, ref merged, merging: false);
        if (info.RequiredCount > 0)
        {
            CheckRequired(in reader, start, ref seen, ref merged);
        }

        return instance;
    }

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
                throw reader.CreateException($"This mapping key is a collection; {ConverterGuards.DescribeType(typeof(T))} binds scalar keys only. Read documents with complex keys through YamlDocument.");
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
            SkipKey skippedKey = index < 0 ? SkippedKeys.Capture(ref reader) : default;
            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            if (index < 0)
            {
                if (options.UnmappedMemberHandling == YamlUnmappedMemberHandling.Disallow)
                {
                    throw YamlException.At(YamlErrorCategory.Binding, $"The key is not a member of {ConverterGuards.DescribeType(typeof(T))}.", reader.Source, keyStart);
                }

                if (!skipped.Add(skippedKey, reader.Source))
                {
                    throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", reader.Source, keyStart);
                }

                reader.Skip();
                continue;
            }

            if (merging)
            {
                // A merged entry never replaces an explicit key or an entry merged before it.
                if (seen.Contains(index) || merged.Contains(index))
                {
                    reader.Skip();
                    continue;
                }

                merged.Add(index);
            }
            else
            {
                if (seen.Contains(index))
                {
                    throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", reader.Source, keyStart);
                }

                seen.Add(index);
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
            ReadMembers(ref reader, options, ref instance, slots, ref seen, ref merged, merging: true);
        }
        else if (reader.Type == YamlTokenType.SequenceStart)
        {
            while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
            {
                if (reader.Type != YamlTokenType.MappingStart)
                {
                    throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
                }

                ReadMembers(ref reader, options, ref instance, slots, ref seen, ref merged, merging: true);
            }
        }
        else
        {
            throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
        }
    }

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
                throw YamlException.At(YamlErrorCategory.Binding, $"The required key '{p.Name}' is missing.", reader.Source, start);
            }
        }
    }
}
