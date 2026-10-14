using System;
using System.Buffers;
using System.Collections.Generic;
using OneYaml.Parsing;

namespace OneYaml;

// Reads any value as null, bool, long (else BigInteger), double, string, List<object?> or
// Dictionary<string, object?>. It walks the input with an explicit stack, so nesting costs heap,
// never call depth.
internal sealed class GenericConverter : YamlConverter<object>
{
    public override bool HandleNull => true;

    public override object? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options) =>
        GenericLoader.Load(ref reader);
}

internal struct GenericFrame
{
    public object Container;
    public string? Key;
    public int KeyOffset;
    public int Entry;
    public bool IsMap;
    public bool HaveKey;
    public bool KeyIsMerge;
    public HashSet<string>? Merged;
    public HashSet<string>? Identities;
}

internal static class GenericLoader
{
    public static object? Load(ref Utf8YamlReader reader)
    {
        GenericFrame[] stack = ArrayPool<GenericFrame>.Shared.Rent(16);
        int sp = 0;
        try
        {
            while (true)
            {
                object? value;
                int entry;
                bool expectKey = sp > 0 && stack[sp - 1].IsMap && !stack[sp - 1].HaveKey;
                switch (reader.Type)
                {
                    case YamlTokenType.Scalar:
                        entry = reader.NodeEntry;
                        if (expectKey)
                        {
                            ref GenericFrame kf = ref stack[sp - 1];
                            kf.KeyIsMerge = ConverterGuards.IsMergeKey(in reader);
                            kf.KeyOffset = reader.Start;
                            kf.Key = reader.GetSharedString();
                            kf.HaveKey = true;
                            if (reader.Style == ScalarStyle.Plain || reader.Tag is not TagClass.None)
                            {
                                // Keys are judged after resolution: 1 and 0x1 are the same key.
                                string? identity = reader.KeyIdentity();
                                if (identity is not null && !(kf.Identities ??= []).Add(identity))
                                {
                                    throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", reader.Source, reader.Start);
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
                            ref GenericFrame af = ref stack[sp - 1];
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

                            if (sp == stack.Length)
                            {
                                GenericFrame[] bigger = ArrayPool<GenericFrame>.Shared.Rent(stack.Length * 2);
                                Array.Copy(stack, bigger, sp);
                                Array.Clear(stack, 0, sp);
                                ArrayPool<GenericFrame>.Shared.Return(stack);
                                stack = bigger;
                            }

                            bool map = reader.Type == YamlTokenType.MappingStart;
                            stack[sp++] = new GenericFrame
                            {
                                Container = map ? new Dictionary<string, object?>() : new List<object?>(),
                                IsMap = map,
                                Entry = reader.NodeEntry,
                            };
                            goto Next;
                        }

                    case YamlTokenType.MappingEnd:
                    case YamlTokenType.SequenceEnd:
                        {
                            if (sp == 0)
                            {
                                throw reader.Error(YamlErrorCategory.Syntax, "Unexpected end of a collection.");
                            }

                            ref GenericFrame top = ref stack[--sp];
                            value = top.Container;
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

                if (sp == 0)
                {
                    return value;
                }

                Add(ref reader, ref stack[sp - 1], value);

            Next:
                if (!reader.Read())
                {
                    throw reader.Error(YamlErrorCategory.Syntax, "The document ended inside a value.");
                }
            }
        }
        finally
        {
            ArrayPool<GenericFrame>.Shared.Return(stack, clearArray: true);
        }
    }

    private static void Add(ref Utf8YamlReader reader, ref GenericFrame frame, object? value)
    {
        if (!frame.IsMap)
        {
            ((List<object?>)frame.Container).Add(value);
            return;
        }

        var dict = (Dictionary<string, object?>)frame.Container;
        string key = frame.Key!;
        frame.HaveKey = false;
        frame.Key = null;
        if (frame.KeyIsMerge)
        {
            Merge(ref reader, dict, ref frame.Merged, value, frame.KeyOffset);
        }
        else if (frame.Merged is not null && frame.Merged.Remove(key))
        {
            dict[key] = value;
        }
        else if (!dict.TryAdd(key, value))
        {
            throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", reader.Source, frame.KeyOffset);
        }
    }

    // The merge key copies the entries of a mapping, or of each mapping in a sequence, that are not already there.
    private static void Merge(ref Utf8YamlReader reader, Dictionary<string, object?> into, ref HashSet<string>? merged, object? source, int offset)
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
                    throw YamlException.At(YamlErrorCategory.Binding, "A merge key needs a mapping or a sequence of mappings.", reader.Source, offset);
                }

                Copy(into, ref merged, d);
            }

            return;
        }

        throw YamlException.At(YamlErrorCategory.Binding, "A merge key needs a mapping or a sequence of mappings.", reader.Source, offset);
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
