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

    // Writes a value known only as an object, for the walker that writes `object` members and for adapters.
    internal abstract void WriteBoxed(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options);
}

/// <summary>Converts values of type <typeparamref name="T"/> between YAML and .NET.</summary>
/// <typeparam name="T">The type this converter handles.</typeparam>
/// <remarks>
/// The built-in converters stop with a <see cref="YamlErrorCategory.Limit"/> error when the stack is running
/// out. A converter that calls itself or other converters for each level of nesting should do the same, for
/// example with <see cref="System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack"/>
/// when <see cref="YamlLimits.None"/> is in use, because a stack overflow ends the process.
/// </remarks>
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

    /// <summary>Writes a value.</summary>
    /// <param name="writer">The emitter. Write exactly one value: a scalar, or a collection from its start to its end.</param>
    /// <param name="value">The value. It is never <see langword="null"/> unless <see cref="HandleNull"/> is <see langword="true"/>.</param>
    /// <param name="options">The options in effect.</param>
    public abstract void Write(Utf8YamlEmitter writer, T value, YamlSerializerOptions options);

    // The call every writer uses: null handling first, then the converter.
    internal void WriteValue(Utf8YamlEmitter writer, T? value, YamlSerializerOptions options)
    {
        if (value is null && !HandleNull)
        {
            writer.WriteNullValue();
            return;
        }

        Write(writer, value!, options);
    }

    internal override void WriteBoxed(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options)
    {
        if (value is null)
        {
            if (s_nonNullableValueType)
            {
                throw NullNotWritable();
            }

            if (!HandleNull)
            {
                writer.WriteNullValue();
                return;
            }
        }

        // The body of WriteValue, repeated: this runs once for every level of an object chain, and the runtime does not
        // inline calls in code it has only just compiled.
        Write(writer, (T)value!, options);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static YamlException NullNotWritable() => Utf8YamlEmitter.Misuse($"A null cannot be written as {typeof(T).Name}.");

    // The call every caller uses: null handling first, then the converter.
    internal T? ReadValue(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (!HandleNull && reader.IsNull)
        {
            if (s_nonNullableValueType)
            {
                throw NullNotAllowed(in reader);
            }

            return default;
        }

        return Read(ref reader, typeof(T), options);
    }

    // ReadValue for the constructor path, which nests deepest: fully optimised from the start, so its frame is the small one
    // whether the document is the first the process reads or the thousandth.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal T? ReadSlot(ref Utf8YamlReader reader, YamlSerializerOptions options)
    {
        if (!HandleNull && reader.IsNull)
        {
            if (s_nonNullableValueType)
            {
                throw NullNotAllowed(in reader);
            }

            return default;
        }

        return Read(ref reader, typeof(T), options);
    }

    // The message is built out of line: ReadValue is on the call stack once for every level of a nested document.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static YamlException NullNotAllowed(in Utf8YamlReader reader) =>
        reader.CreateException($"Null cannot be read as {typeof(T).Name}.");
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

    internal override void WriteBoxed(Utf8YamlEmitter writer, object? value, YamlSerializerOptions options) =>
        throw new InvalidOperationException("A converter factory makes converters; it does not write values.");
}

/// <summary>Names the YAML key of a member, overriding the naming policy.</summary>
/// <remarks>
/// <para>
/// A model written for System.Text.Json binds without change: where a OneYaml attribute is absent, the System.Text.Json attribute
/// of the same meaning is used. When a member has both, the OneYaml attribute wins.
/// </para>
/// <list type="table">
/// <listheader><term>System.Text.Json attribute</term><description>Used as</description></listheader>
/// <item><term><c>JsonPropertyName</c></term><description><see cref="YamlPropertyNameAttribute"/></description></item>
/// <item><term><c>JsonIgnore</c></term><description><see cref="YamlIgnoreAttribute"/>, with its condition</description></item>
/// <item><term><c>JsonRequired</c></term><description><see cref="YamlRequiredAttribute"/></description></item>
/// <item><term><c>JsonPropertyOrder</c></term><description><see cref="YamlPropertyOrderAttribute"/></description></item>
/// <item><term><c>JsonConstructor</c></term><description><see cref="YamlConstructorAttribute"/></description></item>
/// <item><term><c>JsonExtensionData</c></term><description><see cref="YamlExtensionDataAttribute"/>, on a <c>Dictionary&lt;string, object&gt;</c> or <c>IDictionary&lt;string, object&gt;</c> only; on any other type the member is an ordinary one</description></item>
/// <item><term><c>JsonPolymorphic</c>, <c>JsonDerivedType</c></term><description><see cref="YamlPolymorphicAttribute"/> and <see cref="YamlDerivedTypeAttribute"/>, always with a key discriminator; a derived type with no discriminator is not listed</description></item>
/// <item><term><c>JsonConverter</c></term><description>Not used: its converters read and write JSON tokens. Use <see cref="YamlConverterAttribute"/>.</description></item>
/// </list>
/// </remarks>
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

/// <summary>
/// Names an enum member in YAML text, overriding <see cref="YamlSerializerOptions.EnumNamingPolicy"/>.
/// <c>JsonStringEnumMemberName</c> does the same where this attribute is absent.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlEnumMemberNameAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">The text written for the member.</param>
    public YamlEnumMemberNameAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The text written for the member.</summary>
    public string Name { get; }
}

/// <summary>Leaves a member out of binding, or out of the output under a condition.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlIgnoreAttribute : Attribute
{
    /// <summary>
    /// When the member is left out. The default, <see cref="YamlIgnoreCondition.Always"/>, leaves it out of reading and
    /// writing; the other conditions apply to writing only.
    /// </summary>
    public YamlIgnoreCondition Condition { get; set; } = YamlIgnoreCondition.Always;
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
