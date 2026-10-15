using System.Globalization;
using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Edits parse only the part of the text they touch. Every state they make is compared with a fresh
/// parse of the same bytes (see <see cref="SpliceCheck"/>); these tests count how much was parsed
/// and compare the bytes with what a whole-text parse per edit gives.
/// </summary>
public class IncrementalEditTests
{
    private static string Records(int count)
    {
        var sb = new StringBuilder("# a list of records\nitems:\n");
        for (int i = 0; i < count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - id: {i}\n    name: item-{i}\n    tags: [a, b, c{i}]\n    meta:\n      owner: team-{i % 7}\n      # how good it is\n      score: {i}.5   # trailing\n");
        }

        sb.Append("end: true\n");
        return sb.ToString();
    }

    private static string ReplaceOps(int records, int count)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < count; i++)
        {
            int r = i * records / count;
            string path = (i % 3) switch
            {
                0 => $"/items/{r}/name",
                1 => $"/items/{r}/meta/owner",
                _ => $"/items/{r}/tags/1",
            };
            sb.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "," : string.Empty)}{{\"op\":\"replace\",\"path\":\"{path}\",\"value\":\"patched {i}\"}}");
        }

        return sb.Append(']').ToString();
    }

    private static byte[] Apply(string yaml, string patch, bool splice)
    {
        DocState.NoSplice = !splice;
        try
        {
            using YamlDocument doc = YamlDocument.Parse(yaml);
            doc.ApplyPatch(patch);
            return doc.ToBytes();
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    [Fact]
    public void Replacing_values_parses_only_the_entries_around_each_one()
    {
        string yaml = Records(1200);
        string patch = ReplaceOps(1200, 100);
        SpliceCheck.ResetThread();
        byte[] spliced = Apply(yaml, patch, splice: true);
        long splicedOps = SpliceCheck.ThreadSpliced;
        long fullOps = SpliceCheck.ThreadFull;
        long bytes = SpliceCheck.ThreadBytes;

        Assert.Equal(100, splicedOps);
        Assert.Equal(0, fullOps);
        Assert.True(bytes < 100 * 300, $"{bytes} bytes parsed for 100 replaces in {yaml.Length} bytes");
        Assert.Equal(Apply(yaml, patch, splice: false), spliced);
    }

    [Fact]
    public void Appending_to_a_flow_sequence_parses_only_its_last_items()
    {
        string item = new('x', 80);
        var ops = new StringBuilder("[{\"op\":\"add\",\"path\":\"/f\",\"value\":[]}");
        for (int i = 0; i < 400; i++)
        {
            ops.Append(",{\"op\":\"copy\",\"from\":\"/v\",\"path\":\"/f/-\"}");
        }

        string patch = ops.Append(']').ToString();
        string yaml = "a: 1\nv: " + item + "\n";
        SpliceCheck.ResetThread();
        byte[] spliced = Apply(yaml, patch, splice: true);
        long fullOps = SpliceCheck.ThreadFull;
        long bytes = SpliceCheck.ThreadBytes;

        Assert.Equal(0, fullOps);
        Assert.True(bytes < 401 * 400, $"{bytes} bytes parsed for 400 appends");
        Assert.Equal(Apply(yaml, patch, splice: false), spliced);
    }

    [Fact]
    public void Adding_members_to_a_large_mapping_parses_only_the_new_entry()
    {
        var yaml = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"key{i}: value {i}\n");
        }

        var ops = new StringBuilder("[");
        for (int i = 0; i < 200; i++)
        {
            ops.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "," : string.Empty)}{{\"op\":\"add\",\"path\":\"/new{i}\",\"value\":{i}}}");
        }

        string patch = ops.Append(']').ToString();
        SpliceCheck.ResetThread();
        byte[] spliced = Apply(yaml.ToString(), patch, splice: true);
        long splicedOps = SpliceCheck.ThreadSpliced;
        long fullOps = SpliceCheck.ThreadFull;
        long bytes = SpliceCheck.ThreadBytes;

        Assert.Equal(200, splicedOps);
        Assert.Equal(0, fullOps);
        Assert.True(bytes < 200 * 100, $"{bytes} bytes parsed for 200 additions");
        Assert.Equal(Apply(yaml.ToString(), patch, splice: false), spliced);
    }

    [Fact]
    public void An_edit_beside_an_anchor_stays_incremental_and_one_that_replaces_it_parses_everything()
    {
        const string Yaml = "base: &b\n  x: 1\n  y: 2\nuse: *b\nother:\n  p: 1\n  q: 2\n";
        SpliceCheck.ResetThread();
        byte[] beside = Apply(Yaml, "[{\"op\":\"replace\",\"path\":\"/other/q\",\"value\":3}]", splice: true);
        Assert.Equal(1, SpliceCheck.ThreadSpliced);
        Assert.Equal(0, SpliceCheck.ThreadFull);
        Assert.Equal("base: &b\n  x: 1\n  y: 2\nuse: *b\nother:\n  p: 1\n  q: 3\n", Encoding.UTF8.GetString(beside));

        // The anchor is in the text that would be parsed again, and what it names may be used further on.
        const string Anchored = "a: &c\n  x: 1\nb: 2\n";
        const string Patch = "[{\"op\":\"replace\",\"path\":\"/a\",\"value\":9}]";
        SpliceCheck.ResetThread();
        byte[] inside = Apply(Anchored, Patch, splice: true);
        Assert.Equal(0, SpliceCheck.ThreadSpliced);
        Assert.Equal(1, SpliceCheck.ThreadFull);
        Assert.Equal(Apply(Anchored, Patch, splice: false), inside);
    }

    [Fact]
    public void A_stream_with_a_tag_directive_is_parsed_whole()
    {
        const string Yaml = "%TAG !e! tag:example.com,2000:\n---\na: !e!thing 1\nb:\n  c: 2\n";
        SpliceCheck.ResetThread();
        byte[] bytes = Apply(Yaml, "[{\"op\":\"replace\",\"path\":\"/b/c\",\"value\":3}]", splice: true);
        Assert.Equal(0, SpliceCheck.ThreadSpliced);
        Assert.Equal(1, SpliceCheck.ThreadFull);
        Assert.Equal("%TAG !e! tag:example.com,2000:\n---\na: !e!thing 1\nb:\n  c: 3\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void A_failing_operation_after_many_in_place_edits_leaves_the_document_and_its_next_edit_alone()
    {
        string yaml = Records(300);
        string patch = ReplaceOps(300, 50);
        patch = patch.TrimEnd(']') + ",{\"op\":\"replace\",\"path\":\"/items/9999/name\",\"value\":1}]";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        byte[] before = doc.ToBytes();
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(patch));
        Assert.Equal(50, ex.OperationIndex);
        Assert.Equal(before, doc.ToBytes());

        doc.Set("/items/3/name", "after");
        DocState.NoSplice = true;
        try
        {
            using YamlDocument reference = YamlDocument.Parse(yaml);
            reference.Set("/items/3/name", "after");
            Assert.Equal(reference.ToBytes(), doc.ToBytes());
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    [Theory]
    [InlineData("---\n---\na:\n  b: 1\n  c: 2\n", 1, "/a/b")]
    [InlineData("# only a comment\n---\n...\n---\nk: 1\nm:\n  n: 2\n", 1, "/m/n")]
    [InlineData("--- # c\n...\n---\nx:\n  y: 1\n--- \n---\nz: 3\n", 1, "/x/y")]
    [InlineData("a:\n  b: 1\n---\n---\nc: 2\n", 0, "/a/b")]
    public void Documents_without_nodes_keep_their_place_when_another_document_is_edited(string yaml, int document, string path)
    {
        byte[] expected;
        DocState.NoSplice = true;
        try
        {
            using YamlDocument reference = YamlDocument.Parse(yaml);
            reference.Set(path, "changed value", document);
            expected = reference.ToBytes();
        }
        finally
        {
            DocState.NoSplice = false;
        }

        using YamlDocument doc = YamlDocument.Parse(yaml);
        SpliceCheck.ResetThread();
        doc.Set(path, "changed value", document);
        Assert.Equal(1, SpliceCheck.ThreadSpliced);
        Assert.Equal(expected, doc.ToBytes());
    }

    [ThreadStatic]
    private static int s_thrown;

    [Theory]
    [InlineData("a: 1\nnested:\n  x: 1\n  y:\n    z: 2\n", "/b")]
    [InlineData("a: 1\nlist:\n- p\n- q\n", "/b")]
    [InlineData("a: 1\nlist:\n  - p\n  - q\n", "/b")]
    [InlineData("top:\n  nested:\n    x: 1\n", "/top/b")]
    public void A_key_added_after_a_nested_collection_is_placed_without_a_parse_that_fails(string yaml, string path)
    {
        static void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => s_thrown++;
        using YamlDocument doc = YamlDocument.Parse(yaml);
        s_thrown = 0;
        SpliceCheck.ResetThread();
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            doc.Set(path, 3);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        Assert.Equal(0, s_thrown);
        Assert.Equal(1, SpliceCheck.ThreadSpliced);
    }

    [Fact]
    public void An_edit_that_nests_deeper_than_the_limit_allows_is_refused_as_a_full_parse_would()
    {
        string yaml = "a:\n  b:\n    c: 1\n";
        var limits = new YamlLimits { MaxDepth = 3 };
        var options = new YamlDocumentOptions { Limits = limits };
        using YamlDocument doc = YamlDocument.Parse(yaml, options);
        byte[] before = doc.ToBytes();
        YamlException ex = Assert.Throws<YamlException>(() => doc.Set("/a/b/d", new Dictionary<string, object?> { ["e"] = 1 }));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(before, doc.ToBytes());
    }

    // ---- Random documents and patches: the result must equal what parsing the whole text per edit gives.

    private static string Key(Random r, int i) => (r.Next(6)) switch
    {
        0 => $"\"key {i}\"",
        1 => $"'k{i}'",
        _ => $"k{i}",
    };

    private static void Scalar(StringBuilder sb, Random r)
    {
        switch (r.Next(9))
        {
            case 0: sb.Append(CultureInfo.InvariantCulture, $"{r.Next(1000)}"); break;
            case 1: sb.Append("true"); break;
            case 2: sb.Append("null"); break;
            case 3: sb.Append("'single quoted'"); break;
            case 4: sb.Append("\"double \\\"quoted\\\"\""); break;
            case 5: sb.Append(CultureInfo.InvariantCulture, $"{r.Next(100)}.5"); break;
            case 6: sb.Append("two words here"); break;
            default: sb.Append(CultureInfo.InvariantCulture, $"v{r.Next(100)}"); break;
        }

        if (r.Next(8) == 0)
        {
            sb.Append("  # note");
        }
    }

    private static void Flow(StringBuilder sb, Random r, int depth)
    {
        if (r.Next(2) == 0)
        {
            sb.Append('[');
            int n = r.Next(4);
            for (int i = 0; i < n; i++)
            {
                sb.Append(i > 0 ? ", " : string.Empty);
                if (depth < 2 && r.Next(4) == 0)
                {
                    Flow(sb, r, depth + 1);
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $"f{r.Next(50)}");
                }
            }

            sb.Append(']');
        }
        else
        {
            sb.Append('{');
            int n = r.Next(4);
            for (int i = 0; i < n; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}m{i}: ");
                if (depth < 2 && r.Next(4) == 0)
                {
                    Flow(sb, r, depth + 1);
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{r.Next(50)}");
                }
            }

            sb.Append('}');
        }
    }

    private static void Map(StringBuilder sb, Random r, int indent, int depth, ref int anchors)
    {
        string pad = new(' ', indent);
        int n = 1 + r.Next(5);
        for (int i = 0; i < n; i++)
        {
            if (r.Next(6) == 0)
            {
                sb.Append(pad).Append("# between entries\n");
            }

            if (r.Next(10) == 0)
            {
                sb.Append('\n');
            }

            sb.Append(pad);
            int kind = r.Next(depth < 3 ? 10 : 5);
            switch (kind)
            {
                case 0:
                case 1:
                case 2:
                    sb.Append(Key(r, i)).Append(": ");
                    if (anchors > 0 && r.Next(6) == 0)
                    {
                        sb.Append("*a").Append(r.Next(anchors));
                    }
                    else
                    {
                        if (r.Next(12) == 0)
                        {
                            sb.Append("&a").Append(anchors++).Append(' ');
                        }

                        Scalar(sb, r);
                    }

                    sb.Append('\n');
                    break;
                case 3:
                    sb.Append(Key(r, i)).Append(": ");
                    Flow(sb, r, 0);
                    sb.Append('\n');
                    break;
                case 4:
                    sb.Append(Key(r, i)).Append(": |\n").Append(pad).Append("  literal line one\n").Append(pad).Append("  line two\n");
                    break;
                case 5:
                case 6:
                    sb.Append(Key(r, i)).Append(":\n");
                    Map(sb, r, indent + 2, depth + 1, ref anchors);
                    break;
                case 7:
                    sb.Append(Key(r, i)).Append(":\n");
                    Seq(sb, r, r.Next(2) == 0 ? indent : indent + 2, depth + 1, ref anchors);
                    break;
                case 8:
                    sb.Append("? explicit").Append(i).Append('\n').Append(pad).Append(": ");
                    Scalar(sb, r);
                    sb.Append('\n');
                    break;
                default:
                    sb.Append(Key(r, i)).Append(":\n");
                    break;
            }
        }
    }

    // The text of a collection that continues the line of a dash: its first entry sits right after the dash.
    private static string StartAtFirstEntry(string text, int indent)
    {
        int at = 0;
        while (at < text.Length)
        {
            int eol = text.IndexOf('\n', at);
            string line = text.Substring(at, eol - at).TrimStart();
            if (line.Length > 0 && line[0] != '#')
            {
                break;
            }

            at = eol + 1;
        }

        return text.Substring(at + indent);
    }

    private static void Seq(StringBuilder sb, Random r, int indent, int depth, ref int anchors)
    {
        string pad = new(' ', indent);
        int n = 1 + r.Next(5);
        for (int i = 0; i < n; i++)
        {
            sb.Append(pad).Append("- ");
            switch (r.Next(depth < 3 ? 6 : 3))
            {
                case 0:
                case 1:
                    Scalar(sb, r);
                    sb.Append('\n');
                    break;
                case 2:
                    Flow(sb, r, 0);
                    sb.Append('\n');
                    break;
                case 3:
                    {
                        // A mapping that starts on the dash's line.
                        var inner = new StringBuilder();
                        Map(inner, r, indent + 2, depth + 1, ref anchors);
                        sb.Append(StartAtFirstEntry(inner.ToString(), indent + 2));
                        break;
                    }

                case 4:
                    {
                        var inner = new StringBuilder();
                        Seq(inner, r, indent + 2, depth + 1, ref anchors);
                        sb.Append(StartAtFirstEntry(inner.ToString(), indent + 2));
                        break;
                    }

                default:
                    sb.Append("# empty item follows\n");
                    break;
            }
        }
    }

    internal static string RandomDocument(Random r)
    {
        var sb = new StringBuilder();
        int docs = r.Next(5) == 0 ? 2 : 1;
        for (int d = 0; d < docs; d++)
        {
            int anchors = 0;
            if (d > 0 || r.Next(4) == 0)
            {
                sb.Append("---\n");
            }

            if (r.Next(5) == 0)
            {
                sb.Append("# leading comment\n");
            }

            if (r.Next(4) == 0)
            {
                Seq(sb, r, 0, 0, ref anchors);
            }
            else
            {
                Map(sb, r, 0, 0, ref anchors);
            }
        }

        return sb.ToString();
    }

    private static void Collect(YamlNode node, string path, List<string> into, int depth)
    {
        into.Add(path);
        if (depth > 12)
        {
            return;
        }

        if (node.Kind == YamlNodeKind.Mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> e in node.Entries)
            {
                if (e.Key.Kind == YamlNodeKind.Scalar && e.Key.TryGetValue(out string? k) && k is not null)
                {
                    Collect(e.Value, path + "/" + k.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), into, depth + 1);
                }
            }
        }
        else if (node.Kind == YamlNodeKind.Sequence)
        {
            int i = 0;
            foreach (YamlNode item in node.Items)
            {
                Collect(item, path + "/" + i++, into, depth + 1);
            }
        }
    }

    private static readonly string[] Values =
    [
        "\"x\"", "\"two words\"", "42", "true", "null", "1.5", "\"\"", "{\"k\":1,\"n\":\"v\"}", "[1,\"two\"]", "[]", "{}", "[[1],{\"a\":[]}]",
        "\"multi\\nline\\n\"", "\"a: b\"", "\"# c\"", "{\"deep\":{\"er\":{\"est\":[1,2,3]}}}",
    ];

    // Builds a patch of up to maxOps operations that are valid one after the other, by trying each on a scratch
    // copy that is parsed whole after every edit.
    private static string RandomPatch(Random r, string yaml, int maxOps)
    {
        DocState.NoSplice = true;
        try
        {
            using YamlDocument scratch = YamlDocument.Parse(yaml);
            var ops = new StringBuilder("[");
            int made = 0;
            for (int attempt = 0; attempt < maxOps * 3 && made < maxOps; attempt++)
            {
                var paths = new List<string>();
                Collect(scratch.Documents[0], string.Empty, paths, 0);
                string path = paths[r.Next(paths.Count)];
                string op = r.Next(7) switch { 0 => "remove", 1 or 2 => "add", _ => "replace" };
                if (op == "add")
                {
                    path += r.Next(3) == 0 ? "/-" : "/n" + r.Next(4);
                }

                if (op == "remove" && path.Length == 0)
                {
                    continue;
                }

                string value = Values[r.Next(Values.Length)];
                string one = op == "remove"
                    ? $"{{\"op\":\"remove\",\"path\":\"{path}\"}}"
                    : $"{{\"op\":\"{op}\",\"path\":\"{path}\",\"value\":{value}}}";
                try
                {
                    scratch.ApplyPatch("[" + one + "]");
                }
                catch (YamlException)
                {
                    continue;
                }

                ops.Append(made++ > 0 ? "," : string.Empty).Append(one);
            }

            return ops.Append(']').ToString();
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 40).Select(i => new object[] { i });

    public static IEnumerable<object[]> ManySeeds() => Enumerable.Range(100, 400).Select(i => new object[] { i });

    private static void RunRandom(int seed, int maxOps)
    {
        var r = new Random(seed);
        string yaml = RandomDocument(r);
        YamlDocument parsed;
        try
        {
            parsed = YamlDocument.Parse(yaml);
        }
        catch (YamlException ex)
        {
            Assert.Fail($"the generator made a document that does not parse: {ex.Message}\n{yaml}");
            return;
        }

        parsed.Dispose();
        string patch = RandomPatch(r, yaml, maxOps);
        byte[] expected = Apply(yaml, patch, splice: false);
        byte[] actual = Apply(yaml, patch, splice: true);
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"seed {seed}: bytes differ for {patch} on\n{yaml}\n--- expected\n{Encoding.UTF8.GetString(expected)}\n--- actual\n{Encoding.UTF8.GetString(actual)}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_patches_give_the_bytes_that_parsing_the_whole_text_per_edit_gives(int seed) => RunRandom(seed, 12);

    [Trait("Category", "Slow")]
    [Theory]
    [MemberData(nameof(ManySeeds))]
    public void Many_random_patches_give_the_bytes_that_parsing_the_whole_text_per_edit_gives(int seed) => RunRandom(seed, 30);
}
