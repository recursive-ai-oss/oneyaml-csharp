using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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

    // When the member is left out of the output; set from the metadata and settled against the options' default.
    internal YamlIgnoreCondition? IgnoreCondition { get; set; }

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

    // Whether the member has a getter, so it can be written.
    internal abstract bool CanWrite { get; }

    // Writes the key and the value, unless the member is left out.
    internal abstract void Write(Utf8YamlEmitter writer, TDeclaring instance);

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

    /// <summary>Reads the member. A member with no getter is read but never written.</summary>
    public Func<TDeclaring, TProperty>? Getter { get; init; }

    /// <summary>When the member is left out of the output. When it is <see langword="null"/>, the options' default applies.</summary>
    public YamlIgnoreCondition? IgnoreCondition { get; init; }

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
    private byte[]? _prepared;
    private YamlIgnoreCondition _ignore;

    public YamlPropertyInfoImpl(YamlPropertyInfoValues<TDeclaring, TProperty> values)
        : base(values.Name, values.SerializedName, values.IsRequired, values.Order)
    {
        _values = values;
        IgnoreCondition = values.IgnoreCondition;
    }

    internal override bool CanWrite => _values.Getter is not null;

    internal override void Write(Utf8YamlEmitter writer, TDeclaring instance)
    {
        TProperty value = _values.Getter!(instance);
        if (_ignore == YamlIgnoreCondition.WhenWritingNull)
        {
            if (value is null)
            {
                return;
            }
        }
        else if (_ignore == YamlIgnoreCondition.WhenWritingDefault && EqualityComparer<TProperty>.Default.Equals(value, default!))
        {
            return;
        }

        if (_prepared is { } key)
        {
            writer.WritePreparedKey(key);
        }
        else
        {
            writer.WritePropertyName(Name);
        }

        _converter!.WriteValue(writer, value, _options!);
    }

    public override Type PropertyType => typeof(TProperty);

    internal override void Configure(YamlSerializerOptions options, int index)
    {
        ApplyName(options, index);
        _options = options;
        _ignore = IgnoreCondition ?? options.DefaultIgnoreCondition;
        _prepared = Utf8YamlEmitter.PrepareKey(NameUtf8);
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

    /// <summary>
    /// Gets the setter of an init-only property that generated code cannot reach directly, for a property declared on a
    /// generic type (for example a base class closed over a type argument).
    /// </summary>
    /// <typeparam name="TDeclaring">The type that declares the property.</typeparam>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <returns>A delegate that sets the property.</returns>
    /// <exception cref="ArgumentException">The type has no public property with that name and a public setter.</exception>
    public static Action<TDeclaring, TProperty> CreateInitOnlySetter<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] TDeclaring, TProperty>(string name)
        where TDeclaring : class
    {
        ArgumentNullException.ThrowIfNull(name);
        return InitOnlySetterMethod<TDeclaring>(name).CreateDelegate<Action<TDeclaring, TProperty>>();
    }

    /// <summary>As <see cref="CreateInitOnlySetter{TDeclaring, TProperty}"/>, for a property of a struct.</summary>
    /// <typeparam name="TDeclaring">The struct that declares the property.</typeparam>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <returns>A delegate that sets the property.</returns>
    /// <exception cref="ArgumentException">The type has no public property with that name and a public setter.</exception>
    public static YamlRefSetter<TDeclaring, TProperty> CreateInitOnlyRefSetter<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] TDeclaring, TProperty>(string name)
        where TDeclaring : struct
    {
        ArgumentNullException.ThrowIfNull(name);
        return InitOnlySetterMethod<TDeclaring>(name).CreateDelegate<YamlRefSetter<TDeclaring, TProperty>>();
    }

    private static MethodInfo InitOnlySetterMethod<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] TDeclaring>(string name) =>
        typeof(TDeclaring).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.SetMethod
        ?? throw new ArgumentException($"{typeof(TDeclaring).Name} has no public property '{name}' with a setter.", nameof(name));

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

    /// <summary>Describes an enum type, read by name or number.</summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<TEnum> CreateEnumInfo<TEnum>(YamlSerializerOptions options)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(options);
        return new YamlTypeInfo<TEnum>(options, new EnumConverter<TEnum>(options));
    }

    /// <summary>Describes <see cref="Nullable{T}"/>.</summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="underlyingInfo">Metadata for the underlying type.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<T?> CreateNullableInfo<T>(YamlSerializerOptions options, YamlTypeInfo<T> underlyingInfo)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(underlyingInfo);
        return new YamlTypeInfo<T?>(options, new NullableConverter<T>(underlyingInfo.Converter));
    }

    /// <summary>Describes <see cref="HashSet{T}"/>.</summary>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="elementInfo">Metadata for the elements.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<HashSet<TElement>> CreateHashSetInfo<TElement>(YamlSerializerOptions options, YamlTypeInfo<TElement> elementInfo)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(elementInfo);
        return new YamlTypeInfo<HashSet<TElement>>(options, new CollectionConverter<HashSet<TElement>, TElement>(elementInfo, static l => [.. l]));
    }

    /// <summary>Describes a collection or dictionary interface such as <see cref="IList{T}"/>, read as a concrete type that implements it.</summary>
    /// <typeparam name="TInterface">The interface type.</typeparam>
    /// <typeparam name="TImplementation">The concrete type that is made, such as <see cref="List{T}"/>.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="implementationInfo">Metadata for the concrete type.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<TInterface> CreateInterfaceInfo<TInterface, TImplementation>(YamlSerializerOptions options, YamlTypeInfo<TImplementation> implementationInfo)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(implementationInfo);
        return new YamlTypeInfo<TInterface>(options, new AdaptedConverter<TInterface, TImplementation>(implementationInfo.Converter));
    }

    /// <summary>Describes a dictionary type other than <see cref="Dictionary{TKey, TValue}"/>, made through its parameterless constructor.</summary>
    /// <typeparam name="TDictionary">The dictionary type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="keyInfo">Metadata for the keys.</param>
    /// <param name="valueInfo">Metadata for the values.</param>
    /// <param name="create">Makes an empty dictionary.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<TDictionary> CreateCustomDictionaryInfo<TDictionary, TKey, TValue>(
        YamlSerializerOptions options,
        YamlTypeInfo<TKey> keyInfo,
        YamlTypeInfo<TValue> valueInfo,
        Func<TDictionary> create)
        where TDictionary : class, IDictionary<TKey, TValue>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyInfo);
        ArgumentNullException.ThrowIfNull(valueInfo);
        ArgumentNullException.ThrowIfNull(create);
        return new YamlTypeInfo<TDictionary>(options, new GeneralDictionaryConverter<TDictionary, TKey, TValue>(keyInfo, valueInfo, create));
    }

    /// <summary>Describes a collection type other than <see cref="List{T}"/>, made through its parameterless constructor.</summary>
    /// <typeparam name="TCollection">The collection type.</typeparam>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <param name="elementInfo">Metadata for the elements.</param>
    /// <param name="create">Makes an empty collection.</param>
    /// <returns>The metadata.</returns>
    public static YamlTypeInfo<TCollection> CreateCustomCollectionInfo<TCollection, TElement>(
        YamlSerializerOptions options,
        YamlTypeInfo<TElement> elementInfo,
        Func<TCollection> create)
        where TCollection : class, ICollection<TElement>
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(elementInfo);
        ArgumentNullException.ThrowIfNull(create);
        return new YamlTypeInfo<TCollection>(options, new CollectionConverter<TCollection, TElement>(elementInfo, list =>
        {
            TCollection collection = create();
            foreach (TElement item in list)
            {
                collection.Add(item);
            }

            return collection;
        }));
    }

    /// <summary>Finds a converter in <see cref="YamlSerializerOptions.Converters"/> that handles the type, which takes precedence over generated metadata.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="options">The options the metadata belongs to.</param>
    /// <returns>The metadata for the converter, or <see langword="null"/> when no custom converter handles the type.</returns>
    public static YamlTypeInfo<T>? TryCreateCustomConverterInfo<T>(YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (YamlConverter c in options.Converters)
        {
            if (!c.CanConvert(typeof(T)))
            {
                continue;
            }

            YamlConverter? chosen = c is YamlConverterFactory factory ? factory.CreateConverter(typeof(T), options) : c;
            if (chosen is null)
            {
                continue;
            }

            return chosen is YamlConverter<T> typed
                ? new YamlTypeInfo<T>(options, typed)
                : throw new InvalidOperationException($"The converter {chosen.GetType()} cannot convert '{typeof(T)}' because it does not derive from YamlConverter<{typeof(T).Name}>.");
        }

        return null;
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
