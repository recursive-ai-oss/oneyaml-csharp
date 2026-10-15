using System;
using System.Diagnostics.CodeAnalysis;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.Serialization;

/// <summary>Which members are left out of the output.</summary>
public enum DefaultValuesHandling
{
    /// <summary>Every member is written.</summary>
    Preserve,

    /// <summary>Members that are <see langword="null"/> are left out.</summary>
    OmitNull,

    /// <summary>Members that hold the default value of their type are left out.</summary>
    OmitDefaults,

    /// <summary>Empty collections are left out.</summary>
    OmitEmptyCollections,
}

/// <summary>Turns a member name into a mapping key and back.</summary>
public interface INamingConvention
{
    /// <summary>Converts a member name to a key.</summary>
    /// <param name="value">The member name.</param>
    /// <returns>The key.</returns>
    string Apply(string value);

    /// <summary>Converts a key back to a member name.</summary>
    /// <param name="value">The key.</param>
    /// <returns>The member name.</returns>
    string Reverse(string value);
}

/// <summary>Reads the value at the parser's position as a type, advancing the parser past it.</summary>
/// <param name="type">The type to read.</param>
/// <returns>The value.</returns>
public delegate object? ObjectDeserializer(Type type);

/// <summary>Writes a value to the emitter.</summary>
/// <param name="value">The value.</param>
/// <param name="type">The type to write it as, or <see langword="null"/> for its own type.</param>
public delegate void ObjectSerializer(object? value, Type? type = null);

/// <summary>Converts values of some types by reading and writing events.</summary>
public interface IYamlTypeConverter
{
    /// <summary>Tells whether the converter handles a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    bool Accepts(Type type);

    /// <summary>Reads a value. The parser is on the first event of the value; leave it just after the last.</summary>
    /// <param name="parser">The parser.</param>
    /// <param name="type">The type being read.</param>
    /// <param name="rootDeserializer">Reads a nested value with the deserializer's own rules.</param>
    /// <returns>The value.</returns>
    object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer);

    /// <summary>Writes a value as events.</summary>
    /// <param name="emitter">The emitter.</param>
    /// <param name="value">The value.</param>
    /// <param name="type">The type being written.</param>
    /// <param name="serializer">Writes a nested value with the serializer's own rules.</param>
    void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer);
}

/// <summary>A type that reads and writes itself as events.</summary>
public interface IYamlConvertible
{
    /// <summary>Reads the object. The parser is on the first event of the value; leave it just after the last.</summary>
    /// <param name="parser">The parser.</param>
    /// <param name="expectedType">The type being read.</param>
    /// <param name="nestedObjectDeserializer">Reads a nested value with the deserializer's own rules.</param>
    void Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer);

    /// <summary>Writes the object as events.</summary>
    /// <param name="emitter">The emitter.</param>
    /// <param name="nestedObjectSerializer">Writes a nested value with the serializer's own rules.</param>
    void Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer);
}

/// <summary>Serializes objects to YAML text.</summary>
public interface ISerializer
{
    /// <summary>Serializes an object to a string.</summary>
    /// <param name="graph">The object.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    string Serialize(object? graph);

    /// <summary>Serializes an object to a string, as a type.</summary>
    /// <param name="graph">The object.</param>
    /// <param name="type">The static type of the object.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    string Serialize(object? graph, Type type);

    /// <summary>Serializes an object to a writer.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="graph">The object.</param>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    void Serialize(System.IO.TextWriter writer, object? graph);

    /// <summary>Serializes an object to a writer, as a type.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="graph">The object.</param>
    /// <param name="type">The static type of the object.</param>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    void Serialize(System.IO.TextWriter writer, object? graph, Type type);

    /// <summary>Serializes an object to an emitter.</summary>
    /// <param name="emitter">The emitter.</param>
    /// <param name="graph">The object.</param>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    void Serialize(IEmitter emitter, object? graph);

    /// <summary>Serializes an object to an emitter, as a type.</summary>
    /// <param name="emitter">The emitter.</param>
    /// <param name="graph">The object.</param>
    /// <param name="type">The static type of the object.</param>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    void Serialize(IEmitter emitter, object? graph, Type type);
}

/// <summary>Reads YAML text into objects.</summary>
public interface IDeserializer
{
    /// <summary>Reads a document as a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="input">The YAML text.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    T Deserialize<T>(string input);

    /// <summary>Reads a document as a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="input">The reader to take the text from.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    T Deserialize<T>(System.IO.TextReader input);

    /// <summary>Reads the document at the parser's position as a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="parser">The parser.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    T Deserialize<T>(IParser parser);

    /// <summary>Reads a document with no type information.</summary>
    /// <param name="input">The YAML text.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(string input);

    /// <summary>Reads a document with no type information.</summary>
    /// <param name="input">The reader to take the text from.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(System.IO.TextReader input);

    /// <summary>Reads the document at the parser's position with no type information.</summary>
    /// <param name="parser">The parser.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(IParser parser);

    /// <summary>Reads a document as a type.</summary>
    /// <param name="input">The YAML text.</param>
    /// <param name="type">The type.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(string input, Type type);

    /// <summary>Reads a document as a type.</summary>
    /// <param name="input">The reader to take the text from.</param>
    /// <param name="type">The type.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(System.IO.TextReader input, Type type);

    /// <summary>Reads the document at the parser's position as a type.</summary>
    /// <param name="parser">The parser.</param>
    /// <param name="type">The type.</param>
    /// <returns>The value.</returns>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    object? Deserialize(IParser parser, Type type);
}

/// <summary>Settings for one member: its key, its place in the output, and how its value is written.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false)]
public sealed class YamlMemberAttribute : Attribute
{
    private DefaultValuesHandling _handling;

    /// <summary>Creates the attribute.</summary>
    public YamlMemberAttribute()
    {
    }

    /// <summary>Creates the attribute with a type to serialize the member as.</summary>
    /// <param name="serializeAs">The type.</param>
    public YamlMemberAttribute(Type serializeAs)
    {
        SerializeAs = serializeAs;
    }

    /// <summary>The type to serialize the member as.</summary>
    public Type? SerializeAs { get; set; }

    /// <summary>The position among the members; lower comes first.</summary>
    public int Order { get; set; }

    /// <summary>The key to use instead of the member name.</summary>
    public string? Alias { get; set; }

    /// <summary>Whether the naming convention applies to the name (not to an alias).</summary>
    public bool ApplyNamingConventions { get; set; } = true;

    /// <summary>The style to write the member's scalar value in.</summary>
    public ScalarStyle ScalarStyle { get; set; }

    /// <summary>Documentation for the member.</summary>
    public string? Description { get; set; }

    /// <summary>When the member is left out.</summary>
    public DefaultValuesHandling DefaultValuesHandling
    {
        get => _handling;
        set
        {
            _handling = value;
            IsDefaultValuesHandlingSpecified = true;
        }
    }

    /// <summary>Whether <see cref="DefaultValuesHandling"/> was set.</summary>
    public bool IsDefaultValuesHandlingSpecified { get; private set; }
}

/// <summary>Leaves a property or field out of reading and writing.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlIgnoreAttribute : Attribute
{
}

/// <summary>Names the converter for a type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class YamlConverterAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="converterType">A type that implements <see cref="IYamlTypeConverter"/> and has a parameterless constructor.</param>
    public YamlConverterAttribute(Type converterType)
    {
        ConverterType = converterType;
    }

    /// <summary>The converter type.</summary>
    public Type ConverterType { get; }
}
