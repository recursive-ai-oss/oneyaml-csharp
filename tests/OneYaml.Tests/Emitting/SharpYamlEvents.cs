using System.Collections.Generic;
using System.IO;
using SharpYaml;
using SharpYaml.Events;

namespace OneYaml.Tests.Emitting;

/// <summary>Event lists from SharpYaml's parser, in the same form as <see cref="YamlDotNetEvents"/>.</summary>
internal static class SharpYamlEvents
{
    // The reader refuses more than 64 levels unless told otherwise; the deep workloads go further.
    private const int MaxDepth = 1000;

    /// <summary>Parses the text; scalar lines look like <c>=VAL :value</c> (style mark, then the value).</summary>
    public static List<string> Read(string text)
    {
        var events = new List<string>();
        var parser = Parser.CreateParser(new StringReader(text), MaxDepth);
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
                    events.Add("=VAL " + Mark(s.Style) + s.Value + (string.IsNullOrEmpty(s.Tag) ? string.Empty : " TAG"));
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
