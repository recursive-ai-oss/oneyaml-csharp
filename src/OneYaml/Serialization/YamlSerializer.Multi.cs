using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OneYaml.Text;

namespace OneYaml;

public static partial class YamlSerializer
{
    // ---- reading several documents from streams and readers ----

    /// <summary>Reads every document of a stream of UTF-8, UTF-16 or UTF-32 text.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static List<T?> DeserializeAll<T>(Stream utf8Yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        return ReadAllFromStream(utf8Yaml, Info<T>(options));
    }

    /// <summary>Reads every document of a stream using generated metadata.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static List<T?> DeserializeAll<T>(Stream utf8Yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadAllFromStream(utf8Yaml, typeInfo);
    }

    /// <summary>Reads every document of a text reader.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="yaml">The reader, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static List<T?> DeserializeAll<T>(TextReader yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadAll(yaml.ReadToEnd(), Info<T>(options));
    }

    /// <summary>Reads every document of a text reader using generated metadata.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="yaml">The reader, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static List<T?> DeserializeAll<T>(TextReader yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadAll(yaml.ReadToEnd(), typeInfo);
    }

    /// <summary>Reads every document of a stream, reading the stream asynchronously.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read and the enumeration.</param>
    /// <returns>One value per document, in order. The stream is read, and every document bound, when enumeration starts, so a malformed document ends the enumeration before the first value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static IAsyncEnumerable<T?> DeserializeAllAsync<T>(Stream utf8Yaml, YamlSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        return ReadAllAsync(utf8Yaml, Info<T>(options), cancellationToken);
    }

    /// <summary>Reads every document of a stream asynchronously, using generated metadata.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the read and the enumeration.</param>
    /// <returns>One value per document, in order; see the other overload for when the documents are bound.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static IAsyncEnumerable<T?> DeserializeAllAsync<T>(Stream utf8Yaml, YamlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadAllAsync(utf8Yaml, typeInfo, cancellationToken);
    }

    // ---- writing several documents ----

    /// <summary>Writes values as the documents of one YAML stream, each after the first starting with <c>---</c>.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="values">The values, one document each. No values give empty text.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The YAML text. <see cref="DeserializeAll{T}(string, YamlSerializerOptions?)"/> reads it back.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static string SerializeAll<T>(IEnumerable<T> values, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return WriteAllString(values, Info<T>(options));
    }

    /// <summary>Writes values as the documents of one YAML stream using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The YAML text.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static string SerializeAll<T>(IEnumerable<T> values, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteAllString(values, typeInfo);
    }

    /// <summary>Writes values as the documents of one YAML stream, as UTF-8 bytes.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static byte[] SerializeAllToUtf8Bytes<T>(IEnumerable<T> values, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return WriteAllBytes(values, Info<T>(options));
    }

    /// <summary>Writes values as the documents of one YAML stream, as UTF-8 bytes, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static byte[] SerializeAllToUtf8Bytes<T>(IEnumerable<T> values, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteAllBytes(values, typeInfo);
    }

    /// <summary>Writes values as the documents of one YAML stream into a buffer writer.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="output">Where the UTF-8 text goes.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void SerializeAll<T>(IBufferWriter<byte> output, IEnumerable<T> values, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(values);
        WriteAllCore(output, values, Info<T>(options));
    }

    /// <summary>Writes values as the documents of one YAML stream into a buffer writer, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="output">Where the UTF-8 text goes.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void SerializeAll<T>(IBufferWriter<byte> output, IEnumerable<T> values, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        WriteAllCore(output, values, typeInfo);
    }

    /// <summary>Writes values as the documents of one YAML stream into a stream, as UTF-8.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void SerializeAll<T>(Stream utf8Yaml, IEnumerable<T> values, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        WriteAllStream(utf8Yaml, values, Info<T>(options));
    }

    /// <summary>Writes values as the documents of one YAML stream into a stream, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void SerializeAll<T>(Stream utf8Yaml, IEnumerable<T> values, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        WriteAllStream(utf8Yaml, values, typeInfo);
    }

    /// <summary>Writes values as the documents of one YAML stream into a stream, asynchronously.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that ends when the text is written.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static Task SerializeAllAsync<T>(Stream utf8Yaml, IEnumerable<T> values, YamlSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        return WriteAllStreamAsync(utf8Yaml, values, Info<T>(options), cancellationToken);
    }

    /// <summary>Writes values as the documents of one YAML stream into a stream, asynchronously, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that ends when the text is written.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static Task SerializeAllAsync<T>(Stream utf8Yaml, IEnumerable<T> values, YamlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteAllStreamAsync(utf8Yaml, values, typeInfo, cancellationToken);
    }

    /// <summary>Writes values from an asynchronous sequence as the documents of one YAML stream, flushing each document as it is written.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that ends when the sequence is written.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static Task SerializeAllAsync<T>(Stream utf8Yaml, IAsyncEnumerable<T> values, YamlSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        return WriteAllStreamAsync(utf8Yaml, values, Info<T>(options), cancellationToken);
    }

    /// <summary>Writes values from an asynchronous sequence as the documents of one YAML stream, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="utf8Yaml">The stream.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that ends when the sequence is written.</returns>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static Task SerializeAllAsync<T>(Stream utf8Yaml, IAsyncEnumerable<T> values, YamlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteAllStreamAsync(utf8Yaml, values, typeInfo, cancellationToken);
    }

    /// <summary>Writes values as the documents of one YAML stream into a text writer.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static void SerializeAll<T>(TextWriter writer, IEnumerable<T> values, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        writer.Write(WriteAllString(values, Info<T>(options)));
    }

    /// <summary>Writes values as the documents of one YAML stream into a text writer, using generated metadata.</summary>
    /// <typeparam name="T">The type of each value.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="values">The values, one document each.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="YamlException">A value refers back to itself, is nested deeper than the limit, or has text that cannot be written.</exception>
    public static void SerializeAll<T>(TextWriter writer, IEnumerable<T> values, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(typeInfo);
        writer.Write(WriteAllString(values, typeInfo));
    }

    // ---- core ----

    private static List<T?> ReadAllFromStream<T>(Stream stream, YamlTypeInfo<T> info)
    {
        (byte[] buffer, int length) = ReadAllBytes(stream, info.Options.Limits.MaxInputBytes, info.Options.SourceName);
        try
        {
            using YamlInput input = InputFromBytes(buffer.AsSpan(0, length), info.Options);
            return ReadAllUtf8(input.Utf8Text, info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A reader of the text cannot stay open across an await, so the stream is read first and every document bound
    // before the first is handed out.
    private static async IAsyncEnumerable<T?> ReadAllAsync<T>(Stream stream, YamlTypeInfo<T> info, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (byte[] buffer, int length) = await ReadAllBytesAsync(stream, info.Options.Limits.MaxInputBytes, info.Options.SourceName, cancellationToken).ConfigureAwait(false);
        List<T?> all;
        try
        {
            all = ReadAllBinding(buffer, length, info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        foreach (T? item in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    // Not async, so the ref struct input can live here.
    private static List<T?> ReadAllBinding<T>(byte[] buffer, int length, YamlTypeInfo<T> info)
    {
        using YamlInput input = InputFromBytes(buffer.AsSpan(0, length), info.Options);
        return ReadAllUtf8(input.Utf8Text, info);
    }

    // A document after the first starts with a marker, so the stream reads back as the same documents.
    private static void WriteAllCore<T>(IBufferWriter<byte> output, IEnumerable<T> values, YamlTypeInfo<T> info)
    {
        bool first = true;
        foreach (T value in values)
        {
            WriteSeparated(output, value, info, ref first);
        }
    }

    private static void WriteSeparated<T>(IBufferWriter<byte> output, T value, YamlTypeInfo<T> info, ref bool first)
    {
        if (!first)
        {
            Span<byte> marker = output.GetSpan(4);
            "---\n"u8.CopyTo(marker);
            output.Advance(4);
        }

        first = false;
        WriteCore(output, value, info);
    }

    private static string WriteAllString<T>(IEnumerable<T> values, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteAllCore(buffer, values, info);
        return buffer.ToUtf8String();
    }

    private static byte[] WriteAllBytes<T>(IEnumerable<T> values, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteAllCore(buffer, values, info);
        return buffer.ToArray();
    }

    private static void WriteAllStream<T>(Stream stream, IEnumerable<T> values, YamlTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter();
        WriteAllCore(buffer, values, info);
        buffer.WriteTo(stream);
    }

    private static async Task WriteAllStreamAsync<T>(Stream stream, IEnumerable<T> values, YamlTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var buffer = new PooledBufferWriter();
        WriteAllCore(buffer, values, info);
        await buffer.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAllStreamAsync<T>(Stream stream, IAsyncEnumerable<T> values, YamlTypeInfo<T> info, CancellationToken cancellationToken)
    {
        bool first = true;
        await foreach (T value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using var buffer = new PooledBufferWriter();
            WriteSeparated(buffer, value, info, ref first);
            await buffer.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }
}
