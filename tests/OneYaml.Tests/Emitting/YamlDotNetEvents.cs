using System.Collections.Generic;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OneYaml.Tests.Emitting;

/// <summary>Event lists from YamlDotNet's parser, in a form shared with the SharpYaml reader.</summary>
internal static class YamlDotNetEvents
{
    /// <summary>Parses the text; scalar lines look like <c>=VAL :value</c> (style mark, then the value).</summary>
    public static List<string> Read(string text)
    {
        var events = new List<string>();
        var parser = new Parser(new StringReader(text));
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case StreamStart: events.Add("+STR"); break;
                case StreamEnd: events.Add("-STR"); break;
                case DocumentStart: events.Add("+DOC"); break;
                case DocumentEnd: events.Add("-DOC"); break;
                case MappingStart: events.Add("+MAP"); break;
                case MappingEnd: events.Add("-MAP"); break;
                case SequenceStart: events.Add("+SEQ"); break;
                case SequenceEnd: events.Add("-SEQ"); break;
                case Scalar s:
                    events.Add("=VAL " + Mark(s.Style) + s.Value + (s.Tag.IsEmpty ? string.Empty : " TAG"));
                    break;
                default: events.Add("?" + parser.Current!.GetType().Name); break;
            }
        }

        return events;
    }

    private static char Mark(ScalarStyle style) => style switch
    {
        ScalarStyle.SingleQuoted => '\'',
        ScalarStyle.DoubleQuoted => '"',
        ScalarStyle.Literal => '|',
        ScalarStyle.Folded => '>',
        _ => ':',
    };
}
