using System;

namespace OneYaml;

/// <summary>
/// The base of a generated serialization context: a set of <see cref="YamlTypeInfo"/> for known types
/// that needs no reflection. Mark a partial class deriving from this with <see cref="YamlSerializableAttribute"/>.
/// </summary>
public abstract class YamlSerializerContext : IYamlTypeInfoResolver
{
    /// <summary>Creates a context.</summary>
    /// <param name="options">Settings to start from; the context's own options copy them and use the context as resolver.</param>
    protected YamlSerializerContext(YamlSerializerOptions? options = null)
    {
        Options = new YamlSerializerOptions
        {
            // A null policy in the given options is a choice (names as written), so only a missing options object gets the default.
            PropertyNamingPolicy = options is null ? System.Text.Json.JsonNamingPolicy.CamelCase : options.PropertyNamingPolicy,
            PropertyNameCaseInsensitive = options?.PropertyNameCaseInsensitive ?? false,
            UnmappedMemberHandling = options?.UnmappedMemberHandling ?? YamlUnmappedMemberHandling.Skip,
            Limits = options?.Limits ?? YamlLimits.Default,
            MergeKeys = options?.MergeKeys ?? false,
            IndentSize = options?.IndentSize ?? 2,
            DefaultIgnoreCondition = options?.DefaultIgnoreCondition ?? YamlIgnoreCondition.Never,
            TypeInfoResolver = this,
        };
        if (options is not null)
        {
            foreach (YamlConverter converter in options.Converters)
            {
                Options.Converters.Add(converter);
            }
        }
    }

    /// <summary>The options this context's metadata belongs to.</summary>
    public YamlSerializerOptions Options { get; }

    /// <summary>Finds the metadata for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The metadata, or <see langword="null"/> when the context does not know the type.</returns>
    public abstract YamlTypeInfo? GetTypeInfo(Type type);

    YamlTypeInfo? IYamlTypeInfoResolver.GetTypeInfo(Type type, YamlSerializerOptions options) => GetTypeInfo(type);
}

/// <summary>Asks the source generator to write metadata for a type, and the types reachable from it, into a context.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class YamlSerializableAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="type">The type to describe.</param>
    public YamlSerializableAttribute(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>The type to describe.</summary>
    public Type Type { get; }
}
