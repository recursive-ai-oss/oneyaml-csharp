using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

/// <summary>Reads and writes YAML text as .NET values, with the API of SharpYaml 3.</summary>
public static class YamlSerializer
{
    private const string Unreferenced = "Reflection-based binding reads members and constructors that trimming can remove.";
    private const string Dynamic = "Reflection-based binding makes generic types at run time.";

    /// <summary>
    /// Whether reflection-based binding is on unless a program turns it off. A program turns it off with the switch
    /// <c>SharpYaml.YamlSerializer.IsReflectionEnabledByDefault</c>, as it does for SharpYaml, or with the switch of OneYaml.
    /// </summary>
    public static bool IsReflectionEnabledByDefault => Oy.YamlSerializer.IsReflectionEnabledByDefault
        && (!AppContext.TryGetSwitch("SharpYaml.YamlSerializer.IsReflectionEnabledByDefault", out bool enabled) || enabled);

    // ---- helpers ----

    private static YamlSerializerOptions Use(YamlSerializerOptions? options) => options ?? YamlSerializerOptions.Default;

    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    private static Oy.YamlTypeInfo<T> InfoOf<T>(YamlSerializerOptions? options) => Use(options).GetEngine().GetTypeInfo<T>();

    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    private static Oy.YamlTypeInfo InfoOf(Type type, YamlSerializerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Use(options).GetEngine().GetTypeInfo(type);
    }

    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    private static Oy.YamlTypeInfo<T> InfoOf<T>(Serialization.YamlSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Typed<T>(context.GetTypeInfo(typeof(T), context.Options), typeof(T)).Engine;
    }

    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    private static Oy.YamlTypeInfo InfoOf(Type type, Serialization.YamlSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(context);
        return context.GetTypeInfo(type, context.Options)?.Engine ?? throw NoMetadata(type);
    }

    private static YamlTypeInfo<T> Typed<T>(YamlTypeInfo? info, Type type) =>
        info as YamlTypeInfo<T> ?? throw NoMetadata(type);

    private static NotSupportedException NoMetadata(Type type) => new($"No metadata is available for type '{type}'. See docs/compat/sharpyaml.md for the parts of SharpYaml that are not covered.");

    // The wrapped metadata knows its own options, which is where the source name lives.
    private static string? Source(YamlSerializerOptions? options) => options?.SourceName;

    private static T? Read<T>(ReadOnlySpan<char> yaml, Oy.YamlTypeInfo<T> info, string? source)
    {
        int cut = Documents.FirstDocumentEnd(yaml);
        if (cut > 0)
        {
            try
            {
                return ReadPrefix(yaml, cut, info);
            }
            catch (Oy.YamlException)
            {
                // Whatever is wrong is wrong in the first document too, so reading the whole text names it the same way.
            }
        }

        try
        {
            return Oy.YamlSerializer.Deserialize(yaml, info);
        }
        catch (Oy.YamlException error) when (ErrorMap.IsMultiDocument(error))
        {
            return FirstOf(yaml, info, error, source);
        }
        catch (Oy.YamlException error)
        {
            throw ErrorMap.Translate(error, yaml, source);
        }
    }

    // SharpYaml stops after the first document without looking at the rest, so the text is cut where the second one
    // starts. The engine says where that is; when it cannot, all the documents are read and the first is kept.
    private static T? FirstOf<T>(ReadOnlySpan<char> yaml, Oy.YamlTypeInfo<T> info, Oy.YamlException split, string? source)
    {
        try
        {
            int cut = ErrorMap.CharIndexOf(split, yaml);
            if (cut > 0 && cut < yaml.Length)
            {
                return ReadPrefix(yaml, cut, info);
            }

            System.Collections.Generic.List<T?> all = Oy.YamlSerializer.DeserializeAll(yaml.ToString(), info);
            return all.Count > 0 ? all[0] : default;
        }
        catch (Oy.YamlException error)
        {
            throw ErrorMap.Translate(error, yaml, source);
        }
    }

    // The second document may be preceded by directives or comments that belong to it; they are cut off as well.
    private static T? ReadPrefix<T>(ReadOnlySpan<char> yaml, int cut, Oy.YamlTypeInfo<T> info)
    {
        try
        {
            return Oy.YamlSerializer.Deserialize(yaml[..cut], info);
        }
        catch (Oy.YamlException error) when (CutAtError(yaml, error, ref cut))
        {
        }

        return Oy.YamlSerializer.Deserialize(yaml[..cut], info);
    }

    // SharpYaml takes a % line after a block collection for the directive of the next document, while after a scalar the
    // same line is part of the scalar. So the prefix is first read as it stands, and only when that fails, the text is cut
    // at the start of the line where the failure was reported, provided that line and everything after it is blank, a
    // comment or a directive. Cutting there, not at the start of the whole run, keeps the lines before it that still belong
    // to the first document (a % line that continues a plain scalar). One cut, one more parse: a first document followed by
    // thousands of such lines is read twice at most. False when the failure is anywhere else.
    private static bool CutAtError(ReadOnlySpan<char> yaml, Oy.YamlException error, ref int cut)
    {
        ReadOnlySpan<char> head = yaml[..cut];
        int at = ErrorMap.CharIndexOf(error, head);
        if (at < 0)
        {
            return false;
        }

        int start = head[..Math.Min(at, head.Length)].LastIndexOfAny('\n', '\r') + 1;
        if (start <= 0 || start >= head.Length)
        {
            return false;
        }

        ReadOnlySpan<char> rest = head[start..];
        while (!rest.IsEmpty)
        {
            int eol = rest.IndexOfAny('\n', '\r');
            ReadOnlySpan<char> line = eol < 0 ? rest : rest[..eol];
            if (!line.IsWhiteSpace() && line[0] is not ('%' or '#'))
            {
                return false;
            }

            rest = eol < 0 ? default : rest[(eol + 1)..];
        }

        cut = start;
        return true;
    }

    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    private static object? ReadBoxed(string yaml, Oy.YamlTypeInfo info, string? source)
    {
        int cut = Documents.FirstDocumentEnd(yaml);
        if (cut > 0)
        {
            bool retried = false;
            while (true)
            {
                try
                {
                    return Oy.YamlSerializer.Deserialize(yaml[..cut], info);
                }
                catch (Oy.YamlException error) when (!retried && CutAtError(yaml, error, ref cut))
                {
                    retried = true;
                }
                catch (Oy.YamlException)
                {
                    // The whole text names the problem below.
                    break;
                }
            }
        }

        try
        {
            return Oy.YamlSerializer.Deserialize(yaml, info);
        }
        catch (Oy.YamlException error) when (ErrorMap.IsMultiDocument(error))
        {
            System.Reflection.MethodInfo read = typeof(YamlSerializer).GetMethod(nameof(FirstOfString), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(info.Type);
            try
            {
                return read.Invoke(null, [yaml, info, error, source]);
            }
            catch (System.Reflection.TargetInvocationException inner) when (inner.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner.InnerException).Throw();
                throw;
            }
        }
        catch (Oy.YamlException error)
        {
            throw ErrorMap.Translate(error, yaml, source);
        }
    }

    private static object? FirstOfString<T>(string yaml, Oy.YamlTypeInfo info, Oy.YamlException split, string? source) => FirstOf(yaml, (Oy.YamlTypeInfo<T>)info, split, source);

    private static string Text(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadToEnd();
    }

    private static string Text(Stream stream) => StreamText.Read(stream, null);

    private static string Write<T>(T value, Oy.YamlTypeInfo<T> info)
    {
        try
        {
            return Oy.YamlSerializer.Serialize(value, info);
        }
        catch (Oy.YamlException error)
        {
            throw ErrorMap.Translate(error, default, null);
        }
    }

    private static string WriteBoxed(object? value, Oy.YamlTypeInfo info)
    {
        try
        {
            return Oy.YamlSerializer.Serialize(value, info);
        }
        catch (Oy.YamlException error)
        {
            throw ErrorMap.Translate(error, default, null);
        }
    }

    private static void ToStream(Stream stream, string text)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static void ToWriter(TextWriter writer, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(text);
    }

    private static void ToBuffer(IBufferWriter<char> writer, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            Span<char> span = writer.GetSpan(rest.Length);
            int n = Math.Min(span.Length, rest.Length);
            rest[..n].CopyTo(span);
            writer.Advance(n);
            rest = rest[n..];
        }
    }

    // A stream that cannot be decoded is a failure to read, not an exception, for the Try methods.
    private static bool TryText(Stream stream, out string? text)
    {
        try
        {
            text = Text(stream);
            return true;
        }
        catch (YamlException)
        {
            text = null;
            return false;
        }
    }

    private static bool Fail<T>(out T? value)
    {
        value = default;
        return false;
    }

    private static bool Try<T>(Func<T?> read, out T? value)
    {
        try
        {
            value = read();
            return true;
        }
        catch (YamlException)
        {
            value = default;
            return false;
        }
    }

    // ---- Serialize ----

    /// <summary>Writes a value as a YAML string.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static string Serialize<T>(T value, YamlSerializerOptions? options = null) => Write(value, InfoOf<T>(options));

    /// <summary>Writes a value as a YAML string, with the metadata of a context.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="context">The context.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static string Serialize<T>(T value, Serialization.YamlSerializerContext context) => Write(value, InfoOf<T>(context));

    /// <summary>Writes a value as a YAML string, with given metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The YAML text.</returns>
    public static string Serialize<T>(T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Write(value, typeInfo.Engine);
    }

    /// <summary>Writes a value as a YAML string, as a given type.</summary>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static string Serialize(object? value, Type inputType, YamlSerializerOptions? options = null) => WriteBoxed(value, InfoOf(inputType, options));

    /// <summary>Writes a value as a YAML string, as a given type, with the metadata of a context.</summary>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="context">The context.</param>
    /// <returns>The YAML text.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static string Serialize(object? value, Type inputType, Serialization.YamlSerializerContext context) => WriteBoxed(value, InfoOf(inputType, context));

    /// <summary>Writes a value as a YAML string, with given metadata.</summary>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The YAML text.</returns>
    public static string Serialize(object? value, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteBoxed(value, typeInfo.Engine);
    }

    /// <summary>Writes a value to a text writer.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(TextWriter writer, T value, YamlSerializerOptions? options = null) => ToWriter(writer, Write(value, InfoOf<T>(options)));

    /// <summary>Writes a value to a text writer, with the metadata of a context.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(TextWriter writer, T value, Serialization.YamlSerializerContext context) => ToWriter(writer, Write(value, InfoOf<T>(context)));

    /// <summary>Writes a value to a text writer, with given metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    public static void Serialize<T>(TextWriter writer, T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ToWriter(writer, Write(value, typeInfo.Engine));
    }

    /// <summary>Writes a value to a text writer, as a given type.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(TextWriter writer, object? value, Type inputType, YamlSerializerOptions? options = null) => ToWriter(writer, WriteBoxed(value, InfoOf(inputType, options)));

    /// <summary>Writes a value to a text writer, as a given type, with the metadata of a context.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(TextWriter writer, object? value, Type inputType, Serialization.YamlSerializerContext context) => ToWriter(writer, WriteBoxed(value, InfoOf(inputType, context)));

    /// <summary>Writes a value to a text writer, with given metadata.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    public static void Serialize(TextWriter writer, object? value, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ToWriter(writer, WriteBoxed(value, typeInfo.Engine));
    }

    /// <summary>Writes a value to a stream as UTF-8 without a byte order mark.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(Stream stream, T value, YamlSerializerOptions? options = null) => ToStream(stream, Write(value, InfoOf<T>(options)));

    /// <summary>Writes a value to a stream, with the metadata of a context.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(Stream stream, T value, Serialization.YamlSerializerContext context) => ToStream(stream, Write(value, InfoOf<T>(context)));

    /// <summary>Writes a value to a stream, with given metadata.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    public static void Serialize<T>(Stream stream, T value, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ToStream(stream, Write(value, typeInfo.Engine));
    }

    /// <summary>Writes a value to a stream, as a given type.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(Stream stream, object? value, Type inputType, YamlSerializerOptions? options = null) => ToStream(stream, WriteBoxed(value, InfoOf(inputType, options)));

    /// <summary>Writes a value to a stream, as a given type, with the metadata of a context.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(Stream stream, object? value, Type inputType, Serialization.YamlSerializerContext context) => ToStream(stream, WriteBoxed(value, InfoOf(inputType, context)));

    /// <summary>Writes a value to a stream, with given metadata.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The metadata.</param>
    public static void Serialize(Stream stream, object? value, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ToStream(stream, WriteBoxed(value, typeInfo.Engine));
    }

    /// <summary>Writes a value into a buffer writer of characters.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The buffer writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(IBufferWriter<char> writer, T value, YamlSerializerOptions? options = null) => ToBuffer(writer, Write(value, InfoOf<T>(options)));

    /// <summary>Writes a value into a buffer writer of characters, with the metadata of a context.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="writer">The buffer writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize<T>(IBufferWriter<char> writer, T value, Serialization.YamlSerializerContext context) => ToBuffer(writer, Write(value, InfoOf<T>(context)));

    /// <summary>Writes a value into a buffer writer of characters, as a given type.</summary>
    /// <param name="writer">The buffer writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(IBufferWriter<char> writer, object? value, Type inputType, YamlSerializerOptions? options = null) => ToBuffer(writer, WriteBoxed(value, InfoOf(inputType, options)));

    /// <summary>Writes a value into a buffer writer of characters, as a given type, with the metadata of a context.</summary>
    /// <param name="writer">The buffer writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="inputType">The type to write it as.</param>
    /// <param name="context">The context.</param>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static void Serialize(IBufferWriter<char> writer, object? value, Type inputType, Serialization.YamlSerializerContext context) => ToBuffer(writer, WriteBoxed(value, InfoOf(inputType, context)));

    // ---- Deserialize: string ----

    /// <summary>Reads a value from YAML text. A text with several documents gives the first.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(string yaml, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return Read(yaml, InfoOf<T>(options), Source(options));
    }

    /// <summary>Reads a value from YAML text, with the metadata of a context.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(string yaml, Serialization.YamlSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return Read(yaml, InfoOf<T>(context), Source(context.Options));
    }

    /// <summary>Reads a value from YAML text, with given metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    public static T? Deserialize<T>(string yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Read(yaml, typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value of a given type from YAML text.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(string yaml, Type returnType, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadBoxed(yaml, InfoOf(returnType, options), Source(options));
    }

    /// <summary>Reads a value of a given type from YAML text, with the metadata of a context.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(string yaml, Type returnType, Serialization.YamlSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return ReadBoxed(yaml, InfoOf(returnType, context), Source(context.Options));
    }

    /// <summary>Reads a value from YAML text, with given metadata.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(string yaml, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadBoxed(yaml, typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value from YAML text and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(string yaml, out T? value, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Oy.YamlTypeInfo<T> info = InfoOf<T>(options);
        string? source = Source(options);
        return Try(() => Read(yaml, info, source), out value);
    }

    /// <summary>Reads a value from YAML text with the metadata of a context, and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(string yaml, Serialization.YamlSerializerContext context, out T? value)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Oy.YamlTypeInfo<T> info = InfoOf<T>(context);
        string? source = Source(context.Options);
        return Try(() => Read(yaml, info, source), out value);
    }

    /// <summary>Reads a value of a given type from YAML text and tells whether it worked.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(string yaml, Type returnType, out object? value, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Oy.YamlTypeInfo info = InfoOf(returnType, options);
        string? source = Source(options);
        return Try(() => ReadBoxed(yaml, info, source), out value);
    }

    /// <summary>Reads a value of a given type from YAML text with the metadata of a context, and tells whether it worked.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(string yaml, Type returnType, Serialization.YamlSerializerContext context, out object? value)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Oy.YamlTypeInfo info = InfoOf(returnType, context);
        string? source = Source(context.Options);
        return Try(() => ReadBoxed(yaml, info, source), out value);
    }

    // ---- Deserialize: spans ----

    /// <summary>Reads a value from YAML text held in a span.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(ReadOnlySpan<char> yaml, YamlSerializerOptions? options = null) => Read(yaml, InfoOf<T>(options), Source(options));

    /// <summary>Reads a value from YAML text held in a span, with the metadata of a context.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(ReadOnlySpan<char> yaml, Serialization.YamlSerializerContext context) => Read(yaml, InfoOf<T>(context), Source(context.Options));

    /// <summary>Reads a value from YAML text held in a span, with given metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    public static T? Deserialize<T>(ReadOnlySpan<char> yaml, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Read(yaml, typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value of a given type from YAML text held in a span.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(ReadOnlySpan<char> yaml, Type returnType, YamlSerializerOptions? options = null) => ReadBoxed(yaml.ToString(), InfoOf(returnType, options), Source(options));

    /// <summary>Reads a value of a given type from YAML text held in a span, with the metadata of a context.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(ReadOnlySpan<char> yaml, Type returnType, Serialization.YamlSerializerContext context) => ReadBoxed(yaml.ToString(), InfoOf(returnType, context), Source(context.Options));

    // ---- Deserialize: readers and streams ----

    /// <summary>Reads a value from a text reader, to its end.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(TextReader reader, YamlSerializerOptions? options = null) => Read(Text(reader), InfoOf<T>(options), Source(options));

    /// <summary>Reads a value from a text reader, with the metadata of a context.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(TextReader reader, Serialization.YamlSerializerContext context) => Read(Text(reader), InfoOf<T>(context), Source(context.Options));

    /// <summary>Reads a value from a text reader, with given metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    public static T? Deserialize<T>(TextReader reader, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Read(Text(reader), typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value of a given type from a text reader.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(TextReader reader, Type returnType, YamlSerializerOptions? options = null) => ReadBoxed(Text(reader), InfoOf(returnType, options), Source(options));

    /// <summary>Reads a value of a given type from a text reader, with the metadata of a context.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(TextReader reader, Type returnType, Serialization.YamlSerializerContext context) => ReadBoxed(Text(reader), InfoOf(returnType, context), Source(context.Options));

    /// <summary>Reads a value from a text reader, with given metadata.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(TextReader reader, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadBoxed(Text(reader), typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value from a stream of text, to its end. The stream is left open.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(Stream stream, YamlSerializerOptions? options = null) => Read(Text(stream), InfoOf<T>(options), Source(options));

    /// <summary>Reads a value from a stream, with the metadata of a context.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static T? Deserialize<T>(Stream stream, Serialization.YamlSerializerContext context) => Read(Text(stream), InfoOf<T>(context), Source(context.Options));

    /// <summary>Reads a value from a stream, with given metadata.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    public static T? Deserialize<T>(Stream stream, YamlTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Read(Text(stream), typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value of a given type from a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(Stream stream, Type returnType, YamlSerializerOptions? options = null) => ReadBoxed(Text(stream), InfoOf(returnType, options), Source(options));

    /// <summary>Reads a value of a given type from a stream, with the metadata of a context.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(Stream stream, Type returnType, Serialization.YamlSerializerContext context) => ReadBoxed(Text(stream), InfoOf(returnType, context), Source(context.Options));

    /// <summary>Reads a value from a stream, with given metadata.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="typeInfo">The metadata.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The text is not valid YAML, or does not fit the type.</exception>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static object? Deserialize(Stream stream, YamlTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ReadBoxed(Text(stream), typeInfo.Engine, Source(typeInfo.Options));
    }

    /// <summary>Reads a value from a text reader and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(TextReader reader, out T? value, YamlSerializerOptions? options = null) => TryDeserialize(Text(reader), out value, options);

    /// <summary>Reads a value from a text reader with the metadata of a context, and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(TextReader reader, Serialization.YamlSerializerContext context, out T? value) => TryDeserialize(Text(reader), context, out value);

    /// <summary>Reads a value from a stream and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(Stream stream, out T? value, YamlSerializerOptions? options = null) => TryText(stream, out string? text) ? TryDeserialize(text!, out value, options) : Fail(out value);

    /// <summary>Reads a value from a stream with the metadata of a context, and tells whether it worked.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="stream">The stream.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or the default when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize<T>(Stream stream, Serialization.YamlSerializerContext context, out T? value) => TryText(stream, out string? text) ? TryDeserialize(text!, context, out value) : Fail(out value);

    /// <summary>Reads a value of a given type from a text reader and tells whether it worked.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(TextReader reader, Type returnType, out object? value, YamlSerializerOptions? options = null) => TryDeserialize(Text(reader), returnType, out value, options);

    /// <summary>Reads a value of a given type from a text reader with the metadata of a context, and tells whether it worked.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(TextReader reader, Type returnType, Serialization.YamlSerializerContext context, out object? value) => TryDeserialize(Text(reader), returnType, context, out value);

    /// <summary>Reads a value of a given type from a stream and tells whether it worked.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(Stream stream, Type returnType, out object? value, YamlSerializerOptions? options = null) => TryText(stream, out string? text) ? TryDeserialize(text!, returnType, out value, options) : Fail(out value);

    /// <summary>Reads a value of a given type from a stream with the metadata of a context, and tells whether it worked.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="returnType">The type to read.</param>
    /// <param name="context">The context.</param>
    /// <param name="value">The value, or null when the text could not be read.</param>
    /// <returns><see langword="true"/> when the text was read.</returns>
    [RequiresUnreferencedCode(Unreferenced)]
    [RequiresDynamicCode(Dynamic)]
    public static bool TryDeserialize(Stream stream, Type returnType, Serialization.YamlSerializerContext context, out object? value) => TryText(stream, out string? text) ? TryDeserialize(text!, returnType, context, out value) : Fail(out value);
}
