using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneYaml;

// Writes a value known only as an object, by its run-time type: the scalars, mappings and sequences that a
// load into `object` produces, and any other type through its metadata. Collections are walked with an
// explicit stack, so a deeply nested value costs heap, never call depth.
internal static class GenericWriter
{
    private static readonly ConcurrentDictionary<Type, YamlConverter?> s_leaves = new();

    private enum FrameKind : byte
    {
        // The two shapes a load into `object` makes get struct enumerators, so walking them allocates nothing.
        Dictionary,
        List,
        Entries,
        Items,
    }

    private struct Frame
    {
        public Dictionary<string, object?>.Enumerator Dictionary;
        public List<object?>.Enumerator List;
        public IEnumerator? Items;
        public IDictionaryEnumerator? Entries;
        public FrameKind Kind;
    }

    // Room for the first levels of nesting on the stack, so that an ordinary value never rents a frame array.
    [InlineArray(8)]
    private struct InlineFrames
    {
        private Frame _first;
    }

    // The open collections. It starts in the caller's stack space and moves to a pooled array when it runs out.
    private ref struct FrameStack
    {
        private Span<Frame> _items;
        private Frame[]? _rented;

        public FrameStack(Span<Frame> initial)
        {
            _items = initial;
            _rented = null;
            Count = 0;
        }

        public int Count;

        public ref Frame Top => ref _items[Count - 1];

        public ref Frame Push()
        {
            if (Count == _items.Length)
            {
                Frame[] bigger = ArrayPool<Frame>.Shared.Rent(Math.Max(4, _items.Length * 2));
                _items[..Count].CopyTo(bigger);
                _items[..Count].Clear();
                Release();
                _rented = bigger;
                _items = bigger;
            }

            return ref _items[Count++];
        }

        public void DisposeOpen()
        {
            for (int i = 0; i < Count; i++)
            {
                (_items[i].Items as IDisposable)?.Dispose();
            }
        }

        public void Release()
        {
            if (_rented is not null)
            {
                ArrayPool<Frame>.Shared.Return(_rented, clearArray: true);
                _rented = null;
            }
        }
    }

    // This is re-entered once per level when plain objects sit under members of type object, so its frame stays small:
    // a value with no collection to walk is written here without any frame storage, and only the outermost walk of a
    // write keeps room for the first open collections on the stack.
    public static void Write(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options)
    {
        YamlTypeInfo? info = WriteLeaf(writer, value, options, out bool done);
        if (info is not null)
        {
            // Straight to the converter: each call between here and the next level of an object chain is a frame on the stack.
            info.ConverterBase.WriteBoxed(writer, value, info.Options);
            return;
        }

        if (done)
        {
            return;
        }

        if (writer.GenericWalkActive)
        {
            Walk(writer, value, options, default);
        }
        else
        {
            WalkOutermost(writer, value, options);
        }
    }

    // Writes null and the scalars. For an object that is no collection it returns the metadata to write it with, and for
    // a collection (or a type with a custom converter that needs the walk) it writes nothing.
    private static YamlTypeInfo? WriteLeaf(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options, out bool done)
    {
        done = true;
        if (value is null)
        {
            writer.WriteNullValue();
            return null;
        }

        Type type = value.GetType();
        if (type == typeof(Dictionary<string, object?>) || type == typeof(List<object?>))
        {
            done = false;
            return null;
        }

        if (TryWriteScalar(writer, value, options))
        {
            return null;
        }

        if (TryWriteJson(writer, value, options))
        {
            return null;
        }

        if (!HasCustomConverter(options, type) && value is not IDictionary && value is not IEnumerable)
        {
            return options.GetTypeInfo(type);
        }

        done = false;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WalkOutermost(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options)
    {
        InlineFrames inline = default;
        writer.GenericWalkActive = true;
        try
        {
            Walk(writer, value, options, inline);
        }
        finally
        {
            writer.GenericWalkActive = false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Walk(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options, Span<Frame> initial)
    {
        var stack = new FrameStack(initial);
        // Custom converters may claim any type, even string, so they turn the shortcuts off.
        bool plain = options.Converters.Count == 0;
        try
        {
            object? current = value;
            bool pending = true;
            while (true)
            {
                if (pending)
                {
                    pending = false;
                    Push(writer, current, options, plain, ref stack);
                }

                if (stack.Count == 0)
                {
                    return;
                }

                ref Frame top = ref stack.Top;
                switch (top.Kind)
                {
                    case FrameKind.Dictionary:
                        if (top.Dictionary.MoveNext())
                        {
                            KeyValuePair<string, object?> entry = top.Dictionary.Current;
                            if (plain)
                            {
                                writer.WriteStringKey(entry.Key);
                            }
                            else
                            {
                                WriteKey(writer, entry.Key, options);
                            }

                            current = entry.Value;
                            pending = true;
                        }
                        else
                        {
                            writer.WriteEndMapping();
                            top = default;
                            stack.Count--;
                        }

                        break;
                    case FrameKind.List:
                        if (top.List.MoveNext())
                        {
                            current = top.List.Current;
                            pending = true;
                        }
                        else
                        {
                            writer.WriteEndSequence();
                            top = default;
                            stack.Count--;
                        }

                        break;
                    case FrameKind.Entries:
                        {
                            IDictionaryEnumerator entries = top.Entries!;
                            if (entries.MoveNext())
                            {
                                WriteKey(writer, entries.Key, options);
                                current = entries.Value;
                                pending = true;
                            }
                            else
                            {
                                writer.WriteEndMapping();
                                top = default;
                                stack.Count--;
                            }

                            break;
                        }

                    default:
                        if (top.Items!.MoveNext())
                        {
                            current = top.Items.Current;
                            pending = true;
                        }
                        else
                        {
                            writer.WriteEndSequence();
                            (top.Items as IDisposable)?.Dispose();
                            top = default;
                            stack.Count--;
                        }

                        break;
                }
            }
        }
        finally
        {
            stack.DisposeOpen();
            stack.Release();
        }
    }

    // Writes a scalar, or starts a collection and pushes its enumerator.
    private static void Push(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options, bool plain, ref FrameStack stack)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (plain)
        {
            // What a load into `object` gives, by exact type, before the general checks below.
            Type type = value.GetType();
            if (type == typeof(string))
            {
                writer.WriteStringValue((string)value);
                return;
            }

            if (type == typeof(Dictionary<string, object?>))
            {
                var dictionary = (Dictionary<string, object?>)value;
                writer.Begin(isMapping: true, value);
                ref Frame frame = ref stack.Push();
                frame.Dictionary = dictionary.GetEnumerator();
                frame.Kind = FrameKind.Dictionary;
                return;
            }

            if (type == typeof(List<object?>))
            {
                var list = (List<object?>)value;
                writer.Begin(isMapping: false, value);
                ref Frame frame = ref stack.Push();
                frame.List = list.GetEnumerator();
                frame.Kind = FrameKind.List;
                return;
            }

            if (type == typeof(long))
            {
                writer.WriteNumberValue((long)value);
                return;
            }

            if (type == typeof(double))
            {
                writer.WriteNumberValue((double)value);
                return;
            }

            if (type == typeof(bool))
            {
                writer.WriteBooleanValue((bool)value);
                return;
            }
        }

        PushGeneral(writer, value, options, ref stack);
    }

    private static void PushGeneral(Utf8YamlEmitter writer, object value, YamlSerializerOptions options, ref FrameStack stack)
    {
        if (TryWriteScalar(writer, value, options))
        {
            return;
        }

        if (TryWriteJson(writer, value, options))
        {
            return;
        }

        Type type = value.GetType();
        if (!HasCustomConverter(options, type))
        {
            Frame frame = default;
            bool map = false;
            if (value is IDictionary dictionary)
            {
                frame.Entries = dictionary.GetEnumerator();
                frame.Kind = FrameKind.Entries;
                map = true;
            }
            else if (value is IEnumerable sequence)
            {
                frame.Items = sequence.GetEnumerator();
                frame.Kind = FrameKind.Items;
            }
            else
            {
                options.GetTypeInfo(type).ConverterBase.WriteBoxed(writer, value, options);
                return;
            }

            writer.Begin(map, value);
            stack.Push() = frame;
            return;
        }

        options.GetTypeInfo(type).ConverterBase.WriteBoxed(writer, value, options);
    }

    private static void WriteKey(Utf8YamlEmitter writer, object? key, YamlSerializerOptions options)
    {
        if (key is null)
        {
            throw Utf8YamlEmitter.Misuse("A mapping key cannot be null.");
        }

        writer.WritingKey = true;
        if (!TryWriteScalar(writer, key, options))
        {
            // Not a type the walker knows as a scalar: its converter writes the key, or fails because it is no scalar.
            options.GetTypeInfo(key.GetType()).WriteBoxed(writer, key);
        }

        writer.WritingKey = false;
    }

    // The scalars of a load into `object`, and the other types that have a converter without metadata.
    private static bool TryWriteScalar(Utf8YamlEmitter writer, object value, YamlSerializerOptions options)
    {
        if (options.Converters.Count != 0 && HasCustomConverter(options, value.GetType()))
        {
            return false;
        }

        switch (value)
        {
            case string s:
                writer.WriteStringValue(s);
                return true;
            case bool b:
                writer.WriteBooleanValue(b);
                return true;
            case int i:
                writer.WriteNumberValue((long)i);
                return true;
            case long l:
                writer.WriteNumberValue(l);
                return true;
            case double d:
                writer.WriteNumberValue(d);
                return true;
            case BigInteger big:
                writer.WriteNumberValue(big);
                return true;
            default:
                break;
        }

        Type type = value.GetType();
        if (type == typeof(object))
        {
            // An instance of plain object has no members.
            writer.WriteStartMapping();
            writer.WriteEndMapping();
            return true;
        }

        if (type.IsEnum || value is IDictionary)
        {
            return false;
        }

        YamlConverter? leaf = s_leaves.GetOrAdd(type, static t => BuiltInConverters.CreateLeaf(t) as YamlConverter);
        if (leaf is null)
        {
            return false;
        }

        leaf.WriteBoxed(writer, value, options);
        return true;
    }

    // A System.Text.Json value held in an object is written by its runtime type, the way a member of that type is. Without
    // this, a JsonObject would be walked as a sequence of pairs and a JsonValue reflected as its internal class.
    private static bool TryWriteJson(Utf8YamlEmitter writer, object value, YamlSerializerOptions options)
    {
        if (value is not (JsonNode or JsonElement) || (options.Converters.Count != 0 && HasCustomConverter(options, value.GetType())))
        {
            return false;
        }

        if (value is JsonNode node)
        {
            JsonTrees.WriteNode(writer, node);
        }
        else
        {
            JsonTrees.WriteElement(writer, (JsonElement)value);
        }

        return true;
    }

    private static bool HasCustomConverter(YamlSerializerOptions options, Type type)
    {
        foreach (YamlConverter converter in options.Converters)
        {
            if (converter.CanConvert(type))
            {
                return true;
            }
        }

        return false;
    }
}
