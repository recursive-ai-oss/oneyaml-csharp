using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.Serialization;

namespace OneYaml.Compat.YamlDotNet.Internal;

// Turns an object graph into output events.
[RequiresUnreferencedCode("Writes arbitrary objects by reflection.")]
[RequiresDynamicCode("Writes arbitrary objects by reflection.")]
internal sealed class GraphWriter
{
    private readonly Settings _settings;
    private readonly TypeModels _models;
    private EventBuffer _buffer = null!;
    private Dictionary<object, int>? _seen;
    private int _depth;
    private BufferEmitter? _emitter;

    public GraphWriter(Settings settings, TypeModels models)
    {
        _settings = settings;
        _models = models;
    }

    public EventBuffer Write(object? graph, Type? type)
    {
        _buffer = new EventBuffer();
        _seen = _settings.DisableAliases ? null : new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        _depth = 0;
        _emitter = new BufferEmitter(_buffer);
        WriteValue(graph, type, ScalarStyle.Any, isKey: false);
        return _buffer;
    }

    private ObjectSerializer Nested => (value, type) => WriteValue(value, type, ScalarStyle.Any, isKey: false);

    private void WriteValue(object? value, Type? declared, ScalarStyle memberStyle, bool isKey)
    {
        if (++_depth > _settings.MaxRecursion)
        {
            throw new MaximumRecursionLevelReachedException("Too much recursion when traversing the object graph.");
        }

        WriteCore(value, declared, memberStyle, isKey);
        _depth--;
    }

    private void WriteCore(object? value, Type? declared, ScalarStyle memberStyle, bool isKey)
    {
        if (value is null)
        {
            _buffer.Add(new Ev { Kind = EvKind.Scalar, Text = string.Empty, Style = ScalarStyle.Plain });
            return;
        }

        Type type = declared is not null && declared.IsInstanceOfType(value) ? (declared.IsInterface || declared.IsAbstract || declared == typeof(object) ? value.GetType() : declared) : value.GetType();
        foreach (IYamlTypeConverter converter in _settings.Converters)
        {
            if (converter.Accepts(type))
            {
                converter.WriteYaml(_emitter!, value, type, Nested);
                return;
            }
        }

        TypeModel model = _models.Get(type);
        if (model.AttributeConverter is { } attributed)
        {
            attributed.WriteYaml(_emitter!, value, type, Nested);
            return;
        }

        string? tag = _settings.TypeToTag.Count > 0 && _settings.TypeToTag.TryGetValue(type, out TagName mapped) ? mapped.Value : null;
        switch (model.Kind)
        {
            case TypeKind.Scalar:
            case TypeKind.Enum:
                WriteScalar(value, model, tag, memberStyle, isKey);
                return;
            case TypeKind.Convertible:
                ((IYamlConvertible)value).Write(_emitter!, Nested);
                return;
        }

        // Reference types that can be reached twice are written once and aliased afterwards.
        int start = _buffer.Count;
        if (_seen is not null)
        {
            if (_seen.TryGetValue(value, out int first))
            {
                _buffer[first].NeedsAnchor = true;
                _buffer.Add(new Ev { Kind = EvKind.Alias, Target = first });
                return;
            }

            _seen[value] = start;
        }

        switch (model.Kind)
        {
            case TypeKind.Array:
            case TypeKind.Collection:
                _buffer.Add(new Ev { Kind = EvKind.SequenceStart, Tag = tag, Target = -1 });
                foreach (object? item in (IEnumerable)value)
                {
                    WriteValue(item, model.Element, memberStyle, isKey: false);
                }

                _buffer.Add(new Ev { Kind = EvKind.SequenceEnd });
                return;
            case TypeKind.Dictionary:
                WriteDictionary(value, model, tag);
                return;
            default:
                WritePoco(value, model, tag);
                return;
        }
    }

    private void WriteDictionary(object value, TypeModel model, string? tag)
    {
        _buffer.Add(new Ev { Kind = EvKind.MappingStart, Tag = tag, Target = -1 });
        if (value is IDictionary dictionary)
        {
            IDictionaryEnumerator entries = dictionary.GetEnumerator();
            while (entries.MoveNext())
            {
                WriteValue(entries.Key, model.KeyType, ScalarStyle.Any, isKey: true);
                WriteValue(entries.Value, model.ValueType, ScalarStyle.Any, isKey: false);
            }
        }
        else
        {
            foreach (object? pair in (IEnumerable)value)
            {
                Type pairType = pair!.GetType();
                WriteValue(pairType.GetProperty("Key")!.GetValue(pair), model.KeyType, ScalarStyle.Any, isKey: true);
                WriteValue(pairType.GetProperty("Value")!.GetValue(pair), model.ValueType, ScalarStyle.Any, isKey: false);
            }
        }

        _buffer.Add(new Ev { Kind = EvKind.MappingEnd });
    }

    private void WritePoco(object value, TypeModel model, string? tag)
    {
        _buffer.Add(new Ev { Kind = EvKind.MappingStart, Tag = tag, Target = -1 });
        foreach (MemberModel member in model.Members)
        {
            object? memberValue = member.Get(value);
            if (!ShouldWrite(member, memberValue))
            {
                continue;
            }

            _buffer.Add(new Ev { Kind = EvKind.Scalar, Text = member.Key, IsString = true, Target = -1 });
            WriteValue(memberValue, member.SerializeAs, member.Style, isKey: false);
        }

        _buffer.Add(new Ev { Kind = EvKind.MappingEnd });
    }

    private bool ShouldWrite(MemberModel member, object? value)
    {
        switch (member.Handling ?? _settings.Defaults)
        {
            case DefaultValuesHandling.OmitNull:
                return value is not null;
            case DefaultValuesHandling.OmitDefaults:
                object? fallback = member.HasDefaultValue ? member.DefaultValue : member.Type.IsValueType ? Activator.CreateInstance(member.Type) : null;
                return !Equals(value, fallback);
            case DefaultValuesHandling.OmitEmptyCollections:
                return !(value is IEnumerable sequence && value is not string && !sequence.GetEnumerator().MoveNext());
            default:
                return true;
        }
    }

    private void WriteScalar(object value, TypeModel model, string? tag, ScalarStyle memberStyle, bool isKey)
    {
        var ev = new Ev { Kind = EvKind.Scalar, Tag = tag, Target = -1 };
        switch (value)
        {
            case string text:
                ev.Text = text;
                ev.IsString = true;
                break;
            case char c:
                ev.Text = c.ToString();
                ev.IsString = true;
                break;
            case bool b:
                ev.Text = b ? "true" : "false";
                break;
            case double d:
                ev.Text = double.IsNaN(d) ? ".nan" : double.IsPositiveInfinity(d) ? ".inf" : double.IsNegativeInfinity(d) ? "-.inf" : ScalarRules.FormatNumber(d);
                break;
            case float f:
                ev.Text = float.IsNaN(f) ? ".nan" : float.IsPositiveInfinity(f) ? ".inf" : float.IsNegativeInfinity(f) ? "-.inf" : ScalarRules.FormatNumber(f);
                break;
            case DateTime dt:
                ev.Text = dt.ToString("o", CultureInfo.InvariantCulture);
                break;
            case DateTimeOffset dto:
                ev.Text = dto.ToString("o", CultureInfo.InvariantCulture);
                break;
            case TimeSpan ts:
                ev.Text = ts.ToString("c", CultureInfo.InvariantCulture);
                break;
            case DateOnly date:
                ev.Text = date.ToString("o", CultureInfo.InvariantCulture);
                break;
            case TimeOnly time:
                ev.Text = time.ToString("o", CultureInfo.InvariantCulture);
                break;
            case Uri uri:
                ev.Text = uri.OriginalString;
                ev.IsString = true;
                break;
            case Version version:
                ev.Text = version.ToString();
                break;
            case Guid guid:
                ev.Text = guid.ToString("D", CultureInfo.InvariantCulture);
                break;
            case BigInteger big:
                ev.Text = big.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                if (model.Kind == TypeKind.Enum)
                {
                    ev.Text = model.EnumToName!.TryGetValue(value, out string? name) ? name : value.ToString() ?? string.Empty;
                    ev.IsString = true;
                }
                else
                {
                    ev.Text = ScalarRules.FormatNumber(value);
                }

                break;
        }

        if (ev.IsString)
        {
            ev.Style = memberStyle != ScalarStyle.Any ? memberStyle : _settings.DefaultScalarStyle;
        }

        _buffer.Add(ev);
    }
}

// Collects the events a converter writes into the output buffer.
internal sealed class BufferEmitter : IEmitter
{
    private readonly EventBuffer _buffer;

    public BufferEmitter(EventBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Emit(ParsingEvent parsingEvent)
    {
        switch (parsingEvent)
        {
            case Scalar s:
                _buffer.Add(new Ev { Kind = EvKind.Scalar, Text = s.Value, Style = s.Style, Tag = s.Tag.Value, Anchor = s.Anchor.Value, Target = -1 });
                break;
            case SequenceStart q:
                _buffer.Add(new Ev { Kind = EvKind.SequenceStart, Flow = q.Style == SequenceStyle.Flow, Tag = q.Tag.Value, Anchor = q.Anchor.Value, Target = -1 });
                break;
            case MappingStart m:
                _buffer.Add(new Ev { Kind = EvKind.MappingStart, Flow = m.Style == MappingStyle.Flow, Tag = m.Tag.Value, Anchor = m.Anchor.Value, Target = -1 });
                break;
            case SequenceEnd:
                _buffer.Add(new Ev { Kind = EvKind.SequenceEnd });
                break;
            case MappingEnd:
                _buffer.Add(new Ev { Kind = EvKind.MappingEnd });
                break;
            case AnchorAlias a:
                _buffer.Add(new Ev { Kind = EvKind.Alias, Text = a.Value.Value, Target = -1 });
                break;
            default:
                break;
        }
    }
}
