using System.Globalization;
using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Regressions of the edit engine: comments next to values, flow removals, budgets and pointer writes on empty streams.</summary>
public class DocumentRepairTests
{
    // The text after an edit, which must read back as a stream with the value the test expects.
    private static string Edit(string yaml, Action<YamlDocument> edit, string expectedValue)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        edit(doc);
        string result = doc.ToString();
        Assert.Equal(expectedValue, Canon(YamlSerializer.Deserialize<object>(result)));
        return result;
    }

    private static string Canon(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\n", "\\n") + "\"",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Dictionary<string, object?> m => "{" + string.Join(",", m.Select(p => p.Key + ":" + Canon(p.Value))) + "}",
        List<object?> l => "[" + string.Join(",", l.Select(Canon)) + "]",
        _ => value.ToString() ?? "?",
    };

    // ---- A comment between a key and its value ----

    [Fact]
    public void A_tag_that_no_longer_fits_is_removed_when_a_comment_follows_it()
    {
        Assert.Equal("a: # c\n  5\nc: 2\n", Edit("a: !custom # c\n  b: 1\nc: 2\n", d => d.Set("/a", 5), "{a:5,c:2}"));
    }

    [Fact]
    public void A_map_tag_is_removed_when_a_sequence_replaces_the_mapping_under_a_comment()
    {
        Assert.Equal("a: # c\n  - 1\nc: 2\n", Edit("a: !!map # c\n  b: 1\nc: 2\n", d => d.Set("/a", new List<object?> { 1 }), "{a:[1],c:2}"));
    }

    [Fact]
    public void A_seq_tag_is_removed_when_a_mapping_replaces_the_sequence_under_a_comment()
    {
        Assert.Equal("a: # c\n  z: 1\nc: 2\n", Edit("a: !!seq # c\n  - 1\nc: 2\n", d => d.Set("/a", new Dictionary<string, object?> { ["z"] = 1 }), "{a:{z:1},c:2}"));
    }

    [Fact]
    public void A_tag_on_the_value_line_is_removed_when_it_no_longer_fits()
    {
        Assert.Equal("a: # c\n  5\nc: 2\n", Edit("a: # c\n  !custom b: 1\nc: 2\n", d => d.Set("/a", 5), "{a:5,c:2}"));
    }

    [Fact]
    public void A_tag_that_still_fits_stays_next_to_the_comment()
    {
        Assert.Equal("a: !!map # c\n  z: 1\nc: 2\n", Edit("a: !!map # c\n  b: 1\nc: 2\n", d => d.Set("/a", new Dictionary<string, object?> { ["z"] = 1 }), "{a:{z:1},c:2}"));
    }

    [Fact]
    public void An_anchor_stays_when_the_tag_beside_it_is_dropped()
    {
        Assert.Equal("a: &x # c\n  5\nc: 2\n", Edit("a: &x !custom # c\n  b: 1\nc: 2\n", d => d.Set("/a", 5), "{a:5,c:2}"));
    }

    [Fact]
    public void A_comment_after_the_anchor_is_kept()
    {
        Assert.Equal("a: &x # why\n  true\n", Edit("a: &x # why\n  b: 1\n", d => d.Set("/a", true), "{a:true}"));
    }

    [Fact]
    public void An_anchor_on_the_value_line_moves_above_a_collection_that_replaces_the_value()
    {
        Assert.Equal("a: # why\n  &x\n  z: 1\n", Edit("a: # why\n  &x 5\n", d => d.Set("/a", new Dictionary<string, object?> { ["z"] = 1 }), "{a:{z:1}}"));
    }

    [Fact]
    public void A_zero_indented_sequence_replaced_by_a_mapping_is_indented_under_its_key()
    {
        Assert.Equal(
            "steps: # build steps\n  run: all\nname: ci\n",
            Edit("steps: # build steps\n- run: make\n- run: test\nname: ci\n", d => d.Set("/steps", new Dictionary<string, object?> { ["run"] = "all" }), "{steps:{run:\"all\"},name:\"ci\"}"));
    }

    [Fact]
    public void A_zero_indented_sequence_replaced_by_a_string_is_indented_under_its_key()
    {
        Assert.Equal(
            "steps: # build steps\n  none\nname: ci\n",
            Edit("steps: # build steps\n- run: make\n- run: test\nname: ci\n", d => d.Set("/steps", "none"), "{steps:\"none\",name:\"ci\"}"));
    }

    [Fact]
    public void A_zero_indented_sequence_replaced_by_a_multi_line_string_reads_back()
    {
        Edit("steps: # build steps\n- run: make\nname: ci\n", d => d.Set("/steps", "one\ntwo"), "{steps:\"one\\ntwo\",name:\"ci\"}");
    }

    [Fact]
    public void A_zero_indented_sequence_replaced_by_a_sequence_keeps_its_column()
    {
        Assert.Equal(
            "steps: # build steps\n- x\nname: ci\n",
            Edit("steps: # build steps\n- run: make\nname: ci\n", d => d.Set("/steps", new List<object?> { "x" }), "{steps:[\"x\"],name:\"ci\"}"));
    }

    // ---- Removing items of a flow collection laid out with the commas first ----

    [Fact]
    public void Removing_the_first_item_of_a_comma_first_flow_sequence_keeps_it_readable()
    {
        Assert.Equal("[\n b\n ]\n", Edit("[\n a\n , b\n ]\n", d => d.Remove("/0"), "[\"b\"]"));
    }

    [Fact]
    public void Removing_a_middle_item_of_a_comma_first_flow_sequence_keeps_the_commas()
    {
        Assert.Equal("[\n a\n , c\n ]\n", Edit("[\n a\n , b\n , c\n ]\n", d => d.Remove("/1"), "[\"a\",\"c\"]"));
    }

    [Fact]
    public void Removing_the_last_item_of_a_comma_first_flow_sequence_keeps_it_readable()
    {
        Assert.Equal("[\n a\n , b\n ]\n", Edit("[\n a\n , b\n , c\n ]\n", d => d.Remove("/2"), "[\"a\",\"b\"]"));
    }

    [Fact]
    public void Removing_the_first_flow_item_on_its_own_line_takes_the_comment_above_it_and_beside_it()
    {
        Assert.Equal("s: [\n  b # two\n  ]\n", Edit("s: [\n  # about a\n  a, # one\n  b # two\n  ]\n", d => d.Remove("/s/0"), "{s:[\"b\"]}"));
    }

    // ---- A set that replaces a scalar with a collection holding text ----

    [Fact]
    public void A_keep_literal_replaced_by_a_sequence_of_a_kept_string_stays_a_keep_literal()
    {
        Assert.Equal(
            "- a\n- - |+\n    tail\n\n- z\n",
            Edit("- a\n- |+\n  b\n\n- z\n", d => d.Set("/1", new List<object?> { "tail\n\n" }), "[\"a\",[\"tail\\n\\n\"],\"z\"]"));
    }

    // ---- Moves and copies into the anchored node an alias in the value names ----

    [Fact]
    public void A_moved_alias_value_that_names_the_destination_container_is_written_out()
    {
        const string yaml = "c: &c [x, y]\nd: &d [*c, 1]\ne: [*d]\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyPatch("[{\"op\":\"copy\",\"from\":\"/e/0\",\"path\":\"/c/1\"}]");
        string result = doc.ToString();
        Assert.Equal("{c:[\"x\",[[\"x\",\"y\"],1],\"y\"],d:[[\"x\",[[\"x\",\"y\"],1],\"y\"],1],e:[[[\"x\",[[\"x\",\"y\"],1],\"y\"],1]]}", Canon(YamlSerializer.Deserialize<object>(result)));
    }

    // ---- Binding a node with options that have no resolver yet ----

    private sealed class Config
    {
        public int Replicas { get; set; }

        public string? Script { get; set; }
    }

    [Fact]
    public void A_node_binds_with_options_that_have_not_been_used()
    {
        using YamlDocument doc = YamlDocument.Parse("replicas: 2\nscript: x\n");
        Config c = doc.Root.Deserialize<Config>(new YamlSerializerOptions())!;
        Assert.Equal(2, c.Replicas);
        Assert.Equal("x", c.Script);
    }

    // ---- Walking aliases through views ----

    [Fact]
    public void Reading_small_values_through_an_alias_many_times_never_reaches_the_limit()
    {
        using YamlDocument doc = YamlDocument.Parse("base: &b {x: 1, y: 2, z: 3}\nsvc: *b\n");
        YamlNode root = doc.Root;
        long sum = 0;
        for (int i = 0; i < 400_000; i++)
        {
            sum += root["svc"]["x"].GetValue<long>();
        }

        Assert.Equal(400_000, sum);
    }

    [Fact]
    public void Walking_an_alias_to_a_large_scalar_over_and_over_is_refused()
    {
        var text = new StringBuilder("a: &a ").Append('x', 100_000).Append("\nb:\n");
        for (int i = 0; i < 10_000; i++)
        {
            text.Append("  - *a\n");
        }

        using YamlDocument doc = YamlDocument.Parse(text.ToString());
        YamlException ex = Assert.Throws<YamlException>(() =>
        {
            foreach (YamlNode item in doc.Root["b"].Items)
            {
                _ = item.Target.GetValue<string>();
            }
        });
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Walking_nested_aliases_to_the_end_is_refused()
    {
        var text = new StringBuilder("a: &a [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n");
        string previous = "a";
        foreach (char name in "bcdefghij")
        {
            text.Append(name).Append(": &").Append(name).Append(" [").Append(string.Join(", ", Enumerable.Repeat("*" + previous, 10))).Append("]\n");
            previous = name.ToString();
        }

        using YamlDocument doc = YamlDocument.Parse(text.ToString());
        long visited = 0;
        YamlException ex = Assert.Throws<YamlException>(() => Visit(doc.Root["j"], ref visited));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.True(visited < 5_000_000, "the walk was not stopped in time");

        static void Visit(YamlNode node, ref long visited)
        {
            visited++;
            YamlNode target = node.Target;
            if (target.Kind != YamlNodeKind.Sequence)
            {
                return;
            }

            foreach (YamlNode child in target.Items)
            {
                Visit(child, ref visited);
            }
        }
    }

    // ---- Pointer writes on a stream with no content ----

    [Fact]
    public void Set_by_pointer_on_an_empty_stream_creates_the_document()
    {
        using YamlDocument doc = YamlDocument.Parse("");
        doc.Set("/a", 1);
        Assert.Equal("a: 1\n", doc.ToString());
        Assert.Equal(1, doc.Get<long>("/a"));
    }

    [Fact]
    public void Add_by_pointer_on_a_stream_of_comments_keeps_the_comments()
    {
        using YamlDocument doc = YamlDocument.Parse("# only a comment\n");
        doc.Add("/a", "x");
        Assert.StartsWith("# only a comment\n", doc.ToString());
        Assert.Equal("x", doc.Get<string>("/a"));
    }

    [Fact]
    public void Set_by_pointer_below_a_missing_parent_on_an_empty_stream_is_still_refused()
    {
        using YamlDocument doc = YamlDocument.Parse("");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Set("/a/b", 1));
        Assert.Equal(YamlErrorCategory.PatchPath, ex.Category);
        Assert.Equal("", doc.ToString());
    }

    // ---- Binding a node to object ----

    [Fact]
    public void A_node_bound_to_object_shares_the_value_of_an_anchor_between_its_aliases()
    {
        const string yaml = "a: &x {k: 1}\nb: *x\nc: [*x, *x]\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        var fromNode = (Dictionary<string, object?>)doc.Root.Deserialize<object>()!;
        var fromText = (Dictionary<string, object?>)YamlSerializer.Deserialize<object>(yaml)!;
        Assert.Equal(Canon(fromText), Canon(fromNode));
        Assert.Same(fromNode["a"], fromNode["b"]);
        Assert.Same(fromNode["a"], ((List<object?>)fromNode["c"]!)[1]);
        Assert.Same(fromText["a"], fromText["b"]);
    }

    [Fact]
    public void A_node_bound_to_object_reads_an_alias_used_as_a_key_like_the_text_does()
    {
        const string yaml = "- &k name\n- {*k : 1, other: *k}\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(Canon(YamlSerializer.Deserialize<object>(yaml)), Canon(doc.Root.Deserialize<object>()));
    }

    [Fact]
    public void A_node_bound_to_object_with_an_anchor_outside_it_reads_the_value_the_alias_stands_for()
    {
        const string yaml = "base: &b {x: 1}\nsvc: {u: *b, v: *b}\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal("{u:{x:1},v:{x:1}}", Canon(doc.Root["svc"].Deserialize<object>()));
    }

    [Fact]
    public void Many_small_binds_in_one_document_work_out_the_alias_sizes_once()
    {
        var text = new StringBuilder("base: &b {x: 1}\n");
        for (int i = 0; i < 200; i++)
        {
            text.Append("svc").Append(i).Append(": {a: *b}\n");
        }

        using YamlDocument doc = YamlDocument.Parse(text.ToString());
        int before = OneYaml.Documents.NodeReplay.SizePassesOnThisThread;
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal("{a:{x:1}}", Canon(doc.Root["svc" + i].Deserialize<object>()));
        }

        Assert.Equal(1, OneYaml.Documents.NodeReplay.SizePassesOnThisThread - before);
    }
}
