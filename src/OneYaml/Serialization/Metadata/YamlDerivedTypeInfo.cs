using System;
using System.Runtime.CompilerServices;

namespace OneYaml.Serialization.Metadata;

/// <summary>One derived type of a polymorphic base, for generated metadata. Make it with <see cref="YamlMetadataServices.CreateDerivedTypeInfo{TBase, TDerived}"/>.</summary>
/// <typeparam name="TBase">The polymorphic base.</typeparam>
public abstract class YamlDerivedTypeInfo<TBase>
{
    internal YamlDerivedTypeInfo(Type derivedType, string discriminator)
    {
        DerivedType = derivedType;
        TypeDiscriminator = discriminator;
    }

    /// <summary>The derived type.</summary>
    public Type DerivedType { get; }

    /// <summary>The discriminator that stands for the type, as given.</summary>
    public string TypeDiscriminator { get; }

    // The discriminator as it is compared with a key's value, or with the tag.
    internal byte[] DiscriminatorUtf8 { get; set; } = [];

    internal string Tag { get; set; } = string.Empty;

    // Finds the metadata of the derived type; false when it is not a type that binds as an object.
    internal abstract void Resolve(bool needsObject);

    internal abstract void WriteKeyed(Utf8YamlEmitter writer, TBase value, string key);

    internal abstract void WriteTagged(Utf8YamlEmitter writer, TBase value, YamlSerializerOptions options);

    internal abstract TBase? ReadAfterKey(ref Utf8YamlReader reader, YamlSerializerOptions options, int start, byte[] key);

    internal abstract TBase? ReadKeyAhead(ref Utf8YamlReader reader, YamlSerializerOptions options, int start, byte[] key);

    internal abstract TBase? ReadWhole(ref Utf8YamlReader reader, YamlSerializerOptions options);
}

internal sealed class YamlDerivedTypeInfoImpl<TBase, TDerived> : YamlDerivedTypeInfo<TBase>
    where TDerived : TBase
{
    private readonly Func<YamlTypeInfo<TDerived>> _info;
    private YamlTypeInfo<TDerived>? _resolved;

    public YamlDerivedTypeInfoImpl(string discriminator, Func<YamlTypeInfo<TDerived>> info)
        : base(typeof(TDerived), discriminator)
    {
        _info = info;
    }

    internal override void Resolve(bool needsObject)
    {
        _resolved ??= _info();
        if (needsObject && _resolved.ObjectBinder is null)
        {
            throw new NotSupportedException($"The derived type '{typeof(TDerived)}' of '{typeof(TBase)}' cannot be told apart by a key: it does not bind as a mapping of members. Use a tag discriminator, or give the type members.");
        }
    }

    internal override void WriteKeyed(Utf8YamlEmitter writer, TBase value, string key) =>
        _resolved!.ObjectBinder!.WriteDiscriminated(writer, (TDerived)(object)value!, key, TypeDiscriminator);

    internal override void WriteTagged(Utf8YamlEmitter writer, TBase value, YamlSerializerOptions options)
    {
        writer.WriteTag(Tag);
        _resolved!.Converter.WriteValue(writer, (TDerived)(object)value!, options);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override TBase? ReadAfterKey(ref Utf8YamlReader reader, YamlSerializerOptions options, int start, byte[] key) =>
        _resolved!.ObjectBinder!.ReadAfterDiscriminator(ref reader, options, start, key);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override TBase? ReadKeyAhead(ref Utf8YamlReader reader, YamlSerializerOptions options, int start, byte[] key) =>
        _resolved!.ObjectBinder!.ReadWithDiscriminatorAhead(ref reader, options, start, key);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override TBase? ReadWhole(ref Utf8YamlReader reader, YamlSerializerOptions options) =>
        _resolved!.Converter.ReadValue(ref reader, options);
}
