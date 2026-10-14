using System;

namespace OneYaml;

/// <summary>
/// Converts values of a type or family of types. Derive from <see cref="YamlConverter{T}"/> to read
/// one type, or from <see cref="YamlConverterFactory"/> to make converters on demand.
/// </summary>
public abstract class YamlConverter
{
    internal YamlConverter()
    {
    }

    /// <summary>Tells whether this converter can convert the type.</summary>
    /// <param name="typeToConvert">The type.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public abstract bool CanConvert(Type typeToConvert);
}

/// <summary>Converts values of type <typeparamref name="T"/> between YAML and .NET.</summary>
/// <typeparam name="T">The type this converter handles.</typeparam>
public abstract class YamlConverter<T> : YamlConverter
{
    private static readonly bool s_nonNullableValueType = typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;

    /// <summary>Creates a converter.</summary>
    protected YamlConverter()
    {
    }

    /// <summary>
    /// Whether <see cref="Read"/> is called for null scalars. When it is <see langword="false"/> (the default),
    /// a null becomes <see langword="default"/> without calling the converter, and fails for a value type
    /// that cannot hold null.
    /// </summary>
    public virtual bool HandleNull => false;

    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    /// <summary>Reads a value.</summary>
    /// <param name="reader">
    /// The reader, on the first token of the value. Leave it on the last token of the value:
    /// the scalar itself, or the end of the collection.
    /// </param>
    /// <param name="typeToConvert">The type being read.</param>
    /// <param name="options">The options in effect.</param>
    /// <returns>The value.</returns>
    public abstract T? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options);

    // The call every caller uses: null handling first, then the converter.
    internal T? ReadValue(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (!HandleNull && reader.IsNull)
        {
            if (s_nonNullableValueType)
            {
                throw reader.CreateException($"Null cannot be read as {typeof(T).Name}.");
            }

            return default;
        }

        return Read(ref reader, typeof(T), options);
    }
}

/// <summary>Makes converters for types that a single <see cref="YamlConverter{T}"/> cannot cover, such as open generics.</summary>
public abstract class YamlConverterFactory : YamlConverter
{
    /// <summary>Creates a factory.</summary>
    protected YamlConverterFactory()
    {
    }

    /// <summary>Makes a converter for the type.</summary>
    /// <param name="typeToConvert">The type; <see cref="YamlConverter.CanConvert"/> returned <see langword="true"/> for it.</param>
    /// <param name="options">The options in effect.</param>
    /// <returns>A converter for the type, or <see langword="null"/> for none.</returns>
    public abstract YamlConverter? CreateConverter(Type typeToConvert, YamlSerializerOptions options);
}

/// <summary>Names the YAML key of a member, overriding the naming policy.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlPropertyNameAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">The key.</param>
    public YamlPropertyNameAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The key.</summary>
    public string Name { get; }
}

/// <summary>Leaves a member out of binding.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlIgnoreAttribute : Attribute
{
}

/// <summary>Marks the constructor to bind through when a type has several.</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class YamlConstructorAttribute : Attribute
{
}

/// <summary>Makes a member required: reading fails when its key is missing.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlRequiredAttribute : Attribute
{
}

/// <summary>Sets the position of a member among its siblings when writing.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlPropertyOrderAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="order">The position; lower comes first.</param>
    public YamlPropertyOrderAttribute(int order) => Order = order;

    /// <summary>The position; lower comes first.</summary>
    public int Order { get; }
}
