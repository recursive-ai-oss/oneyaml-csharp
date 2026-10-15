using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Patches: the six operations, atomicity, the builder, the JSON forms, merge patches and duplicate keys.</summary>
public class PatchTests
{
    private static string Patched(string yaml, string patch, int document = 0)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyPatch(patch, document);
        return doc.ToString();
    }

    private static string Merged(string yaml, string patch, int document = 0)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyMergePatch(patch, document);
        return doc.ToString();
    }

    private static YamlException Fails(string yaml, string patch, YamlErrorCategory category, int? index = null)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        var ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(patch));
        Assert.Equal(category, ex.Category);
        if (index is not null)
        {
            Assert.Equal(index, ex.OperationIndex);
        }

        Assert.Equal(yaml, doc.ToString());
        return ex;
    }

    // ---- operations ----

    [Fact]
    public void Each_operation_does_its_work_and_keeps_the_rest_of_the_text()
    {
        const string yaml = "# head\n\na: 1   # one\nb:\n  - x\n  - y\nc: {d: 4}\n";
        Assert.Equal("# head\n\na: 1   # one\nb:\n  - x\n  - y\nc: {d: 4}\nz: new\n", Patched(yaml, """[{"op":"add","path":"/z","value":"new"}]"""));
        Assert.Equal("# head\n\na: 2   # one\nb:\n  - x\n  - y\nc: {d: 4}\n", Patched(yaml, """[{"op":"replace","path":"/a","value":2}]"""));
        Assert.Equal("# head\n\na: 1   # one\nb:\n  - x\n  - y\n", Patched(yaml, """[{"op":"remove","path":"/c"}]"""));
        Assert.Equal("# head\n\na: 1   # one\nb:\n  - x\n  - y\nc: {d: 4}\n", Patched(yaml, """[{"op":"test","path":"/c/d","value":4}]"""));
        Assert.Equal("# head\n\na: 1   # one\nb:\n  - x\n  - y\nc: {d: 4}\ne: 1\n", Patched(yaml, """[{"op":"copy","from":"/a","path":"/e"}]"""));
        Assert.Equal("# head\n\nb:\n  - x\n  - y\nc: {d: 4}\ne: 1\n", Patched(yaml, """[{"op":"move","from":"/a","path":"/e"}]"""));
    }

    [Fact]
    public void An_empty_patch_changes_nothing_and_keeps_views_valid()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        YamlNode a = doc.Root["a"];
        doc.ApplyPatch("[]");
        doc.ApplyPatch(new YamlPatch());
        doc.ApplyMergePatch("{}");
        Assert.Equal(1, a.GetValue<int>());
        Assert.Equal("a: 1\n", doc.ToString());
    }

    [Fact]
    public void A_patch_that_works_makes_earlier_views_stale_and_one_that_fails_does_not()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\n");
        YamlNode a = doc.Root["a"];
        Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"replace","path":"/a","value":9},{"op":"remove","path":"/zz"}]"""));
        Assert.Equal(1, a.GetValue<int>());
        doc.ApplyPatch("""[{"op":"replace","path":"/a","value":9}]""");
        Assert.Throws<InvalidOperationException>(() => a.GetValue<int>());
        Assert.Equal(9, doc.Get<int>("/a"));
    }

    [Fact]
    public void A_failure_reports_the_operation_and_leaves_every_byte()
    {
        const string yaml = "a: 1\nb: [1, 2]\n# tail\n";
        YamlException ex = Fails(yaml, """[{"op":"replace","path":"/a","value":2},{"op":"add","path":"/b/-","value":3},{"op":"remove","path":"/missing"},{"op":"remove","path":"/a"}]""", YamlErrorCategory.PatchPath, 2);
        Assert.True(ex.Line >= 1);
        Fails(yaml, """[{"op":"remove","path":"/a"},{"op":"test","path":"/b/0","value":2}]""", YamlErrorCategory.PatchTest, 1);
    }

    [Fact]
    public void The_document_index_selects_the_document_and_a_bad_index_is_a_path_error()
    {
        const string yaml = "a: 1\n---\na: 2\n---\na: 3\n";
        Assert.Equal("a: 1\n---\na: 20\n---\na: 3\n", Patched(yaml, """[{"op":"replace","path":"/a","value":20}]""", 1));
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => doc.ApplyPatch("[]", 3)).Category);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => doc.ApplyMergePatch("{}", -1)).Category);
    }

    // ---- test ----

    [Theory]
    [InlineData("a: 1\n", "/a", "1", true)]
    [InlineData("a: 1\n", "/a", "1.0", false)]
    [InlineData("a: 1.0\n", "/a", "1", false)]
    [InlineData("a: 1.0\n", "/a", "1.0", true)]
    [InlineData("a: 0x1F\n", "/a", "31", true)]
    [InlineData("a: 0o17\n", "/a", "15", true)]
    [InlineData("a: 1e3\n", "/a", "1000.0", true)]
    [InlineData("a: 12345678901234567890123\n", "/a", "12345678901234567890123", true)]
    [InlineData("a: yes\n", "/a", "\"yes\"", true)]
    [InlineData("a: yes\n", "/a", "true", false)]
    [InlineData("a: 'x'\n", "/a", "\"x\"", true)]
    [InlineData("a: |\n  x\n", "/a", "\"x\\n\"", true)]
    [InlineData("a: \"1\"\n", "/a", "1", false)]
    [InlineData("a: \"1\"\n", "/a", "\"1\"", true)]
    [InlineData("a: !!str 1\n", "/a", "\"1\"", true)]
    [InlineData("a: !!str 1\n", "/a", "1", false)]
    [InlineData("a: !!int abc\n", "/a", "\"abc\"", false)]
    [InlineData("a:\n", "/a", "null", true)]
    [InlineData("a: ~\n", "/a", "null", true)]
    [InlineData("a: Null\n", "/a", "null", true)]
    [InlineData("a: 'null'\n", "/a", "null", false)]
    [InlineData("a: True\n", "/a", "true", true)]
    [InlineData("a: {x: 1, y: [1, 2]}\n", "/a", """{"y":[1,2],"x":1}""", true)]
    [InlineData("a: {x: 1, y: [1, 2]}\n", "/a", """{"y":[2,1],"x":1}""", false)]
    [InlineData("a: {x: 1}\n", "/a", """{"x":1,"y":2}""", false)]
    [InlineData("a: {x: 1, y: 2}\n", "/a", """{"x":1}""", false)]
    [InlineData("a: [1, 2]\n", "/a", "[1,2,3]", false)]
    [InlineData("a: {1: x}\n", "/a", """{"1":"x"}""", false)]
    [InlineData("a: {'1': x}\n", "/a", """{"1":"x"}""", true)]
    [InlineData("a: &b {x: 1}\nc: *b\n", "/c", """{"x":1}""", true)]
    [InlineData("a: 1\n", "", """{"a":1}""", true)]
    public void Test_compares_by_the_data_model_under_the_core_schema(string yaml, string path, string value, bool equal)
    {
        string patch = "[{\"op\":\"test\",\"path\":\"" + path + "\",\"value\":" + value + "}]";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        if (equal)
        {
            doc.ApplyPatch(patch);
        }
        else
        {
            Assert.Equal(YamlErrorCategory.PatchTest, Assert.Throws<YamlException>(() => doc.ApplyPatch(patch)).Category);
        }

        Assert.Equal(yaml, doc.ToString());
    }

    [Fact]
    public void Test_of_a_missing_path_is_a_path_error_and_of_two_matching_keys_is_ambiguous()
    {
        Fails("a: 1\n", """[{"op":"test","path":"/b","value":1}]""", YamlErrorCategory.PatchPath, 0);
        Fails("80: a\n\"80\": b\n", """[{"op":"test","path":"/80","value":"a"}]""", YamlErrorCategory.RefusedAmbiguous, 0);
    }

    // ---- copy and move ----

    [Fact]
    public void Copy_of_an_anchored_node_writes_the_value_without_the_anchor()
    {
        Assert.Equal("a: &x\n  k: 1\nb:\n  k: 1\n", Patched("a: &x\n  k: 1\n", """[{"op":"copy","from":"/a","path":"/b"}]"""));
        Assert.Equal("a: &x v\nb: v\n", Patched("a: &x v\n", """[{"op":"copy","from":"/a","path":"/b"}]"""));
    }

    [Fact]
    public void An_alias_in_a_copied_value_stays_an_alias_when_its_anchor_comes_first()
    {
        // The anchor is outside the copied node and before the destination.
        const string yaml = "base: &x\n  k: 1\nitem:\n  ref: *x\n";
        Assert.Equal(yaml + "other:\n  ref: *x\n", Patched(yaml, """[{"op":"copy","from":"/item","path":"/other"}]"""));
        Assert.Equal("base: &x\n  k: 1\nother:\n  ref: *x\n", Patched(yaml, """[{"op":"move","from":"/item","path":"/other"}]"""));
    }

    [Fact]
    public void An_alias_is_written_out_when_its_anchor_follows_the_destination_or_goes_with_the_copy()
    {
        // The anchor is before the destination here, so the alias stays; the case where it is not is below.
        using (YamlDocument doc = YamlDocument.Parse("base: &x\n  k: 1\nitem:\n  ref: *x\n"))
        {
            doc.ApplyPatch("""[{"op":"copy","from":"/item","path":"/base2"}]""");
            Assert.Contains("base2:\n  ref: *x", doc.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal("a:\n  - &x 1\n  - *x\nb:\n  - 1\n  - 1\n", Patched("a:\n  - &x 1\n  - *x\n", """[{"op":"copy","from":"/a","path":"/b"}]"""));
    }

    [Fact]
    public void An_alias_is_written_out_when_the_copy_goes_before_its_anchor()
    {
        const string yaml = "- &x 5\n- ref: *x\n";
        Assert.Equal("- ref: 5\n" + yaml, Patched(yaml, """[{"op":"copy","from":"/1","path":"/0"}]"""));
        Assert.Equal(yaml + "- ref: *x\n", Patched(yaml, """[{"op":"copy","from":"/1","path":"/-"}]"""));
        // A copy over the anchored node itself cannot refer to the anchor it replaces.
        Assert.Equal("a: &x\n  ref: 5\nb: {ref: *x}\n", Patched("a: &x 5\nb: {ref: *x}\n", """[{"op":"copy","from":"/b","path":"/a"}]"""));
    }

    [Fact]
    public void An_alias_is_written_out_when_the_anchor_name_is_defined_twice()
    {
        const string yaml = "a: &x 1\nb: &x 2\nc: *x\nd: {r: *x}\n";
        Assert.Equal(yaml + "e:\n  r: 2\n", Patched(yaml, """[{"op":"copy","from":"/d","path":"/e"}]"""));
    }

    [Fact]
    public void Moving_an_anchored_node_with_live_aliases_is_refused_and_copying_it_is_not()
    {
        const string yaml = "a: &x\n  k: 1\nb: *x\n";
        Fails(yaml, """[{"op":"move","from":"/a","path":"/c"}]""", YamlErrorCategory.RefusedAlias, 0);
        Assert.Equal(yaml + "c:\n  k: 1\n", Patched(yaml, """[{"op":"copy","from":"/a","path":"/c"}]"""));
    }

    [Fact]
    public void Copy_reads_through_an_alias_and_never_writes_through_one()
    {
        const string yaml = "a: &x\n  k: 1\nb: *x\n";
        Assert.Equal(yaml + "c:\n  k: 1\n", Patched(yaml, """[{"op":"copy","from":"/b","path":"/c"}]"""));
        Assert.Equal(yaml + "c: 1\n", Patched(yaml, """[{"op":"copy","from":"/b/k","path":"/c"}]"""));
    }

    [Fact]
    public void Moving_a_value_into_itself_is_invalid_and_moving_it_onto_itself_is_nothing()
    {
        Fails("a:\n  b: 1\n", """[{"op":"move","from":"/a","path":"/a/c"}]""", YamlErrorCategory.PatchInvalid, 0);
        Assert.Equal("a:\n  b: 1\n", Patched("a:\n  b: 1\n", """[{"op":"move","from":"/a","path":"/a"}]"""));
        Fails("a:\n  b: 1\n", """[{"op":"move","from":"/zz","path":"/zz"}]""", YamlErrorCategory.PatchPath, 0);
    }

    [Fact]
    public void Moves_within_a_sequence_follow_the_rfc_order()
    {
        Assert.Equal("- b\n- c\n- a\n", Patched("- a\n- b\n- c\n", """[{"op":"move","from":"/0","path":"/2"}]"""));
        Assert.Equal("- c\n- a\n- b\n", Patched("- a\n- b\n- c\n", """[{"op":"move","from":"/2","path":"/0"}]"""));
    }

    [Fact]
    public void A_move_between_mappings_never_leaves_an_empty_mapping_on_the_way()
    {
        Assert.Equal("b: 1\n", Patched("a: 1\n", """[{"op":"move","from":"/a","path":"/b"}]"""));
        Assert.Equal("m:\n  y: 1\n", Patched("m:\n  x: 1\n", """[{"op":"move","from":"/m/x","path":"/m/y"}]"""));
    }

    // ---- invalid patches ----

    [Theory]
    [InlineData("{}")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("\"add\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[{\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"add\"}]")]
    [InlineData("[{\"op\":1,\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"add\",\"path\":1,\"value\":1}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"test\",\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"move\",\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"copy\",\"path\":\"/a\",\"from\":1}]")]
    [InlineData("[{\"op\":\"Add\",\"path\":\"/a\",\"value\":1}]")]
    [InlineData("[{\"op\":\"frobnicate\",\"path\":\"/a\"}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\",\"value\":1,\"value\":2}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\",\"value\":{\"k\":1,\"k\":2}}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\",\"value\":\"\\ud800\"}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\",\"value\":1}")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/a\",\"value\":1}] x")]
    [InlineData("[{\"op\":\"remove\",\"path\":\"a\"}]")]
    [InlineData("[{\"op\":\"remove\",\"path\":\"/a~2\"}]")]
    public void A_patch_that_is_not_valid_is_a_patch_invalid_error_and_changes_nothing(string patch)
    {
        Fails("a: 1\n", patch, YamlErrorCategory.PatchInvalid);
    }

    [Fact]
    public void An_invalid_operation_names_its_index()
    {
        Fails("a: 1\n", """[{"op":"replace","path":"/a","value":2},{"op":"frobnicate","path":"/a"}]""", YamlErrorCategory.PatchInvalid, 1);
        Fails("a: 1\n", """[{"op":"replace","path":"/a","value":2},{"op":"remove","path":"a"}]""", YamlErrorCategory.PatchInvalid, 1);
    }

    [Fact]
    public void A_deeply_nested_patch_is_a_limit_or_invalid_error_and_never_a_crash()
    {
        string deep = "[{\"op\":\"add\",\"path\":\"/a\",\"value\":" + new string('[', 100_000) + new string(']', 100_000) + "}]";
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        var ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(deep));
        Assert.True(ex.Category is YamlErrorCategory.PatchInvalid or YamlErrorCategory.Limit);
        Assert.Equal("a: 1\n", doc.ToString());

        // A value deeper than the limit that the document would allow.
        var limited = new YamlDocumentOptions { Limits = YamlLimits.Untrusted };
        using YamlDocument strict = YamlDocument.Parse("a: 1\n", limited);
        string nested = "[{\"op\":\"add\",\"path\":\"/a\",\"value\":" + new string('[', 300) + new string(']', 300) + "}]";
        Assert.Throws<YamlException>(() => strict.ApplyPatch(nested));
        Assert.Equal("a: 1\n", strict.ToString());
    }

    // ---- builder and JSON forms ----

    [Fact]
    public void The_builder_makes_the_same_patch_as_the_json_text()
    {
        var patch = new YamlPatch()
            .Test("/name", "web")
            .Replace("/replicas", 3)
            .Add("/labels/tier", "frontend")
            .Copy("/replicas", "/copy")
            .Move("/old", "/new")
            .Remove("/gone");
        Assert.Equal(6, patch.Count);
        Assert.Equal(YamlPatchOperationKind.Move, patch[4].Kind);
        Assert.Equal("/old", patch[4].From);
        Assert.Equal("/new", patch[4].Path);
        int listed = 0;
        foreach (YamlPatchOperation unused in patch)
        {
            _ = unused;
            listed++;
        }

        Assert.Equal(6, listed);

        const string yaml = "name: web\nreplicas: 1\nlabels:\n  app: x\nold: o\ngone: g\n";
        using YamlDocument viaBuilder = YamlDocument.Parse(yaml);
        viaBuilder.ApplyPatch(patch);
        using YamlDocument viaText = YamlDocument.Parse(yaml);
        viaText.ApplyPatch("""
            [{"op":"test","path":"/name","value":"web"},{"op":"replace","path":"/replicas","value":3},
             {"op":"add","path":"/labels/tier","value":"frontend"},{"op":"copy","from":"/replicas","path":"/copy"},
             {"op":"move","from":"/old","path":"/new"},{"op":"remove","path":"/gone"}]
            """);
        Assert.Equal(viaText.ToString(), viaBuilder.ToString());
        Assert.Equal("name: web\nreplicas: 3\nlabels:\n  app: x\n  tier: frontend\ncopy: 3\nnew: o\n", viaBuilder.ToString());

        // The same patch applies to another document.
        using YamlDocument other = YamlDocument.Parse(yaml);
        other.ApplyPatch(patch);
        Assert.Equal(viaBuilder.ToString(), other.ToString());
    }

    [Fact]
    public void The_builder_example_in_the_documentation_runs()
    {
        using YamlDocument document = YamlDocument.Parse("name: web\nreplicas: 1\nlabels:\n  app: x\n");

        // The example from the YamlPatch documentation.
        var patch = new YamlPatch()
            .Test("/name", "web")
            .Replace("/replicas", 3)
            .Add("/labels/tier", "frontend");
        document.ApplyPatch(patch);

        Assert.Equal("name: web\nreplicas: 3\nlabels:\n  app: x\n  tier: frontend\n", document.ToString());
    }

    [Fact]
    public void Builder_values_can_be_collections_nodes_and_null_and_bad_types_are_refused_when_added()
    {
        using YamlDocument source = YamlDocument.Parse("k: [1, 2]\n");
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        doc.ApplyPatch(new YamlPatch()
            .Add("/list", new List<object?> { 1, "two", null, 3.5 })
            .Add("/map", new Dictionary<string, object?> { ["x"] = true })
            .Add("/node", source.Root["k"])
            .Add("/nothing", null));
        Assert.Equal("a: 1\nlist:\n  - 1\n  - two\n  - null\n  - 3.5\nmap:\n  x: true\nnode:\n  - 1\n  - 2\nnothing: null\n", doc.ToString());

        Assert.Throws<ArgumentException>(() => new YamlPatch().Add("/a", new object()));
        Assert.Throws<ArgumentNullException>(() => new YamlPatch().Move(null!, "/a"));
        Assert.Throws<ArgumentNullException>(() => doc.ApplyPatch((YamlPatch)null!));
    }

    [Fact]
    public void Patches_can_be_parsed_ahead_and_given_as_utf8_or_json_nodes()
    {
        const string text = """[{"op":"add","path":"/b","value":{"x":[1,2.0,"s",null,true]}}]""";
        const string expected = "a: 1\nb:\n  x:\n    - 1\n    - 2.0\n    - s\n    - null\n    - true\n";
        using YamlDocument one = YamlDocument.Parse("a: 1\n");
        one.ApplyPatch(YamlPatch.Parse(text));
        using YamlDocument two = YamlDocument.Parse("a: 1\n");
        two.ApplyPatch(Encoding.UTF8.GetBytes(text));
        using YamlDocument three = YamlDocument.Parse("a: 1\n");
        three.ApplyPatch(JsonNode.Parse(text));
        using YamlDocument four = YamlDocument.Parse("a: 1\n");
        four.ApplyPatch(YamlPatch.Parse(Encoding.UTF8.GetBytes(text)));
        using YamlDocument five = YamlDocument.Parse("a: 1\n");
        five.ApplyPatch(YamlPatch.FromJson(JsonNode.Parse(text)));
        foreach (YamlDocument d in new[] { one, two, three, four, five })
        {
            Assert.Equal(expected, d.ToString());
        }
    }

    [Fact]
    public void A_json_node_built_in_code_keeps_integers_and_floats_apart()
    {
        var patch = new JsonArray
        {
            new JsonObject { ["op"] = "add", ["path"] = "/i", ["value"] = 1 },
            new JsonObject { ["op"] = "add", ["path"] = "/f", ["value"] = 1.0 },
            new JsonObject { ["op"] = "add", ["path"] = "/big", ["value"] = 12345678901234L },
            new JsonObject { ["op"] = "add", ["path"] = "/m", ["value"] = 1.5m },
            new JsonObject { ["op"] = "add", ["path"] = "/n", ["value"] = null },
            new JsonObject { ["op"] = "add", ["path"] = "/t", ["value"] = true },
            new JsonObject { ["op"] = "add", ["path"] = "/s", ["value"] = "text" },
        };
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        doc.ApplyPatch(patch);
        Assert.Equal("a: 1\ni: 1\nf: 1.0\nbig: 12345678901234\nm: 1.5\nn: null\nt: true\ns: text\n", doc.ToString());
        doc.ApplyPatch(new JsonArray(new JsonObject { ["op"] = "test", ["path"] = "/f", ["value"] = 1.0 }));
        Assert.Equal(YamlErrorCategory.PatchTest, Assert.Throws<YamlException>(() => doc.ApplyPatch(new JsonArray(new JsonObject { ["op"] = "test", ["path"] = "/f", ["value"] = 1 }))).Category);
    }

    [Fact]
    public void Json_values_are_written_in_the_style_of_their_siblings()
    {
        Assert.Equal("a: 'x'\nb: 'it''s'\n", Patched("a: 'x'\n", """[{"op":"add","path":"/b","value":"it's"}]"""));
        Assert.Equal("- \"a\"\n- \"b\"\n", Patched("- \"a\"\n", """[{"op":"add","path":"/-","value":"b"}]"""));
        Assert.Equal("a: 1\nb: yes\n", Patched("a: 1\n", """[{"op":"add","path":"/b","value":"yes"}]"""));
        Assert.Equal("a: 1\nb: '1'\nc: 'true'\n", Patched("a: 1\n", """[{"op":"add","path":"/b","value":"1"},{"op":"add","path":"/c","value":"true"}]"""));
        Assert.Equal("a: {x: 1}\n", Patched("a: {}\n", """[{"op":"add","path":"/a/x","value":1}]"""));
    }

    // ---- merge patch ----

    [Fact]
    public void A_merge_patch_adds_replaces_deletes_and_keeps_the_rest()
    {
        const string yaml = "# top\na: 1   # one\nb:\n  c: 2\n  d: 3\nlist: [1, 2]\n";
        Assert.Equal("# top\na: 1   # one\nb:\n  c: 20\n  e: 5\nlist: [1, 2]\n", Merged(yaml, """{"b":{"c":20,"d":null,"e":5}}"""));
        Assert.Equal("# top\na: 1   # one\nb:\n  c: 2\n  d: 3\nlist: [9]\n", Merged(yaml, """{"list":[9]}"""));
        Assert.Equal("# top\na: 1   # one\nb:\n  c: 2\n  d: 3\nlist: [1, 2]\n", Merged(yaml, "{}"));
    }

    [Fact]
    public void A_merge_patch_runs_deletions_after_the_other_members()
    {
        Assert.Equal("b: 1\nc: 2\n", Merged("a: 0\n", """{"a":null,"b":1,"c":2}"""));
        Assert.Equal("a: 0\nb: 1\n", Merged("a: 0\n", """{"x":null,"b":1}"""));
    }

    [Fact]
    public void A_merge_patch_fills_in_what_is_missing_without_null_members()
    {
        Assert.Equal("a: 1\nx:\n  z: 1\n", Merged("a: 1\n", """{"x":{"y":null,"z":1}}"""));
        Assert.Equal("a:\n  b: 1\n", Merged("a: scalar\n", """{"a":{"b":1,"c":null}}"""));
        Assert.Equal("a: {}\n", Merged("a: scalar\n", """{"a":{}}"""));
        Assert.Equal("a:\n  - {k: 1}\n", Merged("a: x\n", """{"a":[{"k":1}]}""").Replace("- k: 1", "- {k: 1}", StringComparison.Ordinal));
    }

    [Fact]
    public void A_merge_patch_that_is_not_an_object_replaces_the_value_or_the_document()
    {
        Assert.Equal("5\n", Merged("a: 1\nb: 2\n", "5"));
        Assert.Equal("null\n", Merged("a: 1\n", "null").Replace("~", "null", StringComparison.Ordinal));
        Assert.Equal("a: 1\n---\n- 1\n- 2\n", Merged("a: 1\n---\nb: 2\n", "[1,2]", 1));
    }

    [Fact]
    public void A_merge_patch_refuses_to_merge_into_an_alias_but_may_replace_one()
    {
        const string yaml = "a: &x\n  k: 1\nb: *x\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        var ex = Assert.Throws<YamlException>(() => doc.ApplyMergePatch("""{"b":{"k":2}}"""));
        Assert.Equal(YamlErrorCategory.RefusedAlias, ex.Category);
        Assert.Equal(yaml, doc.ToString());
        Assert.Equal("a: &x\n  k: 1\nb: 7\n", Merged(yaml, """{"b":7}"""));
        Assert.Equal(yaml, Merged(yaml, """{"b":{}}"""));
        // Writing at the anchor shows through every alias.
        Assert.Equal("a: &x\n  k: 2\nb: *x\n", Merged(yaml, """{"a":{"k":2}}"""));
    }

    [Fact]
    public void A_merge_patch_is_atomic_when_a_later_member_fails()
    {
        using YamlDocument doc = YamlDocument.Parse("a: &x\n  k: 1\nb: *x\nc: 1\n");
        string before = doc.ToString();
        Assert.Throws<YamlException>(() => doc.ApplyMergePatch("""{"c":2,"b":{"k":3}}"""));
        Assert.Equal(before, doc.ToString());
        Assert.Throws<YamlException>(() => doc.ApplyMergePatch("{\"c\":2,"));
        Assert.Equal(before, doc.ToString());
    }

    [Fact]
    public void A_merge_patch_takes_utf8_and_json_nodes_and_checks_the_document_index()
    {
        using YamlDocument one = YamlDocument.Parse("a: 1\n");
        one.ApplyMergePatch("""{"b":2}"""u8);
        using YamlDocument two = YamlDocument.Parse("a: 1\n");
        two.ApplyMergePatch(JsonNode.Parse("""{"b":2}"""));
        Assert.Equal(one.ToString(), two.ToString());
        Assert.Equal("a: 1\nb: 2\n", two.ToString());
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => two.ApplyMergePatch("{}", 1)).Category);
        Assert.Equal(YamlErrorCategory.PatchInvalid, Assert.Throws<YamlException>(() => two.ApplyMergePatch("{\"a\":1,\"a\":2}")).Category);
    }

    [Fact]
    public void A_merge_patch_member_that_matches_two_keys_is_refused()
    {
        using YamlDocument doc = YamlDocument.Parse("80: a\n\"80\": b\n");
        var ex = Assert.Throws<YamlException>(() => doc.ApplyMergePatch("""{"80":"c"}"""));
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, ex.Category);
        Assert.Equal("80: a\n\"80\": b\n", doc.ToString());
    }

    // ---- duplicate keys ----

    [Theory]
    [InlineData("a: 1\na: 2\n")]
    [InlineData("1: a\n0x1: b\n")]
    [InlineData("1: a\n+1: b\n")]
    [InlineData("1.0: a\n1.00: b\n")]
    [InlineData("0.0: a\n-0.0: b\n")]
    [InlineData(".nan: a\n.NaN: b\n")]
    [InlineData("null: a\n~: b\n")]
    [InlineData("null: a\n: b\n")]
    [InlineData("true: a\nTrue: b\n")]
    [InlineData("'a': 1\n\"a\": 2\n")]
    [InlineData("a: 1\n? a\n: 2\n")]
    [InlineData("{a: 1, a: 2}\n")]
    [InlineData("[{a: 1, b: 2, a: 3}]\n")]
    [InlineData("x:\n  y:\n    - k: 1\n      k: 2\n")]
    [InlineData("a b: 1\n\"a b\": 2\n")]
    [InlineData("!!str 1: a\n'1': b\n")]
    [InlineData("12345678901234567890123: a\n12345678901234567890123: b\n")]
    [InlineData("? |\n  x\n: a\n? |\n  x\n: b\n")]
    public void A_repeated_key_is_a_duplicate_key_error_after_core_resolution(string yaml)
    {
        var ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.True(ex.Line >= 1 && ex.Column >= 1);
        using YamlDocument allowed = YamlDocument.Parse(yaml, new YamlDocumentOptions { AllowDuplicateKeys = true });
        Assert.Equal(yaml, allowed.ToString());
    }

    [Theory]
    [InlineData("1: a\n'1': b\n")]
    [InlineData("1: a\n1.0: b\n")]
    [InlineData("1: a\n\"1\": b\ntrue: c\n'true': d\nnull: e\n'null': f\n")]
    [InlineData("a: 1\nb: 2\n")]
    [InlineData("? [a]\n: 1\n? [a]\n: 2\n")]
    [InlineData("a: {x: 1}\nb: {x: 1}\n")]
    [InlineData("- {a: 1}\n- {a: 2}\n")]
    [InlineData("0x1F: a\n32: b\n")]
    [InlineData(".inf: a\n-.inf: b\n")]
    public void Keys_that_differ_under_the_core_schema_are_not_duplicates(string yaml)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(yaml, doc.ToString());
    }

    [Fact]
    public void Large_mappings_are_checked_too_and_a_duplicate_at_the_end_is_found()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("key").Append(i).Append(": ").Append(i).Append('\n');
        }

        using (YamlDocument fine = YamlDocument.Parse(sb.ToString()))
        {
            Assert.Equal(5000, fine.Root.Count);
        }

        sb.Append("key17: again\n");
        var ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(sb.ToString()));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal(5001, ex.Line);
    }

    [Fact]
    public void With_duplicates_allowed_a_pointer_or_indexer_that_matches_both_is_refused_as_ambiguous()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\na: 2\nb: 3\n", new YamlDocumentOptions { AllowDuplicateKeys = true });
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, Assert.Throws<YamlException>(() => doc.Get<int>("/a")).Category);
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, Assert.Throws<YamlException>(() => doc.Set("/a", 9)).Category);
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, Assert.Throws<YamlException>(() => doc.Root["a"]).Category);
        Assert.Equal(YamlErrorCategory.RefusedAmbiguous, Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"remove","path":"/a"}]""")).Category);
        doc.Set("/b", 4);
        Assert.Equal("a: 1\na: 2\nb: 4\n", doc.ToString());
    }

    [Fact]
    public void An_edit_keeps_the_option_so_a_document_that_allows_duplicates_stays_editable()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\na: 2\nb: 3\n", new YamlDocumentOptions { AllowDuplicateKeys = true });
        doc.Set("/b", 4);
        doc.ApplyPatch("""[{"op":"add","path":"/c","value":5},{"op":"replace","path":"/b","value":6}]""");
        Assert.Equal("a: 1\na: 2\nb: 6\nc: 5\n", doc.ToString());
    }
}
