using System.Text;
using OneYaml.Parsing;
using OneYaml.Text;

namespace OneYaml.Tests;

/// <summary>Reads a stream with the internal reader and renders events in the yaml-test-suite notation.</summary>
internal static class EventText
{
    /// <summary>The events produced before the stream ended or an error was found.</summary>
    internal sealed record Result(List<string> Events, YamlException? Error);

    internal static Result Read(byte[] input, YamlLimits? limits = null)
    {
        var events = new List<string>();
        try
        {
            using YamlInput text = YamlInput.FromBytes(input, limits ?? YamlLimits.Default);
            using var reader = new YamlReader(text.Utf8Text, new YamlReaderOptions { Limits = limits });
            while (reader.Read())
            {
                events.Add(Format(in reader));
            }
        }
        catch (YamlException ex)
        {
            return new Result(events, ex);
        }

        return new Result(events, null);
    }

    internal static string Format(in YamlReader r)
    {
        switch (r.TokenType)
        {
            case YamlTokenType.StreamStart:
                return "+STR";
            case YamlTokenType.StreamEnd:
                return "-STR";
            case YamlTokenType.DocumentStart:
                return r.IsImplicit ? "+DOC" : "+DOC ---";
            case YamlTokenType.DocumentEnd:
                return r.IsImplicit ? "-DOC" : "-DOC ...";
            case YamlTokenType.MappingStart:
                return "+MAP" + (r.CollectionStyle == CollectionStyle.Flow ? " {}" : string.Empty) + Props(in r);
            case YamlTokenType.MappingEnd:
                return "-MAP";
            case YamlTokenType.SequenceStart:
                return "+SEQ" + (r.CollectionStyle == CollectionStyle.Flow ? " []" : string.Empty) + Props(in r);
            case YamlTokenType.SequenceEnd:
                return "-SEQ";
            case YamlTokenType.Alias:
                return "=ALI *" + Encoding.UTF8.GetString(r.Anchor);
            case YamlTokenType.Scalar:
                char style = r.ScalarStyle switch
                {
                    ScalarStyle.Plain => ':',
                    ScalarStyle.SingleQuoted => '\'',
                    ScalarStyle.DoubleQuoted => '"',
                    ScalarStyle.Literal => '|',
                    _ => '>',
                };
                return "=VAL" + Props(in r) + " " + style + Escape(r.GetString());
            default:
                return "?" + r.TokenType;
        }
    }

    private static string Props(in YamlReader r)
    {
        var sb = new StringBuilder();
        if (r.HasAnchor)
        {
            sb.Append(" &").Append(Encoding.UTF8.GetString(r.Anchor));
        }

        if (r.HasTag)
        {
            sb.Append(" <").Append(r.GetResolvedTag()).Append('>');
        }

        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
