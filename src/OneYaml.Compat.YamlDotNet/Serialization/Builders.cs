using System;
using System.Linq.Expressions;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.Serialization;

/// <summary>The settings builders for the serializer and the deserializer have in common.</summary>
/// <typeparam name="TBuilder">The builder type, so the methods return it.</typeparam>
public abstract class BuilderSkeleton<TBuilder>
    where TBuilder : BuilderSkeleton<TBuilder>
{
    private protected BuilderSkeleton(Settings settings)
    {
        Settings = settings;
    }

    private protected Settings Settings { get; }

    /// <summary>This builder.</summary>
    protected abstract TBuilder Self { get; }

    /// <summary>Includes only properties, not public fields.</summary>
    /// <returns>This builder.</returns>
    public TBuilder IgnoreFields()
    {
        Settings.IgnoreFields = true;
        return Self;
    }

    /// <summary>Includes properties whose accessors are not public.</summary>
    /// <returns>This builder.</returns>
    public TBuilder IncludeNonPublicProperties()
    {
        Settings.IncludeNonPublicProperties = true;
        return Self;
    }

    /// <summary>Lets the deserializer use non-public parameterless constructors.</summary>
    /// <returns>This builder.</returns>
    public TBuilder EnablePrivateConstructors()
    {
        Settings.EnablePrivateConstructors = true;
        return Self;
    }

    /// <summary>Sets how member names become keys.</summary>
    /// <param name="namingConvention">The convention.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithNamingConvention(INamingConvention namingConvention)
    {
        Settings.Naming = namingConvention ?? throw new ArgumentNullException(nameof(namingConvention));
        return Self;
    }

    /// <summary>Sets how enum member names become values.</summary>
    /// <param name="namingConvention">The convention.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithEnumNamingConvention(INamingConvention namingConvention)
    {
        Settings.EnumNaming = namingConvention ?? throw new ArgumentNullException(nameof(namingConvention));
        return Self;
    }

    /// <summary>Adds a converter. Converters added earlier are tried first.</summary>
    /// <param name="converter">The converter.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithTypeConverter(IYamlTypeConverter converter)
    {
        Settings.Converters.Add(converter ?? throw new ArgumentNullException(nameof(converter)));
        return Self;
    }

    /// <summary>Removes the converters of a type.</summary>
    /// <typeparam name="TYamlTypeConverter">The converter type.</typeparam>
    /// <returns>This builder.</returns>
    public TBuilder WithoutTypeConverter<TYamlTypeConverter>()
        where TYamlTypeConverter : IYamlTypeConverter => WithoutTypeConverter(typeof(TYamlTypeConverter));

    /// <summary>Removes the converters of a type.</summary>
    /// <param name="converterType">The converter type.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithoutTypeConverter(Type converterType)
    {
        ArgumentNullException.ThrowIfNull(converterType);
        Settings.Converters.RemoveAll(c => c.GetType() == converterType);
        return Self;
    }

    /// <summary>Maps a tag to a type, for reading and writing.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="type">The type.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithTagMapping(TagName tag, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (tag.IsEmpty)
        {
            throw new ArgumentException("Non-specific tags cannot be mapped.", nameof(tag));
        }

        Settings.TagToType[tag.Value!] = type;
        Settings.TypeToTag[type] = tag;
        return Self;
    }

    /// <summary>Overrides the attribute of a member, as if it were written on the member.</summary>
    /// <typeparam name="TClass">The declaring type.</typeparam>
    /// <param name="propertyAccessor">An expression that names the member.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithAttributeOverride<TClass>(Expression<Func<TClass, object>> propertyAccessor, Attribute attribute)
    {
        ArgumentNullException.ThrowIfNull(propertyAccessor);
        Expression body = propertyAccessor.Body;
        while (body is UnaryExpression unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member)
        {
            throw new ArgumentException("Expression must name a property or field.", nameof(propertyAccessor));
        }

        return WithAttributeOverride(typeof(TClass), member.Member.Name, attribute);
    }

    /// <summary>Overrides the attribute of a member, as if it were written on the member.</summary>
    /// <param name="type">The declaring type.</param>
    /// <param name="member">The member name.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>This builder.</returns>
    public TBuilder WithAttributeOverride(Type type, string member, Attribute attribute)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(attribute);
        if (!Settings.Overrides.TryGetValue((type, member), out System.Collections.Generic.List<Attribute>? list))
        {
            Settings.Overrides[(type, member)] = list = [];
        }

        list.Add(attribute);
        return Self;
    }
}

/// <summary>Builds a <see cref="Deserializer"/> with settings.</summary>
public sealed class DeserializerBuilder : BuilderSkeleton<DeserializerBuilder>
{
    /// <summary>Creates a builder with the default settings.</summary>
    public DeserializerBuilder()
        : base(new Settings { MaxRecursion = Deserializer.DefaultMaxRecursion })
    {
    }

    /// <inheritdoc/>
    protected override DeserializerBuilder Self => this;

    /// <summary>Skips mapping keys that match no member instead of failing.</summary>
    /// <returns>This builder.</returns>
    public DeserializerBuilder IgnoreUnmatchedProperties()
    {
        Settings.IgnoreUnmatched = true;
        return this;
    }

    /// <summary>Matches keys to member names without regard to case.</summary>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithCaseInsensitivePropertyMatching()
    {
        Settings.CaseInsensitive = true;
        return this;
    }

    /// <summary>Fails when a mapping holds the same key twice. By default the last value wins.</summary>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithDuplicateKeyChecking()
    {
        Settings.DuplicateKeyChecking = true;
        return this;
    }

    /// <summary>Reads plain scalars with no target type as the type their text suggests, instead of as strings.</summary>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithAttemptingUnquotedStringTypeDeserialization()
    {
        Settings.UnquotedStringTypeDeserialization = true;
        return this;
    }

    /// <summary>Sets how deeply the input may nest before reading fails.</summary>
    /// <param name="maximumRecursion">The most levels of nesting.</param>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithMaximumRecursion(int maximumRecursion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecursion, 1);
        Settings.MaxRecursion = maximumRecursion;
        return this;
    }

    /// <summary>Reads an interface or base type as a concrete type.</summary>
    /// <typeparam name="TInterface">The interface or base type.</typeparam>
    /// <typeparam name="TConcrete">The type to create.</typeparam>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithTypeMapping<TInterface, TConcrete>()
        where TConcrete : TInterface
    {
        Settings.TypeMappings[typeof(TInterface)] = typeof(TConcrete);
        return this;
    }

    /// <summary>Removes a tag mapping.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithoutTagMapping(TagName tag)
    {
        if (tag.IsEmpty)
        {
            throw new ArgumentException("Non-specific tags cannot be mapped.", nameof(tag));
        }

        if (!Settings.TagToType.Remove(tag.Value!))
        {
            throw new ArgumentException($"Tag '{tag}' is not registered.", nameof(tag));
        }

        return this;
    }

    /// <summary>Creates objects with a function instead of the parameterless constructor.</summary>
    /// <param name="objectFactory">The function, called with the type to create.</param>
    /// <returns>This builder.</returns>
    public DeserializerBuilder WithObjectFactory(Func<Type, object> objectFactory)
    {
        Settings.ObjectFactory = objectFactory ?? throw new ArgumentNullException(nameof(objectFactory));
        return this;
    }

    /// <summary>Builds the deserializer.</summary>
    /// <returns>The deserializer.</returns>
    public IDeserializer Build() => new Deserializer(Settings.Clone());
}

/// <summary>Builds a <see cref="Serializer"/> with settings.</summary>
public sealed class SerializerBuilder : BuilderSkeleton<SerializerBuilder>
{
    /// <summary>Creates a builder with the default settings.</summary>
    public SerializerBuilder()
        : base(new Settings { MaxRecursion = Serializer.DefaultMaxRecursion })
    {
    }

    /// <inheritdoc/>
    protected override SerializerBuilder Self => this;

    /// <summary>Quotes strings that would otherwise read back as another type. Strings are always quoted when needed; this also quotes ones that look like numbers.</summary>
    /// <param name="quoteNumbersAndBooleans">Whether strings that look like numbers or booleans are quoted.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithQuotingNecessaryStrings(bool quoteNumbersAndBooleans = false)
    {
        Settings.QuoteNecessaryStrings = true;
        Settings.QuoteNumericStrings = quoteNumbersAndBooleans;
        return this;
    }

    /// <summary>Sets the style strings are written in unless a member says otherwise.</summary>
    /// <param name="style">The style.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithDefaultScalarStyle(ScalarStyle style)
    {
        Settings.DefaultScalarStyle = style;
        return this;
    }

    /// <summary>Sets how deeply the object graph may nest before writing fails.</summary>
    /// <param name="maximumRecursion">The most levels of nesting.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithMaximumRecursion(int maximumRecursion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecursion, 1);
        Settings.MaxRecursion = maximumRecursion;
        return this;
    }

    /// <summary>Removes a tag mapping.</summary>
    /// <param name="type">The type.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithoutTagMapping(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!Settings.TypeToTag.Remove(type))
        {
            throw new ArgumentException($"Type '{type}' is not registered.", nameof(type));
        }

        return this;
    }

    /// <summary>Asks for output that a deserializer can read back into the same types.</summary>
    /// <returns>This builder.</returns>
    public SerializerBuilder EnsureRoundtrip()
    {
        Settings.EnsureRoundtrip = true;
        return this;
    }

    /// <summary>Writes a repeated object in full each time instead of as an anchor and aliases.</summary>
    /// <returns>This builder.</returns>
    public SerializerBuilder DisableAliases()
    {
        Settings.DisableAliases = true;
        return this;
    }

    /// <summary>Sets which members are left out of the output.</summary>
    /// <param name="handling">What to leave out.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder ConfigureDefaultValuesHandling(DefaultValuesHandling handling)
    {
        Settings.Defaults = handling;
        return this;
    }

    /// <summary>Writes JSON-compatible YAML: flow style, double-quoted strings and keys.</summary>
    /// <returns>This builder.</returns>
    public SerializerBuilder JsonCompatible()
    {
        Settings.JsonCompatible = true;
        Settings.DisableAliases = true;
        return this;
    }

    /// <summary>Sets the line break the output uses.</summary>
    /// <param name="newLine">The line break.</param>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithNewLine(string newLine)
    {
        Settings.NewLine = newLine ?? throw new ArgumentNullException(nameof(newLine));
        return this;
    }

    /// <summary>Indents block sequences under their key.</summary>
    /// <returns>This builder.</returns>
    public SerializerBuilder WithIndentedSequences()
    {
        Settings.IndentedSequences = true;
        return this;
    }

    /// <summary>Builds the serializer.</summary>
    /// <returns>The serializer.</returns>
    public ISerializer Build() => new Serializer(Settings.Clone());
}
