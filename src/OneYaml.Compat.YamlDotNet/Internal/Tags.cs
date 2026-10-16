using System;
using System.Collections.Generic;
using System.Text;
using OneYaml.Compat.YamlDotNet.Core;

namespace OneYaml.Compat.YamlDotNet.Internal;

// Conversion between the tag text of a document (!!str, !local, !<verbatim>) and the full tag names YamlDotNet code uses.
internal static class Tags
{
    public const string Prefix = "tag:yaml.org,2002:";
    public const string Str = Prefix + "str";
    public const string Int = Prefix + "int";
    public const string Float = Prefix + "float";
    public const string Bool = Prefix + "bool";
    public const string Null = Prefix + "null";
    public const string Map = Prefix + "map";
    public const string Seq = Prefix + "seq";

    public static TagName FromRaw(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
        {
            return default;
        }

        string text = Encoding.UTF8.GetString(raw);
        if (text.StartsWith("!!", StringComparison.Ordinal))
        {
            return new TagName(Prefix + text[2..]);
        }

        if (text.StartsWith("!<", StringComparison.Ordinal) && text.EndsWith('>'))
        {
            return new TagName(text[2..^1]);
        }

        return new TagName(text);
    }

    // A written tag resolved with the handles a document declares: !!x and !x use the default prefixes unless redeclared,
    // !h!x needs a declared handle. The result is the prefix a directive gave (or none) and the rest, whose text joined
    // is the full tag.
    public static (TagPrefix? Prefix, string Suffix) Split(ReadOnlySpan<byte> raw, Dictionary<string, TagPrefix>? handles)
    {
        if (handles is null)
        {
            return (null, FromRaw(raw).Value!);
        }

        string text = Encoding.UTF8.GetString(raw);
        if (text.StartsWith("!<", StringComparison.Ordinal))
        {
            return (null, FromRaw(raw).Value!);
        }

        string handle;
        string suffix;
        if (text.StartsWith("!!", StringComparison.Ordinal))
        {
            (handle, suffix) = ("!!", text[2..]);
        }
        else
        {
            int second = text.IndexOf('!', 1);
            (handle, suffix) = second < 0 ? ("!", text[1..]) : (text[..(second + 1)], text[(second + 1)..]);
        }

        if (handles.TryGetValue(handle, out TagPrefix? prefix))
        {
            return (prefix, Uri.UnescapeDataString(suffix));
        }

        return (null, FromRaw(raw).Value!);
    }

    // The form written in a document: !!str for the core tags, !local as is, anything else verbatim.
    public static string ToText(TagName tag)
    {
        string? value = tag.Value;
        if (value is null)
        {
            return string.Empty;
        }

        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return "!!" + value[Prefix.Length..];
        }

        return value.StartsWith('!') ? value : "!<" + value + ">";
    }
}
