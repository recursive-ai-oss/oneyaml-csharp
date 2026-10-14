using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OneYaml;

/// <summary>What to do with a mapping key that matches no member of the target type.</summary>
public enum YamlUnmappedMemberHandling
{
    /// <summary>Ignore the key and its value.</summary>
    Skip = 0,

    /// <summary>Fail with a binding error.</summary>
    Disallow,
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

    /// <summary>The limits to enforce while reading. The default is <see cref="YamlLimits.Default"/>.</summary>
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
    /// <see langword="null"/>, metadata comes from reflection.
    /// </summary>
    public IYamlTypeInfoResolver? TypeInfoResolver
    {
        get => _resolver;
        init => _resolver = value;
    }

    /// <summary>Gets the metadata for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="NotSupportedException">No resolver knows the type.</exception>
    public YamlTypeInfo GetTypeInfo(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        MakeReadOnly();
        if (_typeInfos.TryGetValue(type, out YamlTypeInfo? known))
        {
            return known;
        }

        YamlTypeInfo? info = null;
        if (_resolver is not null)
        {
            info = _resolver.GetTypeInfo(type, this);
        }
        else
        {
            info = ReflectionResolver.GetOrNull()?.GetTypeInfo(type, this);
        }

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
