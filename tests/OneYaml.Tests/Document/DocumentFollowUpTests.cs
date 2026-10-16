using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Tag classification without building tags, opt-in quoting of YAML 1.1 lookalikes, the step of a new
/// sequence, comments that stay put when a value is replaced, empty collections that take members, and the
/// block-scalar reach of a splice.
/// </summary>
public class DocumentFollowUpTests
{
    private static string Edit(string yaml, Action<YamlDocument> edit, YamlDocumentOptions? options = null)
    {
        byte[] spliced = Run(yaml, edit, splice: true, options);
        Assert.Equal(Run(yaml, edit, splice: false, options), spliced);
        string result = Encoding.UTF8.GetString(spliced);

        // The text must read as the same document a fresh parse of it gives, and be stable under another parse.
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(result, again.ToString());
        return result;
    }

    private static byte[] Run(string yaml, Action<YamlDocument> edit, bool splice, YamlDocumentOptions? options)
    {
        DocState.NoSplice = !splice;
        try
        {
            using YamlDocument doc = YamlDocument.Parse(yaml, options);
            edit(doc);
            return doc.ToBytes();
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    private static Dictionary<string, object?> Map(string key, object? value) => new() { [key] = value };

    private static List<object?> Items(params object?[] items) => [.. items];

    // ---- Tags are classified without building them ----

    private static string TaggedStream(int prefixLength, int nodes)
    {
        var sb = new StringBuilder();
        sb.Append("%TAG !e! tag:x,2000:").Append('a', prefixLength).Append("\n---\n");
        for (int i = 0; i < nodes; i++)
        {
            sb.Append("- !e!x").Append(i % 10).Append(" a\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void A_long_tag_prefix_used_by_many_nodes_costs_nothing_per_node()
    {
        // Each node has its own suffix, so no string is shared between them.
        string text = TaggedStream(prefixLength: 50_000, nodes: 600);
        YamlDocumentOptions options = new() { Limits = YamlLimits.Untrusted };
        for (int warm = 0; warm < 2; warm++)
        {
            using YamlDocument w = YamlDocument.Parse(text, options);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using YamlDocument doc = YamlDocument.Parse(text, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Building the 50 KB tag for every node would take tens of megabytes; the text itself is 50 KB.
        Assert.True(allocated < 2_000_000, $"allocated {allocated} bytes");
        Assert.Equal(600, doc.Root.Count);
        Assert.StartsWith("!e!x0", doc.Root[0].RawTag, StringComparison.Ordinal);
    }

    // ---- Opt-in quoting of YAML 1.1 lookalikes ----

    [Theory]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("y")]
    [InlineData("No")]
    [InlineData("2026-01-01")]
    public void A_yaml_1_1_lookalike_stays_plain_by_default(string value)
    {
        Assert.Equal($"k: {value}\nplain: also\n", Edit("k: x\nplain: also\n", d => d.Set("/k", value)));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("y")]
    [InlineData("No")]
    [InlineData("2026-01-01")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    public void A_yaml_1_1_lookalike_is_quoted_when_asked(string value)
    {
        var options = new YamlDocumentOptions { QuoteYaml11Lookalikes = true };
        string result = Edit("k: x\nplain: also\n", d =>
        {
            d.Set("/k", value);
            d.Set("/plain", "ok");
        }, options);
        Assert.Equal($"k: '{value}'\nplain: ok\n", result);
        using YamlDocument back = YamlDocument.Parse(result);
        Assert.Equal(value, back.Get<string>("/k"));
    }

    [Fact]
    public void Quoting_applies_to_new_members_items_flow_values_and_keys()
    {
        var options = new YamlDocumentOptions { QuoteYaml11Lookalikes = true };
        Assert.Equal("a: 1\n'yes': 'no'\n", Edit("a: 1\n", d => d.Set("/yes", "no"), options));
        Assert.Equal("- a\n- 'off'\n", Edit("- a\n", d => d.Add("/-", "off"), options));
        Assert.Equal("a: {k: 'on'}\n", Edit("a: {}\n", d => d.Set("/a/k", "on"), options));
        Assert.Equal("a:\n  b: 'y'\n  c:\n    - 1\n    - 'n'\n", Edit("a: 1\n", d => d.Set("/a", Map("b", "y").Also(m => m["c"] = Items(1, "n"))), options));
    }

    [Fact]
    public void A_plain_scalar_that_reads_back_wrongly_is_not_kept_plain_when_asked()
    {
        var options = new YamlDocumentOptions { QuoteYaml11Lookalikes = true };
        Assert.Equal("flag: 'off'\n", Edit("flag: maybe\n", d => d.Set("/flag", "off"), options));
        Assert.Equal("flag: off\n", Edit("flag: maybe\n", d => d.Set("/flag", "off")));
    }

    [Fact]
    public void The_quoting_choice_survives_later_edits_and_patches()
    {
        var options = new YamlDocumentOptions { QuoteYaml11Lookalikes = true };
        string result = Edit("a: 1\n", d =>
        {
            d.Set("/b", "x");
            d.ApplyPatch("""[{"op":"add","path":"/c","value":"yes"}]""");
            d.ApplyMergePatch("""{"d":"no"}""");
        }, options);
        Assert.Equal("a: 1\nb: x\nc: 'yes'\nd: 'no'\n", result);
    }

    // ---- A new sequence copies the step of the mapping it joins ----

    [Fact]
    public void A_sequence_added_to_a_mapping_uses_that_mappings_step_not_an_earlier_ones()
    {
        string yaml = "x:\n    y: 1\nz:\n  w: 1\n";
        Assert.Equal("x:\n    y: 1\nz:\n  w: 1\n  n:\n    - 1\n", Edit(yaml, d => d.Add("/z/n", Items(1))));
        Assert.Equal("x:\n    y: 1\nz:\n  w: 1\n  n:\n    - 1\n", Edit(yaml, d => d.ApplyPatch("""[{"op":"add","path":"/z/n","value":[1]}]""")));
    }

    // ---- A comment stays on its line when a value is replaced ----

    [Theory]
    [InlineData("--- !!seq # c\n", """{"a":1}""", "--- # c\na: 1\n")]
    [InlineData("--- !!seq\n", """{"a":1}""", "---\na: 1\n")]
    [InlineData("--- !!seq # c\n- 1\n", """{"a":1}""", "--- # c\na: 1\n")]
    [InlineData("--- !!seq # c\n- 1\n", "\"x\"", "--- x # c\n")]
    [InlineData("--- !!seq # c\n", "{}", "--- {} # c\n")]
    [InlineData("--- !!seq # c\n", "5", "--- 5 # c\n")]
    [InlineData("--- !!int # c\n", "5", "--- !!int 5 # c\n")]
    [InlineData("--- # c\n", "{}", "--- {} # c\n")]
    [InlineData("--- !!seq # c\n...\n", "{}", "--- {} # c\n...\n")]
    [InlineData("--- !!seq # c\n---\nb\n", "5", "--- 5 # c\n---\nb\n")]
    [InlineData("--- &a !!seq # c\n- 1\n", """{"a":1}""", "--- &a # c\na: 1\n")]
    public void A_merge_patch_on_a_document_marker_keeps_the_comment_on_the_marker_line(string yaml, string patch, string expected)
    {
        Assert.Equal(expected, Edit(yaml, d => d.ApplyMergePatch(patch)));
    }

    [Theory]
    [InlineData("a: x # c\nb: 1\n", "a: # c\n  k: 1\nb: 1\n")]
    [InlineData("a: \"x\" # c\n", "a: # c\n  k: 1\n")]
    [InlineData("a: |- # c\n  x\nb: 1\n", "a: # c\n  k: 1\nb: 1\n")]
    [InlineData("a: >\n  x\nb: 1\n", "a:\n  k: 1\nb: 1\n")]
    [InlineData("a: !!str x # c\nb: 1\n", "a: # c\n  k: 1\nb: 1\n")]
    [InlineData("a: &x x # c\nb: 1\n", "a: &x # c\n  k: 1\nb: 1\n")]
    [InlineData("a: # c\n  x\nb: 1\n", "a: # c\n  k: 1\nb: 1\n")]
    public void A_comment_on_the_line_of_a_key_stays_there_when_the_value_becomes_a_block_collection(string yaml, string expected)
    {
        Assert.Equal(expected, Edit(yaml, d => d.Set("/a", Map("k", 1))));
    }

    [Fact]
    public void A_comment_after_a_scalar_stays_with_the_key_when_the_value_becomes_a_list()
    {
        Assert.Equal("a: # c\n  - 1\n  - 2\nb: 1\n", Edit("a: x # c\nb: 1\n", d => d.Set("/a", Items(1, 2))));
    }

    // ---- An empty node tagged as a collection takes members ----

    [Theory]
    [InlineData("--- !!map\n", "a", "--- !!map\na: 1\n")]
    [InlineData("--- !!map # c\n", "a", "--- !!map # c\na: 1\n")]
    [InlineData("--- &r !!map\n", "a", "--- &r !!map\na: 1\n")]
    [InlineData("k: !!map\n", "k/a", "k: !!map\n  a: 1\n")]
    public void An_empty_map_node_takes_a_member_by_add(string yaml, string path, string expected)
    {
        Assert.Equal(expected, Edit(yaml, d => d.ApplyPatch($$"""[{"op":"add","path":"/{{path}}","value":1}]""")));
        Assert.Equal(expected, Edit(yaml, d => d.Add("/" + path, 1)));
        Assert.Equal(expected, Edit(yaml, d => d.Set("/" + path, 1)));
    }

    [Theory]
    [InlineData("--- !!seq\n", "0", "--- !!seq\n- 1\n")]
    [InlineData("--- !!seq\n", "-", "--- !!seq\n- 1\n")]
    [InlineData("k: !!seq\n", "k/-", "k: !!seq\n  - 1\n")]
    public void An_empty_seq_node_takes_an_item_by_add(string yaml, string path, string expected)
    {
        Assert.Equal(expected, Edit(yaml, d => d.ApplyPatch($$"""[{"op":"add","path":"/{{path}}","value":1}]""")));
    }

    [Fact]
    public void An_empty_collection_node_keeps_the_refusals_that_still_apply()
    {
        YamlException e = Assert.Throws<YamlException>(() => Edit("--- !!seq\n", d => d.Add("/1", 1)));
        Assert.Equal(YamlErrorCategory.PatchPath, e.Category);

        // replace needs a target, an untagged empty value is a null, and a tagged string has no members.
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => Edit("--- !!map\n", d => d.ApplyPatch("""[{"op":"replace","path":"/a","value":1}]"""))).Category);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => Edit("k:\n", d => d.ApplyPatch("""[{"op":"add","path":"/k/a","value":1}]"""))).Category);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => Edit("--- !!str\n", d => d.ApplyPatch("""[{"op":"add","path":"/a","value":1}]"""))).Category);
    }

    [Fact]
    public void A_failed_patch_on_an_empty_collection_node_leaves_the_text_alone()
    {
        using YamlDocument doc = YamlDocument.Parse("--- !!map\n");
        Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"add","path":"/a","value":1},{"op":"test","path":"/a","value":2}]"""));
        Assert.Equal("--- !!map\n", doc.ToString());
    }

    // ---- An empty stream takes the first member through pointers, as the indexer does ----

    [Fact]
    public void Pointer_methods_create_the_document_in_an_empty_stream()
    {
        Assert.Equal("a: 1\n", Edit("", d => d.Set("/a", 1)));
        Assert.Equal("a: 1\n", Edit("", d => d.Add("/a", 1)));
        Assert.Equal("a: 1\n", Edit("", d => d.Root["a"] = 1));
        Assert.Equal("- 1\n", Edit("", d => d.Add("/-", 1)));
    }

    // ---- A splice that leaves a block scalar last reads the lines after it ----

    [Theory]
    [InlineData("a: |+\n  x\n\nb: 1\n  # c\n\n", "a: |+\n  x\n\n\n")]
    [InlineData("a: |+\n  x\n\nb: 1\n    # deep\n  \n", "a: |+\n  x\n\n  \n")]
    [InlineData("a: |+\n  x\n\nd: [1]\n  # c\n\n", "a: |+\n  x\n\n\n")]
    public void Removing_the_entry_after_a_kept_block_scalar_leaves_it_the_blank_lines_that_followed_the_entry(string yaml, string expected)
    {
        // The edit takes the entry and the comment lines that belong to it. The kept scalar then ends the mapping and
        // owns the blank lines after them, which the region of the edit has to include to read it the way a whole parse does.
        string key = yaml.Contains("d:", StringComparison.Ordinal) ? "d" : "b";
        Assert.Equal(expected, Edit(yaml, d => d.Remove("/" + key)));
    }
}

internal static class DictionaryExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
