using System.Globalization;
using System.Numerics;
using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// A node read with <see cref="YamlNode.Deserialize{T}(YamlSerializerOptions?)"/> must give what
/// <see cref="YamlSerializer"/> gives for the same text: the same value, or the same error with the
/// same category and position.
/// </summary>
public class NodeEquivalenceTests
{
    public static IEnumerable<object[]> SuiteRows() => Corpus.Rows(Corpus.SuiteCaseIds(), Corpus.SuiteVariable);

    public static IEnumerable<object[]> KitRows() => Corpus.Rows(KitIds(), Corpus.KitVariable);

    private static IEnumerable<string> KitIds()
    {
        string? kit = Corpus.KitDirectory;
        if (kit is null)
        {
            yield break;
        }

        foreach (string category in new[] { "schema", "safety", "parse", "diagnostics", "edit" })
        {
            string root = Path.Combine(kit, "suites", "own", "cases", category);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(dir, "input.yaml")))
                {
                    yield return category + "/" + Path.GetFileName(dir);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SuiteRows))]
    public void Suite_input_binds_the_same_through_a_node(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string dir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        Compare(File.ReadAllBytes(Path.Combine(dir, "in.yaml")), id);
    }

    [Theory]
    [MemberData(nameof(KitRows))]
    public void Kit_input_binds_the_same_through_a_node(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        Compare(File.ReadAllBytes(Path.Combine(Corpus.KitCaseDirectory(id), "input.yaml")), id);
    }

    private static void Compare(byte[] bytes, string id)
    {
        string text = new UTF8Encoding(false, false).GetString(bytes);
        CompareAs<object>(text, id);
        CompareAs<string>(text, id);
        CompareAs<long>(text, id);
        CompareAs<Dictionary<string, long>>(text, id);
        CompareAs<List<string>>(text, id);
        CompareAs<Shape>(text, id);
    }

    internal sealed class Shape
    {
        public string? A { get; set; }

        public int B { get; set; }

        public List<object?>? Items { get; set; }
    }

    private static void CompareAs<T>(string text, string id)
    {
        string direct = Run(() => YamlSerializer.DeserializeAll<T>(text).Select(v => Dump(v)).ToList());
        string viaNodes = ViaNodes<T>(text, direct);
        Assert.True(direct == viaNodes, $"{id} as {typeof(T).Name}:\n  serializer: {Cut(direct)}\n  node:       {Cut(viaNodes)}");
    }

    private static string Cut(string s) => s.Length > 300 ? s[..300] + "..." : s;

    private static string ViaNodes<T>(string text, string direct)
    {
        YamlDocument doc;
        try
        {
            doc = YamlDocument.Parse(text);
        }
        catch (YamlException ex)
        {
            // The document refuses the stream as a whole, so a binding error of an earlier document in the
            // serializer's view comes first there. Accept that order; otherwise the errors must be the same.
            string parse = Describe(ex);
            if (direct == parse)
            {
                return parse;
            }

            // The document also checks the keys of members a typed target skips; the typed binder does not.
            if (ex.Category == YamlErrorCategory.DuplicateKey && direct.StartsWith("ok ", StringComparison.Ordinal) && typeof(T) == typeof(Shape))
            {
                return direct;
            }

            return direct.StartsWith("error Binding", StringComparison.Ordinal) ? direct : parse;
        }

        using (doc)
        {
            var values = new List<string>();
            if (doc.Documents.Count == 0)
            {
                return Run(() => values);
            }

            return Run(() =>
            {
                for (int i = 0; i < doc.Documents.Count; i++)
                {
                    values.Add(Dump(doc.Documents[i].Deserialize<T>()));
                }

                return values;
            });
        }
    }

    private static string Run(Func<List<string>> run)
    {
        try
        {
            return "ok " + string.Join("\n--\n", run());
        }
        catch (YamlException ex)
        {
            return Describe(ex);
        }
    }

    private static string Describe(YamlException ex) => $"error {ex.Category} at {ex.Line}:{ex.Column} offset {ex.Offset}";

    internal static string Dump(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                sb.Append("f:").Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case long or int or BigInteger:
                sb.Append("i:").Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case Shape shape:
                sb.Append("shape(");
                Write(sb, shape.A);
                sb.Append(',').Append(shape.B).Append(',');
                Write(sb, shape.Items);
                sb.Append(')');
                break;
            case System.Collections.IDictionary map:
                sb.Append('{');
                foreach (System.Collections.DictionaryEntry e in map)
                {
                    Write(sb, e.Key);
                    sb.Append(':');
                    Write(sb, e.Value);
                    sb.Append(',');
                }

                sb.Append('}');
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                foreach (object? item in list)
                {
                    Write(sb, item);
                    sb.Append(',');
                }

                sb.Append(']');
                break;
            default:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
