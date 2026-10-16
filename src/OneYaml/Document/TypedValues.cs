using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneYaml.Emitting;
using OneYaml.Text;

namespace OneYaml.Documents;

/// <summary>
/// Turns values of types the plain value model does not know into value trees by writing them with the
/// serializer and reading the text back. One model of what a type looks like in YAML, for documents and
/// for <see cref="YamlSerializer"/>.
/// </summary>
internal static class TypedValues
{
    public static ValueTree Of<T>(T value, YamlTypeInfo<T> typeInfo, YamlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            return Read(YamlSerializer.SerializeToUtf8Bytes(value, typeInfo), limits);
        }
        catch (NotSupportedException ex)
        {
            throw NoForm(typeof(T), ex, nameof(value));
        }
        catch (YamlException ex)
        {
            throw InWrittenValue(ex);
        }
    }

    [RequiresUnreferencedCode("Reflection-based serialization reads members that trimming can remove. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    [RequiresDynamicCode("Reflection-based serialization makes generic types at run time. Pass a YamlTypeInfo from a YamlSerializerContext instead.")]
    public static ValueTree Of<T>(T value, YamlSerializerOptions? options, YamlLimits limits)
    {
        try
        {
            return Read(YamlSerializer.SerializeToUtf8Bytes(value, options), limits);
        }
        catch (NotSupportedException ex)
        {
            throw NoForm(typeof(T), ex, nameof(value));
        }
        catch (YamlException ex)
        {
            throw InWrittenValue(ex);
        }
    }

    // The value is written to text nobody sees and read back, so a line and column in an error would point into
    // that text. Keep the category and what went wrong, say where it was, and drop the position and the path.
    private static YamlException InWrittenValue(YamlException ex)
    {
        string message = ex.Message;
        int at = message.LastIndexOf(" (line ", StringComparison.Ordinal);
        if (at >= 0 && message.EndsWith(')'))
        {
            message = message[..at];
        }

        return new YamlException("In the written value: " + message, ex.Category, 0, 0, -1, innerException: ex);
    }

    // The serializer reports a type it cannot describe with NotSupportedException; a document method reports
    // it as a bad argument, the way it does for a value outside the plain model.
    private static ArgumentException NoForm(Type type, Exception inner, string parameter)
        => new($"A value of type {type.Name} has no YAML form: {inner.Message}", parameter, inner);

    /// <summary>Writes a value of a type found only at run time, with reflection.</summary>
    /// <remarks>
    /// This is the way for <c>Set(path, object)</c>, whose callers cannot carry trimming annotations for every
    /// value they pass. It is open only while the reflection switch is on; programs that trim or compile ahead
    /// of time have it off and use the overloads that take a <see cref="YamlTypeInfo{T}"/>.
    /// </remarks>
    public static ValueTree OfObject(object value, YamlLimits limits) => OfObject(value, limits, YamlSerializer.IsReflectionEnabledByDefault);

    // The switch is a parameter so a test can run the switched-off branch in a process where it is on.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Values outside the plain value model are written with reflection only while YamlSerializer.IsReflectionEnabledByDefault is true. That switch is false when trimming or compiling ahead of time, the trimmer folds the check, and this method then throws ArgumentException naming the overloads that take a YamlTypeInfo.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same switch as above: with it off, this method throws before any reflection runs.")]
    internal static ValueTree OfObject(object value, YamlLimits limits, bool reflectionEnabled)
    {
        if (value is JsonNode or JsonElement)
        {
            // The System.Text.Json types have converters that need no reflection, and a number keeps its text.
            return OfJson(value, limits);
        }

        Type type = value.GetType();
        if (!reflectionEnabled)
        {
            throw new ArgumentException($"A value of type {type.Name} is not in the plain value model (strings, numbers, booleans, dictionaries and sequences of these), and reflection is turned off in this application. Use the overload that takes a YamlTypeInfo for this type, or turn the OneYaml.YamlSerializer.IsReflectionEnabledByDefault switch on.", nameof(value));
        }

        try
        {
            YamlTypeInfo info = YamlSerializerOptions.Default.GetReflectiveTypeInfo(type);
            return Read(System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value, info)), limits);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new ArgumentException($"A value of type {type.Name} has no YAML form: {ex.Message} Use the overload that takes a YamlTypeInfo for this type.", nameof(value), ex);
        }
        catch (YamlException ex)
        {
            throw InWrittenValue(ex);
        }
    }

    private static ValueTree OfJson(object value, YamlLimits limits)
    {
        try
        {
            using var buffer = new PooledBufferWriter();
            YamlSerializerOptions options = YamlSerializerOptions.Default;
            Utf8YamlEmitter emitter = Utf8YamlEmitter.Rent(buffer, options);
            try
            {
                if (value is JsonNode node)
                {
                    JsonTrees.WriteNode(emitter, node);
                }
                else
                {
                    JsonTrees.WriteElement(emitter, (JsonElement)value);
                }

                emitter.Finish();
            }
            finally
            {
                Utf8YamlEmitter.Return(emitter);
            }

            return Read(buffer.ToArray(), limits);
        }
        catch (YamlException ex)
        {
            throw InWrittenValue(ex);
        }
    }

    private static ValueTree Read(byte[] utf8, YamlLimits limits)
    {
        using YamlInput input = YamlInput.FromBytes(utf8, limits);
        DocState state = DocState.Build(input.Utf8Text, input.Encoding, input.HasByteOrderMark, limits, false);
        try
        {
            var tree = new ValueTree();
            var budget = new AliasBudget(state, limits);
            tree.AppendFrom(state, state.Doc(0).Root, ref budget);
            return tree;
        }
        finally
        {
            state.Dispose();
        }
    }
}
