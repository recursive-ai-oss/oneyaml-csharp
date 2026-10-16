using System;
using System.Collections.Generic;
using System.Text;
using OneYaml.Parsing;
using OneYaml.Serialization.Metadata;

namespace OneYaml;

/// <summary>Converts a property, field, type or enum member with the converter named by the attribute, instead of the built-in one.</summary>
/// <remarks>
/// <para>
/// The converter type needs a public parameterless constructor and derives from <see cref="YamlConverter{T}"/> for the type
/// it is applied to (or from <see cref="YamlConverterFactory"/>). On a generic type it may be an open generic type whose
/// type parameters match those of the type, as in <c>[YamlConverter(typeof(BoxConverter&lt;&gt;))]</c>.
/// </para>
/// <para>
/// A converter on a member wins over a converter in <see cref="YamlSerializerOptions.Converters"/>, which wins over a converter on the
/// type. The same holds for reflection and for source-generated metadata.
/// </para>
/// <para>
/// System.Text.Json's <c>JsonConverterAttribute</c> is not honoured: its converters read and write JSON tokens.
/// See <see cref="YamlPropertyNameAttribute"/> for the System.Text.Json attributes that are.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlConverterAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="converterType">The converter type.</param>
    public YamlConverterAttribute([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type converterType)
    {
        ArgumentNullException.ThrowIfNull(converterType);
        ConverterType = converterType;
    }

    /// <summary>The converter type.</summary>
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ConverterType { get; }
}

/// <summary>
/// Marks the member that collects the keys of a mapping that no other member takes, and writes them back after the others.
/// The member is a <c>Dictionary&lt;string, object?&gt;</c> or an <c>IDictionary&lt;string, object?&gt;</c>; the values are
/// read the way untyped values are (text, numbers, booleans, <see cref="List{T}"/> and <see cref="Dictionary{TKey, TValue}"/>).
/// </summary>
/// <remarks>
/// A type has at most one such member. When the dictionary is <see langword="null"/> after reading, one is made for the member, so
/// give it a setter, an initializer, or a constructor parameter. When the type has extension data, an unmapped key is
/// collected instead of skipped or refused by <see cref="YamlUnmappedMemberHandling.Disallow"/>.
/// System.Text.Json's <c>JsonExtensionDataAttribute</c> on a member of such a type does the same where this attribute is absent.
/// See <see cref="YamlPropertyNameAttribute"/> for the System.Text.Json attributes that are honoured.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlExtensionDataAttribute : Attribute
{
}

/// <summary>How a derived type of a polymorphic base is told apart in YAML.</summary>
public enum YamlDiscriminatorKind
{
    /// <summary>
    /// A key of the mapping, <c>$type</c> unless <see cref="YamlPolymorphicAttribute.TypeDiscriminatorPropertyName"/> says otherwise.
    /// It must be the first key of the mapping, and the derived types must bind as mappings.
    /// </summary>
    Key = 0,

    /// <summary>A tag on the node, such as <c>!dog</c>. The derived types may be mappings, sequences or scalars.</summary>
    Tag = 1,
}

/// <summary>
/// Makes a base class or interface polymorphic: a value is written with a discriminator that names its derived type, and
/// read back as that type. The derived types are listed with <see cref="YamlDerivedTypeAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator is written only when the declared type is the base: a member of type <c>Animal</c> gets it, a root value
/// serialized as <c>Dog</c> does not. When no discriminator is read, the base type is read as itself if it can be created and
/// an error otherwise; a discriminator that no derived type has is a <see cref="YamlErrorCategory.Binding"/> error with the path.
/// </para>
/// <para>
/// <see cref="YamlDerivedTypeAttribute"/> alone, without this attribute, makes the type polymorphic with the key <c>$type</c>.
/// System.Text.Json's <c>JsonPolymorphicAttribute</c> and <c>JsonDerivedTypeAttribute</c> do the same where these are absent
/// (always with a key). See <see cref="YamlPropertyNameAttribute"/> for the System.Text.Json attributes that are honoured.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class YamlPolymorphicAttribute : Attribute
{
    /// <summary>Whether the discriminator is a key or a tag. The default is <see cref="YamlDiscriminatorKind.Key"/>.</summary>
    public YamlDiscriminatorKind Discriminator { get; set; }

    /// <summary>The key that holds the discriminator, for <see cref="YamlDiscriminatorKind.Key"/>. The default is <c>$type</c>.</summary>
    public string TypeDiscriminatorPropertyName { get; set; } = "$type";
}

/// <summary>Lists one derived type of a polymorphic base, and the discriminator that stands for it.</summary>
/// <remarks>
/// For a key discriminator the value is the text written under the key. For a tag discriminator it is the tag: <c>!dog</c>, or
/// <c>dog</c>, which is the same tag. See <see cref="YamlPolymorphicAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class YamlDerivedTypeAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="derivedType">The derived type; it must derive from, or implement, the type the attribute is on.</param>
    /// <param name="discriminator">The discriminator that stands for the type.</param>
    public YamlDerivedTypeAttribute(Type derivedType, string discriminator)
    {
        ArgumentNullException.ThrowIfNull(derivedType);
        ArgumentNullException.ThrowIfNull(discriminator);
        DerivedType = derivedType;
        Discriminator = discriminator;
    }

    /// <summary>The derived type.</summary>
    public Type DerivedType { get; }

    /// <summary>The discriminator that stands for the type.</summary>
    public string Discriminator { get; }
}

// Reads and writes a base type by the discriminator of its derived types.
internal sealed class PolymorphicConverter<TBase> : YamlConverter<TBase>
{
    private readonly YamlTypeInfo<TBase>? _baseInfo;
    private readonly YamlDiscriminatorKind _kind;
    private readonly string _keyName;
    private readonly byte[] _keyUtf8;
    private readonly YamlDerivedTypeInfo<TBase>[] _derived;
    private readonly object _gate = new();
    private volatile bool _resolved;
    private Dictionary<Type, YamlDerivedTypeInfo<TBase>> _byType = [];
    private Dictionary<string, YamlDerivedTypeInfo<TBase>> _byTag = [];

    public PolymorphicConverter(YamlTypeInfo<TBase>? baseInfo, YamlDiscriminatorKind kind, string keyName, YamlDerivedTypeInfo<TBase>[] derived)
    {
        _baseInfo = baseInfo;
        _kind = kind;
        _keyName = keyName;
        _keyUtf8 = Encoding.UTF8.GetBytes(keyName);
        _derived = derived;
    }

    // The metadata of the derived types is found on first use, so that types which refer to one another can be described.
    private void Resolve()
    {
        lock (_gate)
        {
            if (_resolved)
            {
                return;
            }

            var byType = new Dictionary<Type, YamlDerivedTypeInfo<TBase>>();
            var byTag = new Dictionary<string, YamlDerivedTypeInfo<TBase>>(StringComparer.Ordinal);
            var seenText = new HashSet<string>(StringComparer.Ordinal);
            foreach (YamlDerivedTypeInfo<TBase> d in _derived)
            {
                d.Resolve(needsObject: _kind == YamlDiscriminatorKind.Key);
                if (!byType.TryAdd(d.DerivedType, d))
                {
                    throw new InvalidOperationException($"The derived type '{d.DerivedType}' is listed twice for '{typeof(TBase)}'.");
                }

                if (!seenText.Add(d.Discriminator))
                {
                    throw new InvalidOperationException($"The discriminator '{d.Discriminator}' is used by two derived types of '{typeof(TBase)}'.");
                }

                d.DiscriminatorUtf8 = Encoding.UTF8.GetBytes(d.Discriminator);
                d.Tag = d.Discriminator.StartsWith('!') ? d.Discriminator : "!" + d.Discriminator;
                if (_kind == YamlDiscriminatorKind.Tag && !byTag.TryAdd(d.Tag, d))
                {
                    throw new InvalidOperationException($"The tag '{d.Tag}' is used by two derived types of '{typeof(TBase)}'.");
                }
            }

            _byType = byType;
            _byTag = byTag;
            _resolved = true;
        }
    }

    public override void Write(Utf8YamlEmitter writer, TBase value, YamlSerializerOptions options)
    {
        if (!_resolved)
        {
            Resolve();
        }

        Type runtime = value!.GetType();
        if (_byType.TryGetValue(runtime, out YamlDerivedTypeInfo<TBase>? derived))
        {
            if (_kind == YamlDiscriminatorKind.Key)
            {
                derived.WriteKeyed(writer, value, _keyName);
            }
            else
            {
                derived.WriteTagged(writer, value, options);
            }

            return;
        }

        if (runtime == typeof(TBase) && _baseInfo is not null)
        {
            _baseInfo.Converter.Write(writer, value, options);
            return;
        }

        throw Utf8YamlEmitter.Misuse($"The type '{runtime}' is not listed as a derived type of '{typeof(TBase)}', so it has no discriminator to write.");
    }

    public override TBase? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (!_resolved)
        {
            Resolve();
        }

        return _kind == YamlDiscriminatorKind.Key ? ReadByKey(ref reader, options) : ReadByTag(ref reader, options);
    }

    private TBase? ReadByTag(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (reader.TagKind == TagClass.Unknown && reader.Tag is { } tag)
        {
            if (!_byTag.TryGetValue(tag, out YamlDerivedTypeInfo<TBase>? derived))
            {
                throw reader.CreateException($"The tag '{tag}' is not a derived type of {ConverterGuards.DescribeType(typeof(TBase))}.");
            }

            return derived.ReadWhole(ref reader, options);
        }

        return ReadBase(ref reader, options, "has no tag that names a derived type");
    }

    private TBase? ReadByKey(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (reader.Type != YamlTokenType.MappingStart)
        {
            return ReadBase(ref reader, options, "is not a mapping, which a key discriminator needs");
        }

        ConverterGuards.EnsureStack(in reader);
        int start = reader.Start;
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (reader.Type == YamlTokenType.Scalar && KeyIs(ref reader, _keyUtf8))
        {
            return ReadDerived(ref reader, options, start);
        }

        // Not the discriminator: the base is read as itself, from the key already reached.
        if (_baseInfo?.ObjectBinder is { } baseObject)
        {
            return baseObject.ReadFromFirstKey(ref reader, options, start);
        }

        throw reader.CreateException($"The mapping of {ConverterGuards.DescribeType(typeof(TBase))} must start with the key '{_keyName}', which names the derived type.");
    }

    private TBase? ReadDerived(ref Utf8YamlReader reader, YamlSerializerOptions options, int start)
    {
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (reader.Type != YamlTokenType.Scalar)
        {
            throw reader.CreateException($"The key '{_keyName}' needs a text value that names a derived type.");
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[128];
            ReadOnlySpan<byte> text = reader.Text(scratch, ref rented);
            foreach (YamlDerivedTypeInfo<TBase> d in _derived)
            {
                if (text.SequenceEqual(d.DiscriminatorUtf8))
                {
                    return d.ReadAfterKey(ref reader, options, start);
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        throw reader.CreateException($"{reader.DescribeValue()} is not a known value of '{_keyName}' for {ConverterGuards.DescribeType(typeof(TBase))}.");
    }

    private static bool KeyIs(ref Utf8YamlReader reader, byte[] name)
    {
        if (!reader.Escaped)
        {
            return reader.Source.Slice(reader.ContentStart, reader.ContentEnd - reader.ContentStart).SequenceEqual(name);
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[128];
            return reader.Text(scratch, ref rented).SequenceEqual(name);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private TBase? ReadBase(ref Utf8YamlReader reader, YamlSerializerOptions options, string why)
    {
        if (_baseInfo is not null && _baseInfo.CanReadAsBase)
        {
            return _baseInfo.Converter.ReadValue(ref reader, options);
        }

        throw reader.CreateException($"{ConverterGuards.DescribeType(typeof(TBase))} cannot be read: the value {why}, and the type itself cannot be created.");
    }
}
