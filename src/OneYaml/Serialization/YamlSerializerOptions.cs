using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;

namespace OneYaml;

/// <summary>What to do with a mapping key that matches no member of the target type.</summary>
public enum YamlUnmappedMemberHandling
{
    /// <summary>Ignore the key and its value.</summary>
    Skip = 0,

    /// <summary>Fail with a binding error.</summary>
    Disallow,
}

/// <summary>When a member is left out of the output.</summary>
public enum YamlIgnoreCondition
{
    /// <summary>The member is always written (on a member: use the options' default).</summary>
    Never = 0,

    /// <summary>The member is never read or written. Only meaningful on a member.</summary>
    Always = 1,

    /// <summary>The member is left out when its value is the default for its type.</summary>
    WhenWritingDefault = 2,

    /// <summary>The member is left out when its value is <see langword="null"/>.</summary>
    WhenWritingNull = 3,
}

/// <summary>Supplies the metadata the serializer needs for a type.</summary>
public interface IYamlTypeInfoResolver
{
    /// <summary>Finds the metadata for a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="options">The options the metadata will be used with.</param>
    /// <returns>The metadata, or <see langword="null"/> when this resolver does not know the type.</returns>
    YamlTypeInfo? GetTypeInfo(Type type, YamlSerializerOptions options);
}

/// <summary>
/// Options for reading and writing YAML. An instance is read-only once it has been used: the
/// settings are <c>init</c>-only and the converter list is locked on first use.
/// </summary>
public sealed class YamlSerializerOptions
{
    private static readonly YamlSerializerOptions s_default = CreateDefault();

    private readonly ConcurrentDictionary<Type, YamlTypeInfo> _typeInfos = new();
    private readonly ConverterList _converters = new();
    private JsonNamingPolicy? _namingPolicy = JsonNamingPolicy.CamelCase;
    private IYamlTypeInfoResolver? _resolver;
    private YamlLimits _limits = YamlLimits.Default;
    private int _indentSize = 2;
    private YamlIgnoreCondition _defaultIgnore;
    private bool _used;

    /// <summary>Creates options with the defaults.</summary>
    public YamlSerializerOptions()
    {
    }

    /// <summary>Options with all defaults. They cannot be changed.</summary>
    public static YamlSerializerOptions Default => s_default;

    /// <summary>
    /// How CLR member names become mapping keys, for reading and writing. The default is camel case,
    /// so <c>Replicas</c> binds <c>replicas</c>. <see langword="null"/> keeps names as written.
    /// </summary>
    public JsonNamingPolicy? PropertyNamingPolicy
    {
        get => _namingPolicy;
        init => _namingPolicy = value;
    }

    /// <summary>Whether keys match member names without regard to case, after the naming policy. Off by default.</summary>
    public bool PropertyNameCaseInsensitive { get; init; }

    /// <summary>What to do with keys that match no member. The default is to skip them.</summary>
    public YamlUnmappedMemberHandling UnmappedMemberHandling { get; init; }

    /// <summary>The number of spaces one level of nesting adds when writing, from 1 to 8. The default is 2.</summary>
    public int IndentSize
    {
        get => _indentSize;
        init => _indentSize = value is >= 1 and <= 8 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The indent size must be between 1 and 8.");
    }

    /// <summary>
    /// Which members are left out when writing: never (the default), when null, or when the default of their type.
    /// A member's own condition takes precedence. <see cref="YamlIgnoreCondition.Always"/> is not allowed here.
    /// </summary>
    public YamlIgnoreCondition DefaultIgnoreCondition
    {
        get => _defaultIgnore;
        init => _defaultIgnore = value is YamlIgnoreCondition.Always
            ? throw new ArgumentException("Always cannot be the default for every member.", nameof(value))
            : value;
    }

    /// <summary>The limits to enforce while reading and writing. The default is <see cref="YamlLimits.Default"/>.</summary>
    public YamlLimits Limits
    {
        get => _limits;
        init => _limits = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether the YAML 1.1 merge key <c>&lt;&lt;</c> merges mappings. Off by default, where it is an
    /// ordinary key.
    /// </summary>
    public bool MergeKeys { get; init; }

    /// <summary>Custom converters, tried in order before the built-in ones. Locked once the options are used.</summary>
    public IList<YamlConverter> Converters => _converters;

    /// <summary>
    /// Supplies type metadata, typically a generated <see cref="YamlSerializerContext"/>. When it is
    /// <see langword="null"/>, the reflection-based <see cref="YamlSerializer"/> overloads use a
    /// <see cref="DefaultYamlTypeInfoResolver"/> unless reflection is turned off for the application.
    /// </summary>
    public IYamlTypeInfoResolver? TypeInfoResolver
    {
        get => Volatile.Read(ref _resolver);
        init => _resolver = value;
    }

    /// <summary>Gets the metadata for a type from <see cref="TypeInfoResolver"/>.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The metadata.</returns>
    /// <remarks>
    /// This never falls back to reflection: set <see cref="TypeInfoResolver"/> to a generated context, or to a
    /// <see cref="DefaultYamlTypeInfoResolver"/> to opt in to reflection. The reflection-based
    /// <see cref="YamlSerializer"/> overloads install that resolver themselves when none is set.
    /// </remarks>
    /// <exception cref="NotSupportedException">The resolver does not know the type.</exception>
    /// <exception cref="InvalidOperationException">No resolver is set.</exception>
    public YamlTypeInfo GetTypeInfo(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        MakeReadOnly();
        if (_typeInfos.TryGetValue(type, out YamlTypeInfo? known))
        {
            return known;
        }

        IYamlTypeInfoResolver? resolver = Volatile.Read(ref _resolver);
        if (resolver is null)
        {
            throw new InvalidOperationException("These options have no TypeInfoResolver. Set one to a generated YamlSerializerContext, or to a DefaultYamlTypeInfoResolver to use reflection.");
        }

        YamlTypeInfo? info = resolver.GetTypeInfo(type, this);
        if (info is null)
        {
            throw new NotSupportedException($"No metadata is available for type '{type}'. Add it to a YamlSerializerContext or use reflection-based options.");
        }

        return _typeInfos.GetOrAdd(type, info);
    }

    /// <summary>Gets the metadata for a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <returns>The metadata.</returns>
    public YamlTypeInfo<T> GetTypeInfo<T>() => (YamlTypeInfo<T>)GetTypeInfo(typeof(T));

    /// <summary>Gets the converter used for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The converter.</returns>
    public YamlConverter GetConverter(Type type) => GetTypeInfo(type).ConverterBase;

    // The reflection-based serializer overloads come through here. They carry the trimming and AOT
    // annotations, so a caller that reaches this has accepted reflection; a program that switched
    // reflection off (as trimmed and ahead-of-time compiled ones do by default) gets a clear error.
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    internal YamlTypeInfo GetReflectiveTypeInfo(Type type)
    {
        EnsureResolver();
        return GetTypeInfo(type);
    }

    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    internal YamlTypeInfo<T> GetReflectiveTypeInfo<T>() => (YamlTypeInfo<T>)GetReflectiveTypeInfo(typeof(T));

    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    private void EnsureResolver()
    {
        if (Volatile.Read(ref _resolver) is not null)
        {
            return;
        }

        if (!YamlSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException("Reflection-based binding is turned off in this application. Pass generated metadata from a YamlSerializerContext, set TypeInfoResolver, or set the OneYaml.YamlSerializer.IsReflectionEnabledByDefault switch to true.");
        }

        Interlocked.CompareExchange(ref _resolver, new DefaultYamlTypeInfoResolver(), null);
    }

    private static YamlSerializerOptions CreateDefault()
    {
        var options = new YamlSerializerOptions();
        options.MakeReadOnly();
        return options;
    }

    internal void MakeReadOnly()
    {
        if (!_used)
        {
            _used = true;
            _converters.Lock();
        }
    }

    internal void Register(Type type, YamlTypeInfo info) => _typeInfos.TryAdd(type, info);

    internal bool TryGetCached(Type type, [NotNullWhen(true)] out YamlTypeInfo? info) => _typeInfos.TryGetValue(type, out info);

    private sealed class ConverterList : Collection<YamlConverter>
    {
        private bool _locked;

        public void Lock() => _locked = true;

        protected override void InsertItem(int index, YamlConverter item)
        {
            ArgumentNullException.ThrowIfNull(item);
            Check();
            base.InsertItem(index, item);
        }

        protected override void SetItem(int index, YamlConverter item)
        {
            ArgumentNullException.ThrowIfNull(item);
            Check();
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            Check();
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            Check();
            base.ClearItems();
        }

        private void Check()
        {
            if (_locked)
            {
                throw new InvalidOperationException("The options have been used and can no longer be changed.");
            }
        }
    }
}
