using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.Serialization;

/// <summary>Reads YAML text into .NET objects by YamlDotNet's rules.</summary>
public sealed class Deserializer : IDeserializer
{
    // The nesting depth at which YamlDotNet's deserializer gives up by default.
    internal const int DefaultMaxRecursion = 130;

    private readonly Settings _settings;
    private NodeBinder? _binder;

    /// <summary>Creates a deserializer with the default settings.</summary>
    public Deserializer()
        : this(new Settings { MaxRecursion = DefaultMaxRecursion })
    {
    }

    internal Deserializer(Settings settings)
    {
        _settings = settings;
    }

    // Created on first use: the binder reads types by reflection, and only the read methods carry that warning.
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private NodeBinder GetBinder() => _binder ?? Interlocked.CompareExchange(ref _binder, new NodeBinder(_settings), null) ?? _binder!;

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public T Deserialize<T>(string input) => (T)(ReadText(input, typeof(T)) ?? default(T)!);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public T Deserialize<T>(TextReader input) => (T)(ReadReader(input, typeof(T)) ?? default(T)!);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public T Deserialize<T>(IParser parser) => (T)(ReadParser(parser, typeof(T)) ?? default(T)!);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(string input) => ReadText(input, typeof(object));

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(TextReader input) => ReadReader(input, typeof(object));

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(IParser parser) => ReadParser(parser, typeof(object));

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(TextReader input, Type type) => ReadReader(input, type);

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private object? ReadReader(TextReader input, Type type)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(type);
        using Loaded source = Loaded.Parse(input, _settings.Limits);
        return ReadLoaded(source, type);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(string input, Type type) => ReadText(input, type);

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private object? ReadText(string input, Type type)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(type);
        using Loaded source = Loaded.Parse(input, _settings.Limits);
        return ReadLoaded(source, type);
    }

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private object? ReadLoaded(Loaded source, Type type)
    {
        var state = source.State;
        int documents = state.NoRealDocument ? 0 : state.DocCount;
        if (documents == 0)
        {
            return null;
        }

        if (documents > 1)
        {
            Mark at = source.MarkAt(state.Doc(1).StartPos);
            throw new Core.YamlException(at, at, $"Expected 'StreamEnd', got 'DocumentStart' (at {at}).");
        }

        int root = state.Doc(0).Root;
        if (root < 0)
        {
            return Nullable.GetUnderlyingType(type) is not null || !type.IsValueType ? null : Activator.CreateInstance(type);
        }

        return GetBinder().Bind(new BindContext(source, _settings.Limits), root, type);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public object? Deserialize(IParser parser, Type type) => ReadParser(parser, type);

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private object? ReadParser(IParser parser, Type type)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(type);
        if (parser is not Parser own)
        {
            throw new NotSupportedException("Only parsers created by this library can be read from. See docs/compat/yamldotnet.md for the parts of YamlDotNet that are not covered.");
        }

        // Like YamlDotNet: take a stream start and a document start if they are there, read one value, then take the document end.
        parser.TryConsume<StreamStart>(out _);
        parser.TryConsume<DocumentStart>(out _);
        if (parser.Current is DocumentEnd or StreamEnd)
        {
            return null;
        }

        object? value = GetBinder().BindAtParser(own, type);
        parser.TryConsume<DocumentEnd>(out _);
        return value;
    }
}
