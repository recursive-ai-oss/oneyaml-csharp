using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OneYaml.Parsing;

namespace OneYaml;

internal static class ConverterGuards
{
    // The binder recurses once per level of nesting of the target type. The reader limits the depth,
    // and this stops a runtime whose stack is smaller than that depth needs from overflowing.
    public static void EnsureStack(in Utf8YamlReader reader)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw reader.Error(YamlErrorCategory.Limit, "The nesting is too deep for the stack available.");
        }
    }

    public static YamlException Expected(in Utf8YamlReader reader, string what, Type type) =>
        reader.CreateException($"Expected {what} to read {DescribeType(type)}.");

    public static string DescribeType(Type type) => type.IsGenericType ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] + "<...>" : type.Name;

    public static bool IsMergeKey(in Utf8YamlReader reader) =>
        reader.Options.MergeKeys && reader.Type == YamlTokenType.Scalar && reader.Style == ScalarStyle.Plain && reader.RawEquals("<<"u8);
}

internal sealed class ListConverter<T> : YamlConverter<List<T>>
{
    private readonly YamlConverter<T> _element;

    public ListConverter(YamlTypeInfo<T> elementInfo) => _element = elementInfo.Converter;

    public override void Write(Utf8YamlEmitter writer, List<T> value, YamlSerializerOptions options)
    {
        writer.Begin(isMapping: false, value);
        for (int i = 0; i < value.Count; i++)
        {
            _element.WriteValue(writer, value[i], options);
        }

        writer.WriteEndSequence();
    }

    public override List<T>? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.SequenceStart)
        {
            throw ConverterGuards.Expected(in reader, "a sequence", typeof(List<T>));
        }

        ConverterGuards.EnsureStack(in reader);
        var list = new List<T>();
        while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
        {
            list.Add(_element.ReadValue(ref reader, options)!);
        }

        return list;
    }
}

internal sealed class ArrayConverter<T> : YamlConverter<T[]>
{
    private readonly YamlConverter<T> _element;

    public ArrayConverter(YamlTypeInfo<T> elementInfo) => _element = elementInfo.Converter;

    public override void Write(Utf8YamlEmitter writer, T[] value, YamlSerializerOptions options)
    {
        writer.Begin(isMapping: false, value);
        foreach (T item in value)
        {
            _element.WriteValue(writer, item, options);
        }

        writer.WriteEndSequence();
    }

    public override T[]? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.SequenceStart)
        {
            throw ConverterGuards.Expected(in reader, "a sequence", typeof(T[]));
        }

        ConverterGuards.EnsureStack(in reader);
        var list = new List<T>();
        while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
        {
            list.Add(_element.ReadValue(ref reader, options)!);
        }

        return list.ToArray();
    }
}

// A concrete collection type or interface made from the elements of a list.
internal sealed class CollectionConverter<TCollection, T> : YamlConverter<TCollection>
{
    private readonly ListConverter<T> _list;
    private readonly YamlConverter<T> _element;
    private readonly Func<List<T>, TCollection> _make;

    public CollectionConverter(YamlTypeInfo<T> elementInfo, Func<List<T>, TCollection> make)
    {
        _list = new ListConverter<T>(elementInfo);
        _element = elementInfo.Converter;
        _make = make;
    }

    public override void Write(Utf8YamlEmitter writer, TCollection value, YamlSerializerOptions options)
    {
        // The element type is known from the collection's interface; a value that is no sequence of T is a metadata mistake.
        if (value is not IEnumerable<T> items)
        {
            GenericWriter.Write(writer, value, options);
            return;
        }

        writer.Begin(isMapping: false, value);
        foreach (T item in items)
        {
            _element.WriteValue(writer, item, options);
        }

        writer.WriteEndSequence();
    }

    public override TCollection? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        List<T>? list = _list.Read(ref reader, typeToConvert, options);
        return list is null ? default : _make(list);
    }
}

internal sealed class DictionaryConverter<TKey, TValue> : YamlConverter<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly YamlConverter<TKey> _key;
    private readonly YamlConverter<TValue> _value;

    public DictionaryConverter(YamlTypeInfo<TKey> keyInfo, YamlTypeInfo<TValue> valueInfo)
    {
        _key = keyInfo.Converter;
        _value = valueInfo.Converter;
    }

    public override void Write(Utf8YamlEmitter writer, Dictionary<TKey, TValue> value, YamlSerializerOptions options) =>
        WriteEntries(writer, value, value, options);

    // The entries of any dictionary type, in the order it enumerates them. A scalar written where the mapping
    // expects a key becomes the key, so every key type is written by its own converter.
    internal void WriteEntries(Utf8YamlEmitter writer, object reference, IEnumerable<KeyValuePair<TKey, TValue>> entries, YamlSerializerOptions options)
    {
        writer.Begin(isMapping: true, reference);
        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            _key.WriteValue(writer, entry.Key, options);
            _value.WriteValue(writer, entry.Value, options);
        }

        writer.WriteEndMapping();
    }

    public override Dictionary<TKey, TValue>? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.MappingStart)
        {
            throw ConverterGuards.Expected(in reader, "a mapping", typeof(Dictionary<TKey, TValue>));
        }

        ConverterGuards.EnsureStack(in reader);
        var dict = new Dictionary<TKey, TValue>();
        HashSet<TKey>? merged = null;
        ReadEntries(ref reader, options, dict, ref merged, merging: false);
        return dict;
    }

    private void ReadEntries(ref Utf8YamlReader reader, YamlSerializerOptions options, Dictionary<TKey, TValue> dict, ref HashSet<TKey>? merged, bool merging)
    {
        while (reader.Read() && reader.Type != YamlTokenType.MappingEnd)
        {
            if (ConverterGuards.IsMergeKey(in reader))
            {
                ReadMerge(ref reader, options, dict, ref merged);
                continue;
            }

            int keyStart = reader.Start;
            TKey key = ReadKey(ref reader, options);
            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            TValue value = _value.ReadValue(ref reader, options)!;
            if (merging)
            {
                // Merged entries never replace an entry that is already there.
                if (dict.TryAdd(key, value))
                {
                    (merged ??= []).Add(key);
                }
            }
            else if (merged is not null && merged.Remove(key))
            {
                dict[key] = value;
            }
            else if (!dict.TryAdd(key, value))
            {
                throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", reader.Source, keyStart);
            }
        }
    }

    private void ReadMerge(ref Utf8YamlReader reader, YamlSerializerOptions options, Dictionary<TKey, TValue> dict, ref HashSet<TKey>? merged)
    {
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (reader.Type == YamlTokenType.MappingStart)
        {
            ReadEntries(ref reader, options, dict, ref merged, merging: true);
        }
        else if (reader.Type == YamlTokenType.SequenceStart)
        {
            while (reader.Read() && reader.Type != YamlTokenType.SequenceEnd)
            {
                if (reader.Type != YamlTokenType.MappingStart)
                {
                    throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
                }

                ReadEntries(ref reader, options, dict, ref merged, merging: true);
            }
        }
        else
        {
            throw reader.CreateException("A merge key needs a mapping or a sequence of mappings.");
        }
    }

    private TKey ReadKey(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.Scalar)
        {
            throw reader.CreateException("This mapping key is a collection; read documents with complex keys through YamlDocument.");
        }

        // A string or object key is the scalar's text, whatever it would resolve to.
        if (typeof(TKey) == typeof(string) || typeof(TKey) == typeof(object))
        {
            return (TKey)(object)reader.GetSharedString();
        }

        TKey? key = _key.ReadValue(ref reader, options);
        return key ?? throw reader.CreateException("A null key cannot be used.");
    }
}

// Any other dictionary type: read as a Dictionary, then copied.
internal sealed class GeneralDictionaryConverter<TDictionary, TKey, TValue> : YamlConverter<TDictionary>
    where TDictionary : class, IDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly DictionaryConverter<TKey, TValue> _inner;
    private readonly Func<TDictionary> _create;

    public GeneralDictionaryConverter(YamlTypeInfo<TKey> keyInfo, YamlTypeInfo<TValue> valueInfo, Func<TDictionary> create)
    {
        _inner = new DictionaryConverter<TKey, TValue>(keyInfo, valueInfo);
        _create = create;
    }

    public override void Write(Utf8YamlEmitter writer, TDictionary value, YamlSerializerOptions options) =>
        _inner.WriteEntries(writer, value, value, options);

    public override TDictionary? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        Dictionary<TKey, TValue>? source = _inner.Read(ref reader, typeToConvert, options);
        if (source is null)
        {
            return null;
        }

        TDictionary target = _create();
        foreach (KeyValuePair<TKey, TValue> pair in source)
        {
            target.Add(pair.Key, pair.Value);
        }

        return target;
    }
}

// A dictionary interface read as a Dictionary.
internal sealed class AdaptedConverter<TTarget, TImpl> : YamlConverter<TTarget>
    where TTarget : class
    where TImpl : class, TTarget
{
    private readonly YamlConverter<TImpl> _inner;

    public AdaptedConverter(YamlConverter<TImpl> inner) => _inner = inner;

    // The value is whatever implements the interface; the implementation the reader makes is only one of them.
    public override void Write(Utf8YamlEmitter writer, TTarget value, YamlSerializerOptions options)
    {
        if (value is TImpl impl)
        {
            _inner.Write(writer, impl, options);
        }
        else
        {
            GenericWriter.Write(writer, value, options);
        }
    }

    public override TTarget? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        _inner.Read(ref reader, typeToConvert, options);
}
