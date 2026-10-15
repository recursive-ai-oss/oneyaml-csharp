using System.Text;
using System.Text.RegularExpressions;
using OneYaml.Tests.Emitting;
using Xunit;

namespace OneYaml.Tests.Writing;

/// <summary>
/// Reads written text with the library's own reader and with YamlDotNet and SharpYaml and requires the same
/// event stream from all three, scalar styles and values included.
/// </summary>
internal static partial class ReadBack
{
    [GeneratedRegex(@"^=VAL( &\S+)?( <[^>]*>)? ")]
    private static partial Regex Props();

    /// <summary>The first difference between the three readers, or null when they agree.</summary>
    public static string? Difference(string yaml)
    {
        EventText.Result ours = EventText.Read(Encoding.UTF8.GetBytes(yaml));
        if (ours.Error is not null)
        {
            return "the library's reader failed: " + ours.Error.Message;
        }

        List<string> expected = [.. ours.Events.Select(Normalize)];
        string? problem = Compare("YamlDotNet", expected, () => YamlDotNetEvents.Read(yaml));
        return problem ?? Compare("SharpYaml", expected, () => SharpYamlEvents.Read(yaml));
    }

    public static void Check(string yaml)
    {
        string? problem = Difference(yaml);
        Assert.True(problem is null, problem + "\n--- document:\n" + (yaml.Length > 3000 ? yaml[..3000] + "..." : yaml));
    }

    private static string? Compare(string name, List<string> expected, Func<List<string>> read)
    {
        List<string> actual;
        try
        {
            actual = [.. read().Select(Unescaped)];
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            return name + " failed: " + e.GetType().Name + ": " + e.Message;
        }

        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            string a = i < expected.Count ? expected[i] : "<end>";
            string b = i < actual.Count ? actual[i] : "<end>";
            if (a != b)
            {
                return $"{name} differs at event {i}: library '{Cut(a)}', {name} '{Cut(b)}'";
            }
        }

        return null;
    }

    private static string Cut(string s) => s.Length > 200 ? s[..200] + "..." : s;

    // Tags and anchors are not compared; the other readers report a tag only as a mark.
    private static string Normalize(string line) => line switch
    {
        "+SEQ []" => "+SEQ",
        "+MAP {}" => "+MAP",
        _ when line.StartsWith("=VAL", StringComparison.Ordinal) => Props().Replace(line, "=VAL "),
        _ => line,
    };

    // The other readers give the raw value; the notation of the events escapes a few characters.
    private static string Unescaped(string line)
    {
        if (!line.StartsWith("=VAL ", StringComparison.Ordinal))
        {
            return line;
        }

        if (line.EndsWith(" TAG", StringComparison.Ordinal))
        {
            line = line[..^4];
        }

        var sb = new StringBuilder("=VAL ", line.Length + 8);
        sb.Append(line[5]);
        foreach (char c in line.AsSpan(6))
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\0': sb.Append("\\0"); break;
                case '\b': sb.Append("\\b"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
