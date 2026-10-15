using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

/// <summary>The metadata of a type: how it is read and written. Made by a resolver or a serializer context.</summary>
public abstract class YamlTypeInfo
{
    private protected YamlTypeInfo(Oy.YamlTypeInfo engine, YamlSerializerOptions options)
    {
        Engine = engine;
        Options = options;
    }

    /// <summary>The type the metadata describes.</summary>
    public Type Type => Engine.Type;

    /// <summary>The options the metadata was made for.</summary>
    public YamlSerializerOptions Options { get; }

    internal Oy.YamlTypeInfo Engine { get; }

    [RequiresUnreferencedCode("Reflection-based binding reads members and constructors that trimming can remove.")]
    [RequiresDynamicCode("Reflection-based binding makes generic types at run time.")]
    internal static YamlTypeInfo Create(Type type, YamlSerializerOptions options)
    {
        Oy.YamlTypeInfo engine = options.GetEngine().GetTypeInfo(type);
        MethodInfo make = typeof(YamlTypeInfo).GetMethod(nameof(Wrap), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        return (YamlTypeInfo)make.Invoke(null, [engine, options])!;
    }

    private static YamlTypeInfo<T> Wrap<T>(Oy.YamlTypeInfo engine, YamlSerializerOptions options) => new Wrapped<T>((Oy.YamlTypeInfo<T>)engine, options);

    private sealed class Wrapped<T> : YamlTypeInfo<T>
    {
        public Wrapped(Oy.YamlTypeInfo<T> engine, YamlSerializerOptions options)
            : base(engine, options)
        {
        }
    }
}

/// <summary>The metadata of a type that is known at compile time.</summary>
/// <typeparam name="T">The type.</typeparam>
public abstract class YamlTypeInfo<T> : YamlTypeInfo
{
    private protected YamlTypeInfo(Oy.YamlTypeInfo<T> engine, YamlSerializerOptions options)
        : base(engine, options)
    {
    }

    internal new Oy.YamlTypeInfo<T> Engine => (Oy.YamlTypeInfo<T>)base.Engine;
}

/// <summary>Describes types by reflection.</summary>
public sealed class ReflectionYamlTypeInfoResolver : IYamlTypeInfoResolver
{
    private ReflectionYamlTypeInfoResolver()
    {
    }

    /// <summary>The shared instance.</summary>
    public static ReflectionYamlTypeInfoResolver Default { get; } = new();

    /// <summary>Gets the metadata of a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="options">The options in force.</param>
    /// <returns>The metadata.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reflection-based binding is this resolver's purpose; a program that must not use it never asks for it.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reflection-based binding is this resolver's purpose; a program that must not use it never asks for it.")]
    public YamlTypeInfo? GetTypeInfo(Type type, YamlSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        return YamlTypeInfo.Create(type, options);
    }
}
