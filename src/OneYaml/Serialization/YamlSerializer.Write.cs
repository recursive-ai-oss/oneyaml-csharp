using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneYaml;

public static partial class YamlSerializer
{
    // ---- to a string ----

    /// <summary>Writes a value as YAML text in block style, with a line break at the end.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The YAML text. It reads back as the value, under the Core schema and under YAML 1.1.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written (an unpaired surrogate).</exception>
    /// <example>
    /// <code language="csharp">
    /// var config = new Config(3, "make build\nmake test\n");
    /// Console.Write(YamlSerializer.Serialize(config));
    /// // replicas: 3
    /// // script: |
    /// //   make build
    /// //   make test
    ///
    /// record Config(int Replicas, string Script);
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static string Serialize<T>(T value, YamlSerializerOptions? options = null) => WriteString(value, Info<T>(options));

    /// <summary>Writes a value as YAML text using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The YAML text.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static string Serialize<T>(T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteString(value, typeInfo);
    }

    /// <summary>Writes a value as YAML text, by the type it is declared as.</summary>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The declared type; the value must be an instance of it.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The YAML text.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not an instance of <paramref name="inputType"/>.</exception>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static string Serialize(object? value, Type inputType, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputType);
        return WriteBoxedString(value, (options ?? YamlSerializerOptions.Default).GetReflectiveTypeInfo(inputType));
    }

    /// <summary>Writes a value as YAML text using generated metadata for its declared type.</summary>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for the declared type; the value must be an instance of it.</param>
    /// <returns>The YAML text.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not an instance of the declared type.</exception>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static string Serialize(object? value, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteBoxedString(value, typeInfo);
    }

    // ---- to bytes ----

    /// <summary>Writes a value as UTF-8 YAML, without a byte order mark.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static byte[] SerializeToUtf8Bytes<T>(T value, YamlSerializerOptions? options = null) => WriteBytes(value, Info<T>(options));

    /// <summary>Writes a value as UTF-8 YAML using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static byte[] SerializeToUtf8Bytes<T>(T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteBytes(value, typeInfo);
    }

    // ---- to a buffer, a stream or a text writer ----

    /// <summary>Writes a value as UTF-8 YAML into a buffer writer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void Serialize<T>(IBufferWriter<byte> output, T value, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteCore(output, value, Info<T>(options));
    }

    /// <summary>Writes a value as UTF-8 YAML into a buffer writer, using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void Serialize<T>(IBufferWriter<byte> output, T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(typeInfo);
        WriteCore(output, value, typeInfo);
    }

    /// <summary>Writes a value as UTF-8 YAML (no byte order mark) to a stream.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="utf8Yaml">The stream. It is not closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void Serialize<T>(Stream utf8Yaml, T value, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        WriteStream(utf8Yaml, value, Info<T>(options));
    }

    /// <summary>Writes a value as UTF-8 YAML to a stream, using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="utf8Yaml">The stream. It is not closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void Serialize<T>(Stream utf8Yaml, T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        WriteStream(utf8Yaml, value, typeInfo);
    }

    /// <summary>Writes a value as UTF-8 YAML (no byte order mark) to a stream.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="utf8Yaml">The stream. It is not closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the text is written.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static Task SerializeAsync<T>(Stream utf8Yaml, T value, YamlSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        return WriteStreamAsync(utf8Yaml, value, Info<T>(options), cancellationToken);
    }

    /// <summary>Writes a value as UTF-8 YAML to a stream, using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="utf8Yaml">The stream. It is not closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the text is written.</returns>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static Task SerializeAsync<T>(Stream utf8Yaml, T value, YamlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteStreamAsync(utf8Yaml, value, typeInfo, cancellationToken);
    }

    /// <summary>Writes a value as YAML text to a text writer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The writer. It is not flushed or closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void Serialize<T>(TextWriter writer, T value, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(WriteString(value, Info<T>(options)));
    }

    /// <summary>Writes a value as YAML text to a text writer, using generated metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The writer. It is not flushed or closed.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">The value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void Serialize<T>(TextWriter writer, T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(typeInfo);
        writer.Write(WriteString(value, typeInfo));
    }

    // ---- core ----

    // Writes one document into the buffer writer. The emitter is borrowed from a per-thread cache and given back on every path.
    private static void WriteCore<T>(IBufferWriter<byte> output, T value, YamlTypeInfo<T> info)
    {
        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();
        Utf8YamlEmitter emitter = Utf8YamlEmitter.Rent(output, options);
        try
        {
            info.Converter.WriteValue(emitter, value, options);
            emitter.Finish();
        }
        finally
        {
            Utf8YamlEmitter.Return(emitter);
        }
    }

    // For the document editor: writes a value as the value of an entry in text that is already written, after its
    // "key:" or "- " at the given column, in the style the serializer gives a value of that place. The text ends with a line break.
    internal static void WriteEmbedded(IBufferWriter<byte> output, object? value, YamlTypeInfo info, Utf8YamlEmitter.Embedding embedding, int parentIndent)
    {
        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();
        Utf8YamlEmitter emitter = Utf8YamlEmitter.Rent(output, options);
        try
        {
            emitter.Embed(embedding, parentIndent);
            info.WriteBoxed(emitter, value);
            emitter.Finish();
        }
        finally
        {
            Utf8YamlEmitter.Return(emitter);
        }
    }

    private static void WriteBoxedCore(IBufferWriter<byte> output, object? value, YamlTypeInfo info)
    {
        if (value is not null && !info.Type.IsInstanceOfType(value))
        {
            throw new ArgumentException($"The value is a {value.GetType()}, which is not a {info.Type}.", nameof(value));
        }

        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();
        Utf8YamlEmitter emitter = Utf8YamlEmitter.Rent(output, options);
        try
        {
            info.WriteBoxed(emitter, value);
            emitter.Finish();
        }
        finally
        {
            Utf8YamlEmitter.Return(emitter);
        }
    }

    private static string WriteString<T>(T value, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteCore(buffer, value, info);
        return buffer.ToUtf8String();
    }

    private static string WriteBoxedString(object? value, YamlTypeInfo info)
    {
        using var buffer = new PooledBufferWriter();
        WriteBoxedCore(buffer, value, info);
        return buffer.ToUtf8String();
    }

    private static byte[] WriteBytes<T>(T value, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteCore(buffer, value, info);
        return buffer.ToArray();
    }

    private static void WriteStream<T>(Stream stream, T value, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteCore(buffer, value, info);
        buffer.WriteTo(stream);
    }

    private static async Task WriteStreamAsync<T>(Stream stream, T value, YamlTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var buffer = new PooledBufferWriter();
        WriteCore(buffer, value, info);
        await buffer.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
