using System;
using System.Diagnostics.CodeAnalysis;

namespace OneYaml.Compat.SharpYaml.Serialization;

/// <summary>
/// Carries the options and the metadata of a set of types. SharpYaml fills it in with a source generator; this package
/// has none, so the metadata is made by reflection unless a derived class provides it.
/// </summary>
public abstract class YamlSerializerContext
{
    /// <summary>Creates a context with the default options.</summary>
    protected YamlSerializerContext()
        : this(null)
    {
    }

    /// <summary>Creates a context with options.</summary>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    protected YamlSerializerContext(YamlSerializerOptions? options)
    {
        Options = options ?? new YamlSerializerOptions();
    }

    /// <summary>The options of the context.</summary>
    public YamlSerializerOptions Options { get; }

    /// <summary>Makes options from the options of the context.</summary>
    /// <param name="configure">Changes the options; it receives a copy of the context's options.</param>
    /// <returns>The new options.</returns>
    public YamlSerializerOptions CreateOptions(Func<YamlSerializerOptions, YamlSerializerOptions>? configure = null)
    {
        YamlSerializerOptions copy = new(Options);
        return configure is null ? copy : configure(copy);
    }

    /// <summary>Gets the metadata of a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="options">The options in force.</param>
    /// <returns>The metadata.</returns>
    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    public virtual YamlTypeInfo? GetTypeInfo(Type type, YamlSerializerOptions options) =>
        ReflectionYamlTypeInfoResolver.Default.GetTypeInfo(type, options);
}
