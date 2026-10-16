using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Internal;

namespace OneYaml.Compat.YamlDotNet.Serialization;

/// <summary>Writes .NET objects as YAML text, laid out the way YamlDotNet does.</summary>
public sealed class Serializer : ISerializer
{
    // The nesting depth at which YamlDotNet's serializer gives up by default.
    internal const int DefaultMaxRecursion = 50;

    private readonly Settings _settings;
    private TypeModels? _models;

    /// <summary>Creates a serializer with the default settings.</summary>
    public Serializer()
        : this(new Settings { MaxRecursion = DefaultMaxRecursion })
    {
    }

    internal Serializer(Settings settings)
    {
        _settings = settings;
    }

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private TypeModels GetModels()
    {
        // Refused here, at first use, rather than accepted and ignored: the output would differ from what the builder asked for.
        if (_settings.EnsureRoundtrip)
        {
            throw new NotSupportedException("EnsureRoundtrip is not supported by OneYaml.Compat.YamlDotNet. Remove the call and give the target type when reading the output back. See docs/compat/yamldotnet.md for what is and what is not covered.");
        }

        return _models ?? Interlocked.CompareExchange(ref _models, new TypeModels(_settings), null) ?? _models!;
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public string Serialize(object? graph) => SerializeText(graph, null);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public string Serialize(object? graph, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return SerializeText(graph, type);
    }

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private string SerializeText(object? graph, Type? type)
    {
        EventBuffer buffer = new GraphWriter(_settings, GetModels()).Write(graph, type);
        var renderer = new TextRenderer(_settings);
        renderer.Document(buffer, 0, buffer.Count, false);
        string text = Encoding.UTF8.GetString(renderer.Written);
        return _settings.WithNewLine(text);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public void Serialize(TextWriter writer, object? graph)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(SerializeText(graph, null));
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public void Serialize(TextWriter writer, object? graph, Type type)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(type);
        writer.Write(SerializeText(graph, type));
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public void Serialize(IEmitter emitter, object? graph) => SerializeToEmitter(emitter, graph, null);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    public void Serialize(IEmitter emitter, object? graph, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        SerializeToEmitter(emitter, graph, type);
    }

    [RequiresUnreferencedCode(Messages.Reflection)]
    [RequiresDynamicCode(Messages.Reflection)]
    private void SerializeToEmitter(IEmitter emitter, object? graph, Type? type)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        EventBuffer buffer = new GraphWriter(_settings, GetModels()).Write(graph, type);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        EventTranslator.Emit(buffer, 0, buffer.Count, emitter);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
    }
}

// Turns buffered output events back into the event objects an emitter takes.
internal static class EventTranslator
{
    public static void Emit(EventBuffer buffer, int from, int to, IEmitter emitter)
    {
        var names = new Dictionary<int, string>();
        int named = 0;
        for (int i = from; i < to; i++)
        {
            if (buffer[i].NeedsAnchor)
            {
                names[i] = buffer[i].Anchor ?? "o" + named++.ToString(CultureInfo.InvariantCulture);
            }
            else if (buffer[i].Anchor is { } given)
            {
                names[i] = given;
            }
        }

        for (int i = from; i < to; i++)
        {
            ref Ev e = ref buffer[i];
            AnchorName anchor = names.TryGetValue(i, out string? n) ? AnchorName.AsWritten(n) : AnchorName.Empty;
            TagName tag = string.IsNullOrEmpty(e.Tag) ? TagName.Empty : new TagName(e.Tag);
            switch (e.Kind)
            {
                case EvKind.Scalar:
                    emitter.Emit(new Scalar(anchor, tag, e.Text ?? string.Empty, e.Style, tag.IsEmpty, tag.IsEmpty));
                    break;
                case EvKind.SequenceStart:
                    emitter.Emit(new SequenceStart(anchor, tag, tag.IsEmpty, e.Flow ? SequenceStyle.Flow : SequenceStyle.Any));
                    break;
                case EvKind.MappingStart:
                    emitter.Emit(new MappingStart(anchor, tag, tag.IsEmpty, e.Flow ? MappingStyle.Flow : MappingStyle.Any));
                    break;
                case EvKind.SequenceEnd:
                    emitter.Emit(new SequenceEnd());
                    break;
                case EvKind.MappingEnd:
                    emitter.Emit(new MappingEnd());
                    break;
                default:
                    emitter.Emit(new AnchorAlias(AnchorName.AsWritten(e.Target >= 0 ? names[e.Target] : e.Text)));
                    break;
            }
        }
    }
}
