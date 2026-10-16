using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// collected instead of skipped or refused by <see cref="YamlUnmappedMemberHandling.Disallow"/>. When the type is written, a
/// key of the dictionary that a member (or the key naming a derived type) already writes is left out, so that the output
/// reads back.
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
    /// It may be anywhere in the mapping, and the derived types must bind as mappings. When it is not the first key, the reader
    /// looks ahead for it. Every node passed while looking counts against the alias node budget of <see cref="YamlLimits"/>,
    /// once for each enclosing mapping that also looks ahead (the nodes an alias brought in were counted when it was read, and
    /// the end of a collection counts for nothing). Deeply nested types that all name their type last, or a large value before
    /// the key, can therefore end in a <see cref="YamlErrorCategory.Limit"/> error that the same document with the key first does not.
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
/// (always with a key). The key those name with <c>TypeDiscriminatorPropertyName</c> is used only when no YAML attribute is
/// present; when one is, the key is the one set here, <c>$type</c> by default. The key may be anywhere in the mapping, and
/// a repeat of it is a <see cref="YamlErrorCategory.DuplicateKey"/> error. See <see cref="YamlPropertyNameAttribute"/> for the
/// System.Text.Json attributes that are honoured.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class YamlPolymorphicAttribute : Attribute
{
    /// <summary>Whether the discriminator is a key or a tag. The default is <see cref="YamlDiscriminatorKind.Key"/>.</summary>
    public YamlDiscriminatorKind DiscriminatorKind { get; set; }

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
        TypeDiscriminator = discriminator;
    }

    /// <summary>The derived type.</summary>
    public Type DerivedType { get; }

    /// <summary>The discriminator that stands for the type: the text under the key, or the tag.</summary>
    public string TypeDiscriminator { get; }
}

// Reads and writes a base type by the discriminator of its derived types.
internal sealed class PolymorphicConverter<TBase> : YamlConverter<TBase>
{
    private readonly YamlTypeInfo<TBase>? _baseInfo;
    private readonly YamlDiscriminatorKind _kind;
    private readonly string _keyName;
    private readonly byte[] _keyUtf8;
    private readonly YamlDerivedTypeInfo<TBase>[] _derived;
    private readonly Dictionary<Type, YamlDerivedTypeInfo<TBase>> _byType = [];
    private readonly Dictionary<string, YamlDerivedTypeInfo<TBase>> _byTag = [];
    private readonly object _gate = new();
    private volatile bool _resolved;

    public PolymorphicConverter(YamlTypeInfo<TBase>? baseInfo, YamlDiscriminatorKind kind, string keyName, YamlDerivedTypeInfo<TBase>[] derived)
    {
        _baseInfo = baseInfo;
        _kind = kind;
        _keyName = keyName;
        _keyUtf8 = Encoding.UTF8.GetBytes(keyName);
        _derived = derived;

        // What can be checked from the listing alone is checked now, so a mistake in it shows when the metadata is made.
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        foreach (YamlDerivedTypeInfo<TBase> d in derived)
        {
            if (!_byType.TryAdd(d.DerivedType, d))
            {
                throw new NotSupportedException($"The derived type '{d.DerivedType}' is listed twice for '{typeof(TBase)}'.");
            }

            if (!seenText.Add(d.TypeDiscriminator))
            {
                throw new NotSupportedException($"The discriminator '{d.TypeDiscriminator}' is used by two derived types of '{typeof(TBase)}'.");
            }

            d.DiscriminatorUtf8 = Encoding.UTF8.GetBytes(d.TypeDiscriminator);
            d.Tag = d.TypeDiscriminator.StartsWith('!') ? d.TypeDiscriminator : "!" + d.TypeDiscriminator;
            if (kind == YamlDiscriminatorKind.Tag && !_byTag.TryAdd(d.Tag, d))
            {
                throw new NotSupportedException($"The tag '{d.Tag}' is used by two derived types of '{typeof(TBase)}'.");
            }
        }
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

            foreach (YamlDerivedTypeInfo<TBase> d in _derived)
            {
                d.Resolve(needsObject: _kind == YamlDiscriminatorKind.Key);
            }

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

    // Like the other methods that recurse once per level, this and the readers below are compiled fully optimised from the
    // start: a document read once never leaves the first, larger code, and the depth limit has to fit on a 1 MB stack.
    //
    // The key case is spelled out here and finds the derived type in a helper that returns before anything recurses. A call
    // with this many arguments cannot always be turned into a jump (on Windows only four go in registers), so a frame that
    // only passes the call on would stay on the stack for every level: this one holds an index and a flag, and the next
    // frame is the derived type's reader.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override TBase? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
    {
        if (!_resolved)
        {
            Resolve();
        }

        if (_kind != YamlDiscriminatorKind.Key)
        {
            return ReadByTag(ref reader, options);
        }

        if (reader.Type != YamlTokenType.MappingStart)
        {
            return ReadBase(ref reader, options, "is not a mapping, which a key discriminator needs");
        }

        ConverterGuards.EnsureStack(in reader);
        int start = reader.Start;
        int found = FindDerived(ref reader);
        if (found < 0)
        {
            return ReadBaseFromKey(ref reader, options, start);
        }

        return (found & 1) != 0 ? _derived[found >> 1].ReadKeyAhead(ref reader, options, start, _keyUtf8) : _derived[found >> 1].ReadAfterKey(ref reader, options, start, _keyUtf8);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private TBase? ReadByTag(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (reader.TagKind == TagClass.Unknown)
        {
            YamlDerivedTypeInfo<TBase>? derived = FindByTag(ref reader);
            if (derived is not null)
            {
                return derived.ReadWhole(ref reader, options);
            }
        }

        return ReadBase(ref reader, options, "has no tag that names a derived type");
    }

    // Out of line: the tag string and the message are not in the frame that recurses.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private YamlDerivedTypeInfo<TBase>? FindByTag(ref Utf8YamlReader reader)
    {
        if (reader.Tag is not { } tag)
        {
            return null;
        }

        if (!_byTag.TryGetValue(tag, out YamlDerivedTypeInfo<TBase>? derived))
        {
            // The tag is text from the input: cut it, so a huge or forged tag does not end up whole in the message.
            throw reader.CreateException($"The tag '{YamlException.Excerpt(tag, YamlException.MessageExcerpt)}' is not a derived type of {ConverterGuards.DescribeType(typeof(TBase))}.");
        }

        return derived;
    }

    // Reads up to the value of the key that names the derived type and returns twice the index of that type, plus one when the
    // key was found by looking ahead. The reader is left on the value when the key was the first one, and put back on the first key
    // when it was found by looking ahead. -1: no key names a derived type; the reader is back on the first key. (A result and
    // not an out parameter: a local whose address is taken keeps a call from becoming a jump.)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FindDerived(ref Utf8YamlReader reader)
    {
        if (!reader.Read())
        {
            throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
        }

        if (reader.Type == YamlTokenType.Scalar && ConverterGuards.KeyEquals(ref reader, _keyUtf8))
        {
            if (!reader.Read())
            {
                throw reader.Error(YamlErrorCategory.Syntax, "The mapping ends without a value.");
            }

            return MatchValue(ref reader) << 1;
        }

        // The discriminator is not the first key. Looking ahead costs nothing for the common case, where it is first.
        bool looked = reader.Type == YamlTokenType.Scalar;
        if (looked && reader.LookForKey(_keyUtf8))
        {
            int index = MatchValue(ref reader);
            reader.RewindLook();
            return (index << 1) | 1;
        }

        if (looked)
        {
            reader.RewindLook();
        }

        return -1;
    }

    private int MatchValue(ref Utf8YamlReader reader)
    {
        if (reader.Type != YamlTokenType.Scalar)
        {
            throw NeedsText(in reader);
        }

        int index = Match(ref reader);
        if (index < 0)
        {
            throw UnknownValue(in reader);
        }

        return index;
    }

    // No key that names a derived type: the base is read as itself, from the key already reached.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TBase? ReadBaseFromKey(ref Utf8YamlReader reader, YamlSerializerOptions options, int start)
    {
        if (_baseInfo?.ObjectBinder is { } baseObject)
        {
            return baseObject.ReadFromFirstKey(ref reader, options, start);
        }

        throw MustHaveKey(in reader, start);
    }

    // The value is compared with the listed discriminators out of line, so its scratch space is not in the frame that recurses.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Match(ref Utf8YamlReader reader)
    {
        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[128];
            ReadOnlySpan<byte> text = reader.Text(scratch, ref rented);
            for (int i = 0; i < _derived.Length; i++)
            {
                if (text.SequenceEqual(_derived[i].DiscriminatorUtf8))
                {
                    return i;
                }
            }

            return -1;
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private YamlException NeedsText(in Utf8YamlReader reader) =>
        reader.CreateException($"The key '{_keyName}' needs a text value that names a derived type.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private YamlException UnknownValue(in Utf8YamlReader reader) =>
        reader.CreateException($"{reader.DescribeValue()} is not a known value of '{_keyName}' for {ConverterGuards.DescribeType(typeof(TBase))}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private YamlException MustHaveKey(in Utf8YamlReader reader, int mappingStart) =>
        reader.ErrorAt(YamlErrorCategory.Binding, $"The mapping of {ConverterGuards.DescribeType(typeof(TBase))} has no key '{_keyName}', which names the derived type.", mappingStart, atCollection: true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TBase? ReadBase(ref Utf8YamlReader reader, YamlSerializerOptions options, string why)
    {
        if (_baseInfo is not null && _baseInfo.CanReadAsBase)
        {
            return _baseInfo.Converter.ReadValue(ref reader, options);
        }

        throw reader.CreateException($"{ConverterGuards.DescribeType(typeof(TBase))} cannot be read: the value {why}, and the type itself cannot be created.");
    }
}
