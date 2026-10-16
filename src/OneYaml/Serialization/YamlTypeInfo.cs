using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using OneYaml.Serialization.Metadata;

namespace OneYaml;

/// <summary>Metadata about a type: how its values are read. Source-generated code and the reflection resolver both produce it.</summary>
public abstract class YamlTypeInfo
{
    internal YamlTypeInfo(Type type, YamlSerializerOptions options)
    {
        Type = type;
        Options = options;
    }

    /// <summary>The type this metadata describes.</summary>
    public Type Type { get; }

    /// <summary>The options this metadata belongs to.</summary>
    public YamlSerializerOptions Options { get; }

    internal abstract YamlConverter ConverterBase { get; }

    internal abstract object? ReadBoxed(ref Utf8YamlReader reader);

    internal abstract void WriteBoxed(Utf8YamlEmitter writer, object? value);
}

/// <summary>Metadata about <typeparamref name="T"/>: its converter and, for an object type, its members.</summary>
/// <typeparam name="T">The described type.</typeparam>
public sealed class YamlTypeInfo<T> : YamlTypeInfo
{
    private readonly YamlObjectInfoValues<T>? _values;
    private readonly object _gate = new();
    private volatile bool _configured;
    private YamlPropertyInfo<T>[] _properties = [];
    private YamlPropertyInfo<T>[] _writeProps = [];
    private YamlParameterInfo[] _parameters = [];
    private byte[][] _names = [];
    private int[] _buckets = [];
    private Dictionary<string, int>? _ignoreCase;
    private int _extraSlots;
    private object?[]? _defaults;
    private int _requiredCount;

    internal YamlTypeInfo(YamlSerializerOptions options, YamlConverter<T> converter)
        : base(typeof(T), options)
    {
        Converter = converter;
        _configured = true;
    }

    internal YamlTypeInfo(YamlSerializerOptions options, YamlObjectInfoValues<T> values)
        : base(typeof(T), options)
    {
        _values = values;
        Converter = new ObjectConverter<T>(this);
    }

    /// <summary>The converter for the type.</summary>
    public YamlConverter<T> Converter { get; }

    /// <summary>The members that are bound, for an object type; empty otherwise.</summary>
    public IReadOnlyList<YamlPropertyInfo> Properties
    {
        get
        {
            EnsureConfigured();
            return _properties;
        }
    }

    internal override YamlConverter ConverterBase => Converter;

    internal override object? ReadBoxed(ref Utf8YamlReader reader) => Converter is YamlConverter<T> c ? c.ReadValue(ref reader, Options) : null;

    internal bool UsesConstructor => _values!.ConstructorCreator is not null;

    internal bool CanCreate => _values!.ConstructorCreator is not null || _values.ObjectCreator is not null;

    internal YamlPropertyInfo<T>[] Props => _properties;

    // The members that can be written, in the order they are written: by order, then as described.
    internal YamlPropertyInfo<T>[] WriteProps => _writeProps;

    internal override void WriteBoxed(Utf8YamlEmitter writer, object? value) => Converter.WriteBoxed(writer, value, Options);

    internal int RequiredCount => _requiredCount;

    internal int SlotCount => _parameters.Length + _extraSlots;

    internal YamlParameterInfo[] Parameters => _parameters;

    internal T CreateInstance() => _values!.ObjectCreator is { } create
        ? create()
        : throw new InvalidOperationException($"Type '{typeof(T)}' has no public parameterless constructor, so it cannot be created.");

    internal T CreateWith(object?[] args) => _values!.ConstructorCreator!(args);

    internal object?[] NewArguments() => (object?[])_defaults!.Clone();

    internal void EnsureConfigured()
    {
        if (!_configured)
        {
            Configure();
        }
    }

    private void Configure()
    {
        lock (_gate)
        {
            if (_configured)
            {
                return;
            }

            YamlObjectInfoValues<T> values = _values!;
            YamlPropertyInfo[] raw = values.PropertyMetadataInitializer?.Invoke(Options) ?? [];
            var props = new YamlPropertyInfo<T>[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                props[i] = (YamlPropertyInfo<T>)raw[i];
                props[i].Configure(Options, i);
            }

            YamlParameterInfo[] parameters = values.ConstructorParameterMetadataInitializer?.Invoke() ?? [];
            if (values.ConstructorCreator is null)
            {
                parameters = [];
            }

            // A constructor parameter is filled from the member of the same name, ignoring case.
            int extra = 0;
            foreach (YamlPropertyInfo<T> p in props)
            {
                p.ParameterIndex = -1;
                for (int k = 0; k < parameters.Length; k++)
                {
                    if (string.Equals(parameters[k].Name, p.ClrName, StringComparison.OrdinalIgnoreCase))
                    {
                        p.ParameterIndex = k;
                        break;
                    }
                }

                p.Slot = p.ParameterIndex >= 0 ? p.ParameterIndex : parameters.Length + extra++;
                if (p.IsRequired)
                {
                    _requiredCount++;
                }
            }

            object?[] defaults = new object?[parameters.Length + extra];
            for (int k = 0; k < parameters.Length; k++)
            {
                // A missing value stays null, and the constructor call turns null into default(T).
                defaults[k] = parameters[k].HasDefaultValue ? parameters[k].DefaultValue : null;
            }

            // Build the name table: UTF-8 names in open-addressing buckets, so a key is matched without a string.
            var names = new byte[props.Length][];
            int size = 4;
            while (size < props.Length * 2)
            {
                size *= 2;
            }

            var buckets = new int[size];
            Dictionary<string, int>? ignoreCase = Options.PropertyNameCaseInsensitive ? new(StringComparer.OrdinalIgnoreCase) : null;
            for (int i = 0; i < props.Length; i++)
            {
                names[i] = props[i].NameUtf8;
                int b = (int)(Hash(names[i]) & (uint)(size - 1));
                while (buckets[b] != 0)
                {
                    if (names[buckets[b] - 1].AsSpan().SequenceEqual(names[i]))
                    {
                        throw new InvalidOperationException($"Type '{typeof(T)}' has two members that read the key '{props[i].Name}'.");
                    }

                    b = (b + 1) & (size - 1);
                }

                buckets[b] = i + 1;
                if (ignoreCase is not null && !ignoreCase.TryAdd(props[i].Name, i))
                {
                    throw new InvalidOperationException($"Type '{typeof(T)}' has two members that read the key '{props[i].Name}' when case is ignored.");
                }
            }

            _writeProps = [.. props.Where(static p => p.CanWrite).OrderBy(static p => p.Order)];
            _properties = props;
            _parameters = parameters;
            _extraSlots = extra;
            _defaults = defaults;
            _names = names;
            _buckets = buckets;
            _ignoreCase = ignoreCase;
            _configured = true;
        }
    }

    // Finds the member for a key given as UTF-8 text; -1 when none matches.
    internal int Find(ReadOnlySpan<byte> key)
    {
        int[] buckets = _buckets;
        int mask = buckets.Length - 1;
        int i = (int)(Hash(key) & (uint)mask);
        while (true)
        {
            int slot = buckets[i];
            if (slot == 0)
            {
                break;
            }

            if (_names[slot - 1].AsSpan().SequenceEqual(key))
            {
                return slot - 1;
            }

            i = (i + 1) & mask;
        }

        if (_ignoreCase is not null && _ignoreCase.TryGetValue(System.Text.Encoding.UTF8.GetString(key), out int found))
        {
            return found;
        }

        return -1;
    }

    private static uint Hash(ReadOnlySpan<byte> s)
    {
        ulong a;
        if (s.Length >= 8)
        {
            a = BinaryPrimitives.ReadUInt64LittleEndian(s);
            ulong b = BinaryPrimitives.ReadUInt64LittleEndian(s[^8..]);
            a = (a * 0x9E3779B97F4A7C15UL) ^ (b + (uint)s.Length);
        }
        else
        {
            a = (ulong)s.Length << 56;
            for (int i = 0; i < s.Length; i++)
            {
                a = (a << 8) | s[i];
            }
        }

        a *= 0x9E3779B97F4A7C15UL;
        return (uint)(a >> 32);
    }
}
