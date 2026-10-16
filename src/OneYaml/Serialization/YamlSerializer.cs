using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OneYaml.Parsing;
using OneYaml.Text;

namespace OneYaml;

/// <summary>Reads YAML into .NET values and writes .NET values as YAML.</summary>
/// <remarks>
/// A <see cref="DateTime"/> read from a timestamp that has an offset is converted to the local time of the
/// machine, as <see cref="System.Text.Json.JsonSerializer"/> does, so the result depends on its time zone; use <see cref="DateTimeOffset"/> to keep the offset.
/// <para>
/// Tags are dropped wherever the target is <see cref="object"/> (a root, a member, a list or dictionary item of
/// <see cref="object"/>): a value tagged <c>!Sub</c> reads as the plain string, a mapping tagged <c>!!omap</c> as a dictionary. That
/// is the shape of the untyped model, and code may rely on it: a value of that model is a <see cref="string"/>,
/// <see cref="long"/>, <see cref="System.Numerics.BigInteger"/>, <see cref="double"/>, <see cref="bool"/>, a dictionary, a list, or null, and nothing else. Keeping
/// tags would need a new representation, so it could only come as an option that is off by default. To read tags today use a
/// <see cref="YamlConverter{T}"/> and <see cref="Utf8YamlReader.Tag"/>, or a <see cref="YamlDocument"/>, which keeps them.
/// </para>
/// </remarks>
public static partial class YamlSerializer
{
    /// <summary>
    /// Whether options without a resolver fall back to reflection. It is <see langword="true"/> unless the
    /// application sets the <c>OneYaml.YamlSerializer.IsReflectionEnabledByDefault</c> runtime switch to
    /// <see langword="false"/>. Trimmed and ahead-of-time published applications get
    /// <see langword="false"/> unless the project sets <c>YamlSerializerIsReflectionEnabledByDefault</c>;
    /// the trimmer then removes the reflection code.
    /// </summary>
    public static bool IsReflectionEnabledByDefault { get; } = !AppContext.TryGetSwitch("OneYaml.YamlSerializer.IsReflectionEnabledByDefault", out bool enabled) || enabled;

    private const string UnreferencedMessage = "Reflection-based binding reads members and constructors that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.";
    private const string DynamicMessage = "Reflection-based binding makes generic types and compiles accessors at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.";

    // ---- string ----

    /// <summary>Reads a value from YAML text.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="yaml"/> is <see langword="null"/>.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static T? Deserialize<T>(string yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadChars(yaml, Info<T>(options));
    }

    /// <summary>Reads a value from YAML text using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static T? Deserialize<T>(string yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadChars(yaml, typeInfo);
    }

    /// <summary>Reads a value of the given type from YAML text.</summary>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static object? Deserialize(string yaml, Type returnType, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(returnType);
        return ReadBoxed(yaml.AsSpan(), (options ?? YamlSerializerOptions.Default).GetReflectiveTypeInfo(returnType));
    }

    /// <summary>Reads a value from YAML text using generated metadata.</summary>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="typeInfo">The metadata for the type to read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static object? Deserialize(string yaml, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadBoxed(yaml.AsSpan(), typeInfo);
    }

    // ---- spans ----

    /// <summary>Reads a value from YAML text.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static T? Deserialize<T>(ReadOnlySpan<char> yaml, YamlSerializerOptions? options = null) => ReadChars(yaml, Info<T>(options));

    /// <summary>Reads a value from YAML text using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text; it must hold one document.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static T? Deserialize<T>(ReadOnlySpan<char> yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadChars(yaml, typeInfo);
    }

    /// <summary>Reads a value from UTF-8 bytes. UTF-16 and UTF-32 are detected by byte order mark or by their null bytes.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The YAML bytes; they must hold one document.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Yaml, YamlSerializerOptions? options = null) => ReadBytes(utf8Yaml, Info<T>(options));

    /// <summary>Reads a value from UTF-8 bytes using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The YAML bytes; they must hold one document.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadBytes(utf8Yaml, typeInfo);
    }

    // ---- streams and readers ----

    /// <summary>Reads a value from a stream of bytes.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static T? Deserialize<T>(Stream utf8Yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        return ReadStream(utf8Yaml, Info<T>(options));
    }

    /// <summary>Reads a value from a stream of bytes using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    public static T? Deserialize<T>(Stream utf8Yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadStream(utf8Yaml, typeInfo);
    }

    /// <summary>Reads a value from a stream of bytes.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Yaml, YamlSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        return ReadStreamAsync(utf8Yaml, Info<T>(options), cancellationToken);
    }

    /// <summary>Reads a value from a stream of bytes using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8Yaml">The stream, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Yaml, YamlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadStreamAsync(utf8Yaml, typeInfo, cancellationToken);
    }

    /// <summary>Reads a value from a text reader.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The reader, read to its end.</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static T? Deserialize<T>(TextReader yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadChars(yaml.ReadToEnd(), Info<T>(options));
    }

    /// <summary>Reads a value from a text reader using generated metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The reader, read to its end.</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static T? Deserialize<T>(TextReader yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadChars(yaml.ReadToEnd(), typeInfo);
    }

    // ---- several documents ----

    /// <summary>Reads every document of a stream.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="yaml">The YAML text, with any number of documents (an empty text has none).</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static IReadOnlyList<T?> DeserializeAll<T>(string yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadAll(yaml, Info<T>(options));
    }

    /// <summary>Reads every document of a stream using generated metadata.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="yaml">The YAML text, with any number of documents (an empty text has none).</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, does not fit the type, or exceeds a limit.</exception>
    public static IReadOnlyList<T?> DeserializeAll<T>(string yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadAll(yaml, typeInfo);
    }

    /// <summary>Reads every document of a stream of bytes.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The YAML bytes, with any number of documents (empty input has none).</param>
    /// <param name="options">Options; <see langword="null"/> means <see cref="YamlSerializerOptions.Default"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static IReadOnlyList<T?> DeserializeAll<T>(ReadOnlySpan<byte> utf8Yaml, YamlSerializerOptions? options = null)
    {
        YamlTypeInfo<T> info = Info<T>(options);
        using YamlInput input = InputFromBytes(utf8Yaml, info.Options);
        return ReadAllUtf8(input.Utf8Text, info);
    }

    /// <summary>Reads every document of a stream of bytes using generated metadata.</summary>
    /// <typeparam name="T">The type of each document.</typeparam>
    /// <param name="utf8Yaml">The YAML bytes, with any number of documents (empty input has none).</param>
    /// <param name="typeInfo">The metadata for <typeparamref name="T"/>.</param>
    /// <returns>One value per document, in order.</returns>
    /// <exception cref="YamlException">The bytes are not valid YAML, do not fit the type, or exceed a limit.</exception>
    public static IReadOnlyList<T?> DeserializeAll<T>(ReadOnlySpan<byte> utf8Yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        using YamlInput input = InputFromBytes(utf8Yaml, typeInfo.Options);
        return ReadAllUtf8(input.Utf8Text, typeInfo);
    }

    // ---- core ----

    [RequiresUnreferencedCode(UnreferencedMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    private static YamlTypeInfo<T> Info<T>(YamlSerializerOptions? options) => (options ?? YamlSerializerOptions.Default).GetReflectiveTypeInfo<T>();

    // The source name is added to an error as it passes through the entry point, by an exception filter that never
    // catches. It sits here and not in ReadUtf8, whose frame the reader lives in, so the hot path is as it was.
    private static T? ReadChars<T>(ReadOnlySpan<char> text, YamlTypeInfo<T> info)
    {
        try
        {
            using YamlInput input = YamlInput.FromChars(text, info.Options.Limits);
            return ReadUtf8(input.Utf8Text, info);
        }
        catch (YamlException e) when (YamlException.Tag(e, info.Options.SourceName))
        {
            throw;
        }
    }

    private static T? ReadBytes<T>(ReadOnlySpan<byte> bytes, YamlTypeInfo<T> info)
    {
        try
        {
            using YamlInput input = YamlInput.FromBytes(bytes, info.Options.Limits);
            return ReadUtf8(input.Utf8Text, info);
        }
        catch (YamlException e) when (YamlException.Tag(e, info.Options.SourceName))
        {
            throw;
        }
    }

    private static object? ReadBoxed(ReadOnlySpan<char> text, YamlTypeInfo info)
    {
        info.Options.MakeReadOnly();
        using YamlInput input = InputFromChars(text, info.Options);
        YamlReader.Scratch scratch = default;
        var reader = new Utf8YamlReader(input.Utf8Text, info.Options, !IsGeneric(info), ref scratch);
        try
        {
            reader.BeginDocument(synthesizeEmptyStream: true);
            object? value = info.ReadBoxed(ref reader);
            reader.EndDocument();
            reader.EndStreamAfterSingleDocument();
            return value;
        }
        catch (YamlException e) when (YamlException.Tag(e, info.Options.SourceName))
        {
            throw;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static T? ReadUtf8<T>(ReadOnlySpan<byte> utf8, YamlTypeInfo<T> info)
    {
        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();
        YamlReader.Scratch scratch = default;
        var reader = new Utf8YamlReader(utf8, options, !IsGeneric(info), ref scratch);
        try
        {
            reader.BeginDocument(synthesizeEmptyStream: true);
            T? value = info.Converter.ReadValue(ref reader, options);
            reader.EndDocument();
            reader.EndStreamAfterSingleDocument();
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    // The input is checked and transcoded here, before any reader exists, so an encoding or size error is
    // marked with the source name on its way out. The filter never catches.
    private static YamlInput InputFromChars(ReadOnlySpan<char> text, YamlSerializerOptions options)
    {
        try
        {
            return YamlInput.FromChars(text, options.Limits);
        }
        catch (YamlException e) when (YamlException.Tag(e, options.SourceName))
        {
            throw;
        }
    }

    private static YamlInput InputFromBytes(ReadOnlySpan<byte> bytes, YamlSerializerOptions options)
    {
        try
        {
            return YamlInput.FromBytes(bytes, options.Limits);
        }
        catch (YamlException e) when (YamlException.Tag(e, options.SourceName))
        {
            throw;
        }
    }

    // Only a plain object target is read without following aliases, so that aliases share instances.
    private static bool IsGeneric(YamlTypeInfo info) => info.ConverterBase is GenericConverter;

    private static List<T?> ReadAll<T>(string text, YamlTypeInfo<T> info)
    {
        using YamlInput input = InputFromChars(text, info.Options);
        return ReadAllUtf8(input.Utf8Text, info);
    }

    private static List<T?> ReadAllUtf8<T>(ReadOnlySpan<byte> utf8, YamlTypeInfo<T> info)
    {
        YamlSerializerOptions options = info.Options;
        options.MakeReadOnly();
        YamlReader.Scratch scratch = default;
        var reader = new Utf8YamlReader(utf8, options, !IsGeneric(info), ref scratch);
        try
        {
            var all = new List<T?>();
            while (reader.BeginDocument(synthesizeEmptyStream: false))
            {
                all.Add(info.Converter.ReadValue(ref reader, options));
                reader.EndDocument();
            }

            return all;
        }
        catch (YamlException e) when (YamlException.Tag(e, options.SourceName))
        {
            throw;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static T? ReadStream<T>(Stream stream, YamlTypeInfo<T> info)
    {
        (byte[] buffer, int length) = ReadAllBytes(stream, info.Options.Limits.MaxInputBytes, info.Options.SourceName);
        try
        {
            return ReadBytes(buffer.AsSpan(0, length), info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<T?> ReadStreamAsync<T>(Stream stream, YamlTypeInfo<T> info, CancellationToken cancellationToken)
    {
        (byte[] buffer, int length) = await ReadAllBytesAsync(stream, info.Options.Limits.MaxInputBytes, info.Options.SourceName, cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadBytes(buffer.AsSpan(0, length), info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Reads a stream to its end into a pooled buffer, stopping early once it passes the input limit.
    private static (byte[] Buffer, int Length) ReadAllBytes(Stream stream, long max, string? sourceName)
    {
        // A stream that says how long it is can be refused before any buffer is taken for it.
        if (stream.CanSeek)
        {
            CheckSize(stream.Length - stream.Position, max, sourceName);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position + 1, 4096, 1 << 26) : 16384);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    buffer = Grow(buffer, length, max, sourceName);
                }

                int n = stream.Read(buffer, length, buffer.Length - length);
                if (n == 0)
                {
                    return (buffer, length);
                }

                length += n;
                CheckSize(length, max, sourceName);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static async ValueTask<(byte[] Buffer, int Length)> ReadAllBytesAsync(Stream stream, long max, string? sourceName, CancellationToken cancellationToken)
    {
        // A stream that says how long it is can be refused before any buffer is taken for it.
        if (stream.CanSeek)
        {
            CheckSize(stream.Length - stream.Position, max, sourceName);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position + 1, 4096, 1 << 26) : 16384);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    buffer = Grow(buffer, length, max, sourceName);
                }

                int n = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return (buffer, length);
                }

                length += n;
                CheckSize(length, max, sourceName);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static void CheckSize(long length, long max, string? sourceName)
    {
        if (length > max)
        {
            throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0) { SourceName = sourceName };
        }
    }

    private static byte[] Grow(byte[] buffer, int length, long max, string? sourceName)
    {
        if (buffer.Length >= int.MaxValue / 2 || buffer.Length > max)
        {
            throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0) { SourceName = sourceName };
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        Buffer.BlockCopy(buffer, 0, bigger, 0, length);
        ArrayPool<byte>.Shared.Return(buffer);
        return bigger;
    }
}
