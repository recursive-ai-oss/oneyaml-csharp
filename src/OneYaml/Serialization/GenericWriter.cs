using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

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

    public static void Write(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options)
    {
        Frame[]? stack = null;
        int sp = 0;
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
                    Push(writer, current, options, plain, ref stack, ref sp);
                }

                if (sp == 0)
                {
                    return;
                }

                ref Frame top = ref stack![sp - 1];
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
                            sp--;
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
                            sp--;
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
                                sp--;
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
                            sp--;
                        }

                        break;
                }
            }
        }
        finally
        {
            if (stack is not null)
            {
                for (int i = 0; i < sp; i++)
                {
                    (stack[i].Items as IDisposable)?.Dispose();
                }

                ArrayPool<Frame>.Shared.Return(stack, clearArray: true);
            }
        }
    }

    // Writes a scalar, or starts a collection and pushes its enumerator.
    private static void Push(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options, bool plain, ref Frame[]? stack, ref int sp)
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
                ref Frame frame = ref PushFrame(ref stack, ref sp);
                frame.Dictionary = dictionary.GetEnumerator();
                frame.Kind = FrameKind.Dictionary;
                return;
            }

            if (type == typeof(List<object?>))
            {
                var list = (List<object?>)value;
                writer.Begin(isMapping: false, value);
                ref Frame frame = ref PushFrame(ref stack, ref sp);
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

        PushGeneral(writer, value, options, ref stack, ref sp);
    }

    private static ref Frame PushFrame(ref Frame[]? stack, ref int sp)
    {
        if (stack is null)
        {
            stack = ArrayPool<Frame>.Shared.Rent(16);
        }
        else if (sp == stack.Length)
        {
            Frame[] bigger = ArrayPool<Frame>.Shared.Rent(stack.Length * 2);
            Array.Copy(stack, bigger, sp);
            Array.Clear(stack, 0, sp);
            ArrayPool<Frame>.Shared.Return(stack);
            stack = bigger;
        }

        return ref stack[sp++];
    }

    private static void PushGeneral(Utf8YamlEmitter writer, object value, YamlSerializerOptions options, ref Frame[]? stack, ref int sp)
    {
        if (TryWriteScalar(writer, value, options))
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
                options.GetTypeInfo(type).WriteBoxed(writer, value);
                return;
            }

            writer.Begin(map, value);
            PushFrame(ref stack, ref sp) = frame;
            return;
        }

        options.GetTypeInfo(type).WriteBoxed(writer, value);
    }

    private static void WriteKey(Utf8YamlEmitter writer, object? key, YamlSerializerOptions options)
    {
        if (key is null)
        {
            throw Utf8YamlEmitter.Misuse("A mapping key cannot be null.");
        }

        if (!TryWriteScalar(writer, key, options))
        {
            // Not a type the walker knows as a scalar: its converter writes the key, or fails because it is no scalar.
            options.GetTypeInfo(key.GetType()).WriteBoxed(writer, key);
        }
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
