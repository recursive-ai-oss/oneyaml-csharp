using System;
using System.Diagnostics.CodeAnalysis;
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
    }

    // The serializer reports a type it cannot describe with NotSupportedException; a document method reports
    // it as a bad argument, the way it does for a value outside the plain model.
    private static ArgumentException NoForm(Type type, Exception inner, string parameter)
        => new($"A value of type {type.Name} has no YAML form: {inner.Message}", parameter, inner);

    /// <summary>Writes a value of a type found only at run time, with reflection.</summary>
    /// <remarks>
    /// This is the way for <c>Set(path, object)</c>, whose callers cannot carry trimming annotations for every
    /// value they pass. It uses the same reflection metadata as the annotated overloads and says so in its
    /// error when reflection is switched off; programs that trim or compile ahead of time use the overloads
    /// that take a <see cref="YamlTypeInfo{T}"/>.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A value of a type outside the plain value model is written by reflection, as the reflection-based serializer overloads do; the typed overloads take generated metadata instead, and a program with reflection switched off gets an ArgumentException that says so.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Same as above: the typed overloads are the ahead-of-time route.")]
    public static ValueTree OfObject(object value, YamlLimits limits)
    {
        Type type = value.GetType();
        try
        {
            YamlTypeInfo info = YamlSerializerOptions.Default.GetReflectiveTypeInfo(type);
            return Read(System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value, info)), limits);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new ArgumentException($"A value of type {type.Name} has no YAML form: {ex.Message} Use the overload that takes a YamlTypeInfo for this type.", nameof(value), ex);
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
