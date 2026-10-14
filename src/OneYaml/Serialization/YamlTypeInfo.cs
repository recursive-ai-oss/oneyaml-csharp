using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;

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
}

/// <summary>Metadata about <typeparamref name="T"/>: its converter and, for an object type, its members.</summary>
/// <typeparam name="T">The described type.</typeparam>
public sealed class YamlTypeInfo<T> : YamlTypeInfo
{
    private readonly YamlObjectInfoValues<T>? _values;
    private readonly object _gate = new();
    private volatile bool _configured;
    private YamlPropertyInfo<T>[] _properties = [];
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

/// <summary>Metadata about one constructor parameter.</summary>
public sealed class YamlParameterInfo
{
    /// <summary>Creates parameter metadata.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="parameterType">The parameter type.</param>
    /// <param name="hasDefaultValue">Whether the parameter has a default.</param>
    /// <param name="defaultValue">The default, when there is one.</param>
    public YamlParameterInfo(string name, Type parameterType, bool hasDefaultValue = false, object? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameterType);
        Name = name;
        ParameterType = parameterType;
        HasDefaultValue = hasDefaultValue;
        DefaultValue = defaultValue;
    }

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>The parameter type.</summary>
    public Type ParameterType { get; }

    /// <summary>Whether the parameter has a default value.</summary>
    public bool HasDefaultValue { get; }

    /// <summary>The default value, when there is one.</summary>
    public object? DefaultValue { get; }
}

/// <summary>Metadata about one member that is bound.</summary>
public abstract class YamlPropertyInfo
{
    internal YamlPropertyInfo(string clrName, string? explicitName, bool isRequired, int order)
    {
        ClrName = clrName;
        ExplicitName = explicitName;
        IsRequired = isRequired;
        Order = order;
        Name = explicitName ?? clrName;
        NameUtf8 = System.Text.Encoding.UTF8.GetBytes(Name);
    }

    /// <summary>The key this member reads, after the naming policy. Set when the type's metadata is first used.</summary>
    public string Name { get; private set; }

    /// <summary>The name of the CLR member.</summary>
    public string ClrName { get; }

    /// <summary>Whether the key must be present.</summary>
    public bool IsRequired { get; }

    /// <summary>The position among siblings when writing.</summary>
    public int Order { get; }

    /// <summary>The member's type.</summary>
    public abstract Type PropertyType { get; }

    internal string? ExplicitName { get; }

    internal byte[] NameUtf8 { get; private set; }

    internal int ParameterIndex { get; set; }

    internal int Slot { get; set; }

    internal int Index { get; private set; }

    internal void ApplyName(YamlSerializerOptions options, int index)
    {
        Index = index;
        if (ExplicitName is null && options.PropertyNamingPolicy is { } policy)
        {
            Name = policy.ConvertName(ClrName);
            NameUtf8 = System.Text.Encoding.UTF8.GetBytes(Name);
        }
    }
}

/// <summary>Metadata about a member of <typeparamref name="TDeclaring"/>.</summary>
/// <typeparam name="TDeclaring">The declaring type.</typeparam>
public abstract class YamlPropertyInfo<TDeclaring> : YamlPropertyInfo
{
    internal YamlPropertyInfo(string clrName, string? explicitName, bool isRequired, int order)
        : base(clrName, explicitName, isRequired, order)
    {
    }

    internal abstract void Configure(YamlSerializerOptions options, int index);

    // Reads the value and assigns it.
    internal abstract void Read(ref Utf8YamlReader reader, ref TDeclaring instance);

    // Reads the value into an argument slot, for types made through a constructor.
    internal abstract void ReadBoxed(ref Utf8YamlReader reader, object?[] slots);

    // Assigns a value read earlier.
    internal abstract void Assign(ref TDeclaring instance, object? value);
}

/// <summary>Assigns a member of a value type.</summary>
/// <typeparam name="TDeclaring">The declaring type.</typeparam>
/// <typeparam name="TProperty">The member type.</typeparam>
/// <param name="instance">The instance to change.</param>
/// <param name="value">The new value.</param>
public delegate void YamlRefSetter<TDeclaring, in TProperty>(ref TDeclaring instance, TProperty value);

/// <summary>Everything needed to describe one member without reflection.</summary>
/// <typeparam name="TDeclaring">The declaring type.</typeparam>
/// <typeparam name="TProperty">The member type.</typeparam>
public sealed class YamlPropertyInfoValues<TDeclaring, TProperty>
{
    /// <summary>The CLR member name.</summary>
    public required string Name { get; init; }

    /// <summary>The key given by an attribute, which the naming policy does not change; otherwise <see langword="null"/>.</summary>
    public string? SerializedName { get; init; }

    /// <summary>Metadata for the member's type. When <see langword="null"/>, it is looked up in the options.</summary>
    public YamlTypeInfo<TProperty>? PropertyTypeInfo { get; init; }

    /// <summary>A converter for this member only.</summary>
    public YamlConverter<TProperty>? Converter { get; init; }

    /// <summary>Reads the member.</summary>
    public Func<TDeclaring, TProperty>? Getter { get; init; }

    /// <summary>Assigns the member of a reference type. A member with no setter is filled through the constructor only.</summary>
    public Action<TDeclaring, TProperty>? Setter { get; init; }

    /// <summary>Assigns the member of a value type.</summary>
    public YamlRefSetter<TDeclaring, TProperty>? RefSetter { get; init; }

    /// <summary>Whether the key must be present.</summary>
    public bool IsRequired { get; init; }

    /// <summary>The position among siblings when writing.</summary>
    public int Order { get; init; }
}

/// <summary>The members of a type and how to make one.</summary>
/// <typeparam name="T">The type.</typeparam>
public sealed class YamlObjectInfoValues<T>
{
    /// <summary>Makes an instance through a parameterless constructor.</summary>
    public Func<T>? ObjectCreator { get; init; }

    /// <summary>Makes an instance through the constructor whose parameters <see cref="ConstructorParameterMetadataInitializer"/> describes.</summary>
    public Func<object?[], T>? ConstructorCreator { get; init; }

    /// <summary>Describes the members. Called once, when the type is first used.</summary>
    public Func<YamlSerializerOptions, YamlPropertyInfo[]>? PropertyMetadataInitializer { get; init; }

    /// <summary>Describes the constructor parameters.</summary>
    public Func<YamlParameterInfo[]>? ConstructorParameterMetadataInitializer { get; init; }
}

internal sealed class YamlPropertyInfoImpl<TDeclaring, TProperty> : YamlPropertyInfo<TDeclaring>
{
    private readonly YamlPropertyInfoValues<TDeclaring, TProperty> _values;
    private YamlConverter<TProperty>? _converter;
    private YamlSerializerOptions? _options;

    public YamlPropertyInfoImpl(YamlPropertyInfoValues<TDeclaring, TProperty> values)
        : base(values.Name, values.SerializedName, values.IsRequired, values.Order)
    {
        _values = values;
    }

    public override Type PropertyType => typeof(TProperty);

    internal override void Configure(YamlSerializerOptions options, int index)
    {
        ApplyName(options, index);
        _options = options;
        _converter = _values.Converter ?? (_values.PropertyTypeInfo ?? options.GetTypeInfo<TProperty>()).Converter;
    }

    internal override void Read(ref Utf8YamlReader reader, ref TDeclaring instance)
    {
        TProperty? value = _converter!.ReadValue(ref reader, _options!);
        if (_values.Setter is { } set)
        {
            set(instance, value!);
        }
        else if (_values.RefSetter is { } refSet)
        {
            refSet(ref instance, value!);
        }
    }

    internal override void ReadBoxed(ref Utf8YamlReader reader, object?[] slots) =>
        slots[Slot] = _converter!.ReadValue(ref reader, _options!);

    internal override void Assign(ref TDeclaring instance, object? value)
    {
        if (_values.Setter is { } set)
        {
            set(instance, (TProperty)value!);
        }
        else if (_values.RefSetter is { } refSet)
        {
            refSet(ref instance, (TProperty)value!);
        }
    }
}

/// <summary>Factory methods for the metadata of a type, for generated code. Reflection-based resolution uses the same methods.</summary>
public static class YamlMetadataServices
{
    /// <summary>Describes one member.</summary>
    /// <typeparam name="TDeclaring">The declaring type.</typeparam>
    /// <typeparam name="TProperty">The member type.</typeparam>
    /// <param name="values">What is known about the member.</param>
    /// <returns>The member metadata.</returns>
    public static YamlPropertyInfo CreatePropertyInfo<TDeclaring, TProperty>(YamlPropertyInfoValues<TDeclaring, TProperty> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new YamlPropertyInfoImpl<TDeclaring, TProperty>(values);
    }

    /// <summary>Describes an object type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="values">The members and how to make an instance.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<T> CreateObjectInfo<T>(YamlSerializerOptions options, YamlObjectInfoValues<T> values)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(values);
        return new YamlTypeInfo<T>(options, values);
    }

    /// <summary>Describes a type read by a given converter.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="converter">The converter.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<T> CreateValueInfo<T>(YamlSerializerOptions options, YamlConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(converter);
        return new YamlTypeInfo<T>(options, converter);
    }

    /// <summary>Describes <see cref="List{T}"/>.</summary>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="elementInfo">Metadata for the elements.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<List<TElement>> CreateListInfo<TElement>(YamlSerializerOptions options, YamlTypeInfo<TElement> elementInfo)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(elementInfo);
        return new YamlTypeInfo<List<TElement>>(options, new ListConverter<TElement>(elementInfo));
    }

    /// <summary>Describes an array.</summary>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="elementInfo">Metadata for the elements.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<TElement[]> CreateArrayInfo<TElement>(YamlSerializerOptions options, YamlTypeInfo<TElement> elementInfo)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(elementInfo);
        return new YamlTypeInfo<TElement[]>(options, new ArrayConverter<TElement>(elementInfo));
    }

    /// <summary>Describes <see cref="Dictionary{TKey, TValue}"/>.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="keyInfo">Metadata for the keys.</param>
    /// <param name="valueInfo">Metadata for the values.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<Dictionary<TKey, TValue>> CreateDictionaryInfo<TKey, TValue>(
        YamlSerializerOptions options,
        YamlTypeInfo<TKey> keyInfo,
        YamlTypeInfo<TValue> valueInfo)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyInfo);
        ArgumentNullException.ThrowIfNull(valueInfo);
        return new YamlTypeInfo<Dictionary<TKey, TValue>>(options, new DictionaryConverter<TKey, TValue>(keyInfo, valueInfo));
    }

    /// <summary>Gets the built-in converter of a type such as <see cref="int"/>, <see cref="string"/>, <see cref="Guid"/> or an enum.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="options">The options the converter will be used with.</param>
    /// <returns>The converter.</returns>
    /// <exception cref="NotSupportedException">The type has no built-in converter.</exception>
    public static YamlConverter<T> GetBuiltInConverter<T>(YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (YamlConverter<T>?)BuiltInConverters.Create(typeof(T), options)
            ?? throw new NotSupportedException($"Type '{typeof(T)}' has no built-in converter.");
    }
}
