using System.Numerics;
using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Behaviour of the document, its nodes and the edit engine on small, named inputs.</summary>
public class DocumentBehaviorTests
{
    private static string Edit(string yaml, Action<YamlDocument> edit)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        edit(doc);
        return doc.ToString();
    }

    private static YamlException Refused(string yaml, Action<YamlDocument> edit, YamlErrorCategory category)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        string before = doc.ToString();
        YamlException ex = Assert.Throws<YamlException>(() => edit(doc));
        Assert.Equal(category, ex.Category);
        Assert.Equal(before, doc.ToString());
        return ex;
    }

    // ---- The quick start ----

    [Fact]
    public void Quick_start_changes_only_the_edited_values()
    {
        string src = """
            # Build settings
            script: |
              make build
            replicas: 2      # the CI job scales this
            """;

        using var doc = YamlDocument.Parse(src);
        doc.Set("/script", "make build\nmake test\n"); // still a block literal
        doc.Root["replicas"] = 3;                      // only the 2 changes

        Assert.Equal(
            """
            # Build settings
            script: |
              make build
              make test
            replicas: 3      # the CI job scales this
            """,
            doc.ToString());

        Config cfg = YamlSerializer.Deserialize<Config>(src)!;
        Assert.Equal(new Config(2, "make build\n"), cfg);
    }

    private sealed record Config(int Replicas, string Script);

    // ---- Binding a node ----

    public sealed record Server(string Name, int Port, List<string> Tags);

    [Fact]
    public void A_node_binds_to_a_type_with_aliases_expanded()
    {
        using YamlDocument doc = YamlDocument.Parse("tags: &t [a, b]\nweb:\n  name: w\n  port: 80\n  tags: *t\n");
        Server s = doc.Root["web"].Deserialize<Server>()!;
        Assert.Equal(new Server("w", 80, ["a", "b"]).Name, s.Name);
        Assert.Equal(80, s.Port);
        Assert.Equal(["a", "b"], s.Tags);
        Assert.Equal(["a", "b"], doc.Root["tags"].Deserialize<List<string>>());
        Assert.Equal(YamlErrorCategory.Binding, Assert.Throws<YamlException>(() => doc.Root["web"]["name"].Deserialize<int>()).Category);
    }

    // ---- Stale views ----

    [Fact]
    public void A_view_taken_before_an_edit_stays_valid_after_it()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\n");
        YamlNode a = doc.Root["a"];
        YamlNode root = doc.Root;
        Assert.Equal(1, a.GetValue<int>());
        doc.Set("/b", 3);
        Assert.Equal(1, a.GetValue<int>());
        Assert.Equal(3, root["b"].GetValue<int>());
        Assert.Equal(2, root.Count);
        Assert.Equal(YamlNodeKind.Scalar, a.Kind);
        a.Set(5);
        Assert.Equal("a: 5\nb: 3\n", doc.ToString());
        a.Remove();
        Assert.Equal("b: 3\n", doc.ToString());
        Assert.Throws<InvalidOperationException>(() => a.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => a.Set(7));
        Assert.Equal("b: 3\n", doc.ToString());
        Assert.Single(root.Items);
    }

    [Fact]
    public void A_failed_edit_leaves_views_and_text_usable()
    {
        using YamlDocument doc = YamlDocument.Parse("a: &x [1]\nb: *x\n");
        YamlNode root = doc.Root;
        Assert.Throws<YamlException>(() => doc.Set("/b/0", 9));
        Assert.Equal("a: &x [1]\nb: *x\n", doc.ToString());
        Assert.Equal(2, root.Count);
        Assert.Equal(1, root["b"][0].GetValue<int>());
    }

    [Fact]
    public void A_view_made_from_a_value_is_detached_and_cannot_be_read_from_a_document()
    {
        YamlNode n = 5;
        Assert.True(n.IsDetached);
        Assert.Throws<InvalidOperationException>(() => n.Count);
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        doc.Root["a"] = n;
        Assert.Equal("a: 5\n", doc.ToString());
    }

    // ---- One engine: indexers and pointers agree ----

    [Theory]
    [InlineData("a: 1\nb: 2\n", "/a", "x y")]
    [InlineData("a: 1\nb: 2\n", "/c", 7)]
    [InlineData("k:\n  - 1\n  - 2\n", "/k/1", "two")]
    [InlineData("k:\n  - 1\n  - 2\n", "/k/2", true)]
    [InlineData("k:\n  - 1\n  - 2\n", "/k/-", null)]
    [InlineData("- {a: 1}\n- [x]\n", "/0/b", 2.5)]
    [InlineData("a:\n  b:\n    c: 1\n", "/a/b/d", "yes")]
    public void An_indexer_edit_gives_the_same_bytes_as_the_pointer_edit(string yaml, string path, object? value)
    {
        string byPointer = Edit(yaml, d => d.Set(path, value));
        string byIndexer = Edit(yaml, d =>
        {
            string[] tokens = path[1..].Split('/');
            YamlNode node = d.Root;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                node = int.TryParse(tokens[i], out int idx) && node.Kind == YamlNodeKind.Sequence ? node[idx] : node[tokens[i]];
            }

            string last = tokens[^1];
            YamlNode v = YamlNode.Create(value);
            if (node.Kind == YamlNodeKind.Sequence)
            {
                node[last == "-" ? node.Count : int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = v;
            }
            else
            {
                node[last] = v;
            }
        });
        Assert.Equal(byPointer, byIndexer);
    }

    [Fact]
    public void Remove_through_a_view_gives_the_same_bytes_as_remove_by_pointer()
    {
        const string yaml = "a: 1\n# about b\nb:\n  c: 2\n  d: 3\ne: [1, 2, 3]\n";
        Assert.Equal(Edit(yaml, d => d.Remove("/b")), Edit(yaml, d => d.Root["b"].Remove()));
        Assert.Equal(Edit(yaml, d => d.Remove("/b/c")), Edit(yaml, d => d.Root["b"]["c"].Remove()));
        Assert.Equal(Edit(yaml, d => d.Remove("/e/1")), Edit(yaml, d => d.Root["e"][1].Remove()));
        Assert.Equal(Edit(yaml, d => d.Set("/a", "z")), Edit(yaml, d => d.Root["a"].Set("z")));
    }

    // ---- Failure leaves the document byte-identical ----

    [Fact]
    public void Edits_that_are_refused_leave_the_document_byte_identical()
    {
        const string yaml = "# c\na: &x\n  k: 1\nb: *x\n80: n\n\"80\": s\nlist: [1, 2]\n";
        Refused(yaml, d => d.Set("/b/k", 2), YamlErrorCategory.RefusedAlias);
        Refused(yaml, d => d.Add("/b/z", 2), YamlErrorCategory.RefusedAlias);
        Refused(yaml, d => d.Remove("/b/k"), YamlErrorCategory.RefusedAlias);
        Refused(yaml, d => d.Remove("/a"), YamlErrorCategory.RefusedAlias);
        Refused(yaml, d => d.Set("/80", 1), YamlErrorCategory.RefusedAmbiguous);
        Refused(yaml, d => d.Get<int>("/80"), YamlErrorCategory.RefusedAmbiguous);
        Refused(yaml, d => d.Set("/nothing/here", 1), YamlErrorCategory.PatchPath);
        Refused(yaml, d => d.Remove("/list/2"), YamlErrorCategory.PatchPath);
        Refused(yaml, d => d.Remove("/list/-"), YamlErrorCategory.PatchPath);
        Refused(yaml, d => d.Set("/list/3", 1), YamlErrorCategory.PatchPath);
        Refused(yaml, d => d.Set("/list/01", 1), YamlErrorCategory.PatchPath);
        Refused(yaml, d => d.Set("a", 1), YamlErrorCategory.PatchInvalid);
        Refused(yaml, d => d.Set("/a~2", 1), YamlErrorCategory.PatchInvalid);
        Refused(yaml, d => d.Remove(string.Empty), YamlErrorCategory.PatchInvalid);
        Assert.Equal("a: 1\n", Edit("a: 1\n", d => Assert.Throws<ArgumentOutOfRangeException>(() => d.Set("/a", 2, document: 3))));
    }

    [Fact]
    public void A_value_that_has_no_yaml_form_is_an_argument_error_and_changes_nothing()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        Assert.Throws<ArgumentException>(() => doc.Set("/a", new NoYamlForm()));
        Assert.Throws<ArgumentException>(() => doc.Set("/a", "bad \ud800 surrogate"));
        Assert.Throws<ArgumentException>(() => doc.Set("/a", new Dictionary<object, object?> { [new object()] = 1 }));
        Assert.Throws<ArgumentNullException>(() => doc.Set(null!, 1));
        Assert.Equal("a: 1\n", doc.ToString());
    }

    // ---- Anchors, aliases and tags (the cases that sank an earlier document) ----

    [Fact]
    public void A_key_that_carries_an_anchor_cannot_be_removed_while_an_alias_uses_it()
    {
        Refused("&k key: 1\nother: *k\n", d => d.Remove("/key"), YamlErrorCategory.RefusedAlias);
        Assert.Equal("other: 2\n", Edit("&k key: 1\nother: 2\n", d => d.Remove("/key")));
        Assert.Equal("&k key: 5\nother: *k\n", Edit("&k key: 1\nother: *k\n", d => d.Set("/key", 5)));
    }

    [Fact]
    public void Replacing_an_anchored_node_keeps_the_anchor_and_aliases_read_the_new_value()
    {
        using YamlDocument doc = YamlDocument.Parse("a: &x [1, 2]\nb: *x\n");
        doc.Set("/a", "now text");
        Assert.Equal("a: &x now text\nb: *x\n", doc.ToString());
        Assert.Equal("now text", doc.Get<string>("/b"));
    }

    [Fact]
    public void Replacing_the_alias_node_itself_is_allowed()
    {
        Assert.Equal("a: &x 1\nb: 2\n", Edit("a: &x 1\nb: *x\n", d => d.Set("/b", 2)));
    }

    [Fact]
    public void Replacing_or_removing_a_subtree_that_holds_an_anchor_in_use_is_refused()
    {
        Refused("l:\n  - &x 1\nr: *x\n", d => d.Set("/l", "gone"), YamlErrorCategory.RefusedAlias);
        Refused("l:\n  - &x 1\nr: *x\n", d => d.Remove("/l"), YamlErrorCategory.RefusedAlias);
        Assert.Equal("l:\n  - 1\nr: 2\n", Edit("l:\n  - &x 1\nr: *x\n", d => d.Set("/r", 2)).Replace("&x ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void An_anchor_that_is_redefined_binds_each_alias_to_the_definition_before_it()
    {
        const string yaml = "a: &x 1\nb: *x\nc: &x 2\nd: *x\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(1, doc.Get<int>("/b"));
        Assert.Equal(2, doc.Get<int>("/d"));
        // The first anchor is used by an alias; the second is used by another.
        Refused(yaml, d => d.Remove("/a"), YamlErrorCategory.RefusedAlias);
        Refused(yaml, d => d.Remove("/c"), YamlErrorCategory.RefusedAlias);
    }

    [Fact]
    public void The_bare_non_specific_tag_stays_only_while_the_value_is_a_string()
    {
        Assert.Equal("a: ! y\n", Edit("a: ! 12\n", d => d.Set("/a", "y")));
        Assert.Equal("a: 5\n", Edit("a: ! 12\n", d => d.Set("/a", 5)));
        Assert.Equal("a: !!str y\n", Edit("a: !!str 12\n", d => d.Set("/a", "y")));
        Assert.Equal("a: 5\n", Edit("a: !!str 12\n", d => d.Set("/a", 5)));
        Assert.Equal("a: !!int 7\n", Edit("a: !!int 12\n", d => d.Set("/a", 7)));
        Assert.Equal("a: x\n", Edit("a: !!int 12\n", d => d.Set("/a", "x")));
        Assert.Equal("a: !local x\n", Edit("a: !local 12\n", d => d.Set("/a", "x")));
        Assert.Equal("a: 1\n", Edit("a: !local 12\n", d => d.Set("/a", 1)));
    }

    [Fact]
    public void A_tag_on_a_collection_stays_when_the_new_value_is_the_same_kind_of_node()
    {
        Assert.Equal("a: !!map\n  x: 1\n", Edit("a: !!map\n  y: 2\n", d => d.Set("/a", new Dictionary<string, object?> { ["x"] = 1 })));
        Assert.Equal("a:\n  - 1\n", Edit("a: !!map\n  y: 2\n", d => d.Set("/a", new List<object?> { 1 })));
        Assert.Equal("a: !t [1]\n", Edit("a: !t [2]\n", d => d.Set("/a", new List<object?> { 1 })));
    }

    [Fact]
    public void A_value_taken_from_a_node_is_copied_without_its_anchor_and_with_aliases_expanded()
    {
        using YamlDocument doc = YamlDocument.Parse("a: &x [1, 2]\nb: *x\nc: 0\n");
        doc.Set("/c", doc.GetNode("/b"));
        Assert.Equal("a: &x [1, 2]\nb: *x\nc:\n  - 1\n  - 2\n", doc.ToString());

        using YamlDocument other = YamlDocument.Parse("src: &s {k: v}\nref: *s\n");
        using YamlDocument target = YamlDocument.Parse("t: 1\n");
        target.Set("/t", other.GetNode("/ref"));
        Assert.Equal("t:\n  k: v\n", target.ToString());
        Assert.DoesNotContain("*", target.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_a_large_alias_expansion_hits_the_alias_budget_and_changes_nothing()
    {
        var sb = new StringBuilder("l0: &l0 [a, a, a, a, a, a, a, a, a]\n");
        for (int i = 1; i <= 9; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 9))}]\n");
        }

        sb.Append("t: 0\n");
        using YamlDocument doc = YamlDocument.Parse(sb.ToString());
        string before = doc.ToString();
        YamlException ex = Assert.Throws<YamlException>(() => doc.Set("/t", doc.GetNode("/l9")));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(before, doc.ToString());
    }

    // ---- Conventions for inserted content ----

    [Fact]
    public void A_new_member_goes_after_the_last_entry_and_its_comment_but_before_the_footer()
    {
        Assert.Equal("a: 1\nb: 2 # two\nc: 3\n# footer\n", Edit("a: 1\nb: 2 # two\n# footer\n", d => d.Set("/c", 3)));
    }

    [Fact]
    public void A_new_member_copies_the_nearest_preceding_sibling_quote_style()
    {
        Assert.Equal("a: 'x'\nb: y\nc: z\n", Edit("a: 'x'\nb: y\n", d => d.Set("/c", "z")));
        Assert.Equal("a: y\nb: 'x'\nc: 'z'\n", Edit("a: y\nb: 'x'\n", d => d.Set("/c", "z")));
        Assert.Equal("a: \"x\"\nb: 3\n", Edit("a: \"x\"\n", d => d.Set("/b", 3)));
        Assert.Equal("- a\n- 'true'\n", Edit("- a\n", d => d.Set("/-", "true")));
    }

    [Fact]
    public void Setting_a_value_to_null_writes_null_and_an_existing_null_keeps_its_spelling()
    {
        Assert.Equal("a: null\n", Edit("a: 1\n", d => d.Set("/a", null)));
        Assert.Equal("a: ~\nb: 1\n", Edit("a: ~\nb: 1\n", d => d.Set("/a", null)));
        Assert.Equal("a:\nb: 1\n", Edit("a:\nb: 1\n", d => d.Set("/a", null)));
        Assert.Equal("a: Null\n", Edit("a: Null\n", d => d.Set("/a", null)));
        Assert.Equal("a: 3\nb: ~\n", Edit("a: 3\nb: ~\n", d => d.Set("/a", 3)));
    }

    [Fact]
    public void A_null_value_can_be_filled_in_without_disturbing_the_line()
    {
        Assert.Equal("a: 5   # c\nb: 1\n", Edit("a:   # c\nb: 1\n", d => d.Set("/a", 5)));
        Assert.Equal("- 5\n- b\n", Edit("-\n- b\n", d => d.Set("/0", 5)));
    }

    [Fact]
    public void Replacing_the_whole_document_keeps_directives_markers_comments_and_the_byte_order_mark()
    {
        Assert.Equal("%YAML 1.2\n--- # top\nx: 1\ny: 2\n...\n# end\n", Edit("%YAML 1.2\n--- # top\na: 1\n...\n# end\n", d => d.Set(string.Empty, new Dictionary<string, object?> { ["x"] = 1, ["y"] = 2 })));
        Assert.Equal("\uFEFF# h\n[1, 2]\n", Edit("\uFEFF# h\n[a]\n", d => d.Set(string.Empty, new List<object?> { 1, 2 })));
        Assert.Equal("--- 5\n", Edit("--- {a: 1}\n", d => d.Set(string.Empty, 5)));
        Assert.Equal("--- {x: 1}\n", Edit("--- {a: 1}\n", d => d.Set(string.Empty, new Dictionary<string, object?> { ["x"] = 1 })));
    }

    [Fact]
    public void A_block_scalar_of_blank_lines_keeps_its_value_when_a_neighbour_is_added_or_removed()
    {
        const string yaml = "- |+\n   \n- b\n";
        using YamlDocument before = YamlDocument.Parse(yaml);
        string value = before.Get<string>("/0");
        using YamlDocument added = YamlDocument.Parse("- |+\n   \n");
        Assert.Equal(value, added.Get<string>("/0"));
        added.Set("/-", false);
        Assert.Equal(value, added.Get<string>("/0"));
        before.Remove("/1");
        Assert.Equal(value, before.Get<string>("/0"));
    }

    [Fact]
    public void Removing_the_last_entry_leaves_an_empty_collection()
    {
        Assert.Equal("a: {}\nc: 2\n", Edit("a:\n  b: 1\nc: 2\n", d => d.Remove("/a/b")));
        Assert.Equal("a: []\n", Edit("a:\n- 1\n", d => d.Remove("/a/0")));
        Assert.Equal("{}\n", Edit("a: 1\n", d => d.Remove("/a")));
        Assert.Equal("- {}\n- 2\n", Edit("- a: 1\n- 2\n", d => d.Remove("/0/a")));
    }

    [Fact]
    public void Block_scalars_keep_their_style_and_header_as_far_as_the_value_allows()
    {
        Assert.Equal("a: |+\n  new\nb: 1\n", Edit("a: |+\n  old\n\nb: 1\n", d => d.Set("/a", "new\n")));
        Assert.Equal("a: |-\n  new\nb: 1\n", Edit("a: |\n  old\nb: 1\n", d => d.Set("/a", "new")));
        Assert.Equal("a: | # keep\n  new\nb: 1\n", Edit("a: | # keep\n  old\nb: 1\n", d => d.Set("/a", "new\n")));
        Assert.Equal("a: 5\nb: 1\n", Edit("a: |\n  old\nb: 1\n", d => d.Set("/a", 5)));
        Assert.Equal("a: |\n  x\n\nb: 1\n", Edit("a: |\n  old\n\nb: 1\n", d => d.Set("/a", "x\n")));
        Assert.Equal("a: |+\n  x\n\nb: 1\n", Edit("a: |\n  old\n\nb: 1\n", d => d.Set("/a", "x\n\n")));
    }

    [Fact]
    public void Line_breaks_of_the_document_are_used_for_new_text()
    {
        Assert.Equal("a: 1\r\nb:\r\n  - 1\r\n  - 2\r\n", Edit("a: 1\r\n", d => d.Set("/b", new List<object?> { 1, 2 })));
        Assert.Equal("a: |\r\n  q\r\n", Edit("a: |\r\n  p\r\n", d => d.Set("/a", "q\n")));
        Assert.Equal("a: 1\r\nb: \"x\\ny\\n\"\r\n", Edit("a: 1\r\n", d => d.Set("/b", "x\ny\n")));
        Assert.Equal("a: 1\rb: 2\r", Edit("a: 1\r", d => d.Set("/b", 2)));
    }

    // ---- Reading ----

    [Fact]
    public void Nodes_report_kind_style_tag_anchor_and_position()
    {
        using YamlDocument doc = YamlDocument.Parse("a: &x !!str 'q'\nb: [1, 2]\nc: |\n  t\nd: *x\n");
        YamlNode a = doc.Root["a"];
        Assert.Equal(YamlNodeKind.Scalar, a.Kind);
        Assert.Equal(YamlNodeStyle.SingleQuoted, a.Style);
        Assert.Equal("tag:yaml.org,2002:str", a.Tag);
        Assert.Equal("!!str", a.RawTag);
        Assert.Equal("x", a.Anchor);
        Assert.Equal(new YamlPosition(1, 13, 12), a.Position);
        Assert.Equal(YamlNodeStyle.Flow, doc.Root["b"].Style);
        Assert.Equal(YamlNodeStyle.Block, doc.Root.Style);
        Assert.Equal(YamlNodeStyle.Literal, doc.Root["c"].Style);
        Assert.Equal(YamlNodeKind.Alias, doc.Root["d"].Kind);
        Assert.Equal("q", doc.Root["d"].GetValue<string>());
        Assert.Equal(YamlNodeKind.Scalar, doc.Root["d"].Target.Kind);
        Assert.Null(doc.Root["b"].Tag);
        Assert.Null(doc.Root["b"].Anchor);
    }

    [Fact]
    public void Scalars_read_under_the_core_schema()
    {
        using YamlDocument doc = YamlDocument.Parse("i: 0x1F\nf: 1.5\ns: yes\nq: '12'\nn: ~\nb: True\nbig: 123456789012345678901234567890\nt: !!str 12\ne:\n");
        Assert.Equal(31, doc.Get<int>("/i"));
        Assert.Equal(31L, doc.Get<long>("/i"));
        Assert.Equal(1.5, doc.Get<double>("/f"));
        Assert.Equal(1.5m, doc.Get<decimal>("/f"));
        Assert.Equal("yes", doc.Get<string>("/s"));
        Assert.False(doc.TryGet("/s", out bool _));
        Assert.Equal("12", doc.Get<string>("/q"));
        Assert.False(doc.TryGet("/q", out int _));
        Assert.True(doc.Get<bool>("/b"));
        Assert.Null(doc.Get<string?>("/n"));
        Assert.Null(doc.Get<string?>("/e"));
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture), doc.Get<BigInteger>("/big"));
        Assert.Equal("12", doc.Get<string>("/t"));
        Assert.False(doc.TryGet("/t", out int _));
        Assert.Equal(31L, doc.Get<object>("/i"));
        Assert.IsType<BigInteger>(doc.Get<object>("/big"));
        Assert.Null(doc.Get<object>("/n"));
        Assert.True(doc.Root["n"].IsNull);
        Assert.True(doc.Root["e"].IsNull);
        Assert.False(doc.Root["s"].IsNull);
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<int>("/big"));
        Assert.Equal(YamlErrorCategory.Binding, ex.Category);
        Assert.Throws<YamlException>(() => doc.Get<sbyte>("/q"));
        Assert.Equal(31, doc.Get<byte>("/i"));
    }

    [Fact]
    public void Collections_enumerate_items_and_entries_and_follow_aliases()
    {
        using YamlDocument doc = YamlDocument.Parse("m: &m {a: 1, b: 2}\ns: [x, y, z]\nalias: *m\n");
        Assert.Equal(["x", "y", "z"], doc.Root["s"].Items.Select(n => n.GetValue<string>()).ToArray());
        Assert.Equal(["a", "b"], doc.Root["alias"].Entries.Select(e => e.Key.GetValue<string>()).ToArray());
        Assert.Equal([1, 2], doc.Root["alias"].Entries.Select(e => e.Value.GetValue<int>()).ToArray());
        Assert.Equal([1, 2], doc.Root["alias"].Items.Select(n => n.GetValue<int>()).ToArray());
        Assert.Equal(2, doc.Root["alias"].Count);
        Assert.Equal(3, doc.Root.Count);
        Assert.True(doc.Root["alias"].ContainsKey("a"));
        Assert.False(doc.Root["alias"].ContainsKey("zz"));
        Assert.True(doc.Contains("/alias/b"));
        Assert.False(doc.Contains("/alias/zz"));
        Assert.False(doc.Contains("/s/3"));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["s"][3]);
        Assert.Contains("'zz'", Assert.Throws<KeyNotFoundException>(() => doc.Root["zz"]).Message);
        Assert.False(doc.Root.TryGetValue("zz", out _));
        Assert.True(doc.Root["s"].TryGetValue(1, out YamlNode? second));
        Assert.Equal("y", second!.GetValue<string>());
        Assert.False(doc.Root["s"].TryGetValue(3, out _));
    }

    [Fact]
    public void A_write_through_a_view_that_came_from_an_alias_is_refused()
    {
        using YamlDocument doc = YamlDocument.Parse("m: &m {a: 1}\nalias: *m\n");
        YamlNode viaAlias = doc.Root["alias"]["a"];
        Assert.Equal(YamlErrorCategory.RefusedAlias, Assert.Throws<YamlException>(() => viaAlias.Set(5)).Category);
        Assert.Equal(YamlErrorCategory.RefusedAlias, Assert.Throws<YamlException>(() => viaAlias.Remove()).Category);
        Assert.Equal(YamlErrorCategory.RefusedAlias, Assert.Throws<YamlException>(() => doc.Root["alias"]["b"] = 1).Category);
        Assert.Equal("m: &m {a: 1}\nalias: *m\n", doc.ToString());
        doc.Root["m"]["a"] = 2;
        Assert.Equal("m: &m {a: 2}\nalias: *m\n", doc.ToString());
    }

    [Fact]
    public void A_stream_has_a_list_of_documents_and_an_empty_stream_has_one()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n---\nb: 2\n...\n---\nc: 3\n");
        Assert.Equal(3, doc.Documents.Count);
        Assert.Equal(2, doc.Documents[1]["b"].GetValue<int>());
        doc.Set("/b", 9, document: 1);
        doc.Add("/d", 4, document: 2);
        Assert.Equal("a: 1\n---\nb: 9\n...\n---\nc: 3\nd: 4\n", doc.ToString());
        Assert.Equal(9, doc.Get<int>("/b", 1));

        foreach (string empty in new[] { string.Empty, "# only a comment\n", "---\n", "..." })
        {
            using YamlDocument e = YamlDocument.Parse(empty);
            Assert.Equal(empty, e.ToString());
            Assert.True(e.Root.IsNull);
        }

        using YamlDocument filled = YamlDocument.Parse("# c\n");
        filled.Set(string.Empty, new Dictionary<string, object?> { ["a"] = 1 });
        Assert.Equal("# c\na: 1\n", filled.ToString());
        using YamlDocument marker = YamlDocument.Parse("---\n");
        marker.Set(string.Empty, 5);
        Assert.Equal("--- 5\n", marker.ToString());
    }

    // ---- Encodings and output ----

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le-bom")]
    [InlineData("utf-16be-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le-bom")]
    [InlineData("utf-32be-bom")]
    [InlineData("utf-32le")]
    public void The_original_encoding_and_byte_order_mark_are_written_back_after_an_edit(string kind)
    {
        const string text = "a: é\nb: \U0001F600\n";
        Encoding enc = kind switch
        {
            "utf-16le-bom" or "utf-16le" => new UnicodeEncoding(false, kind.EndsWith("bom", StringComparison.Ordinal)),
            "utf-16be-bom" or "utf-16be" => new UnicodeEncoding(true, kind.EndsWith("bom", StringComparison.Ordinal)),
            "utf-32le-bom" or "utf-32le" => new UTF32Encoding(false, kind.EndsWith("bom", StringComparison.Ordinal)),
            "utf-32be-bom" => new UTF32Encoding(true, true),
            "utf-8-bom" => new UTF8Encoding(true),
            _ => new UTF8Encoding(false),
        };
        byte[] original = [.. enc.GetPreamble(), .. enc.GetBytes(text)];
        using YamlDocument doc = YamlDocument.Parse(original);
        Assert.Equal(original, doc.ToBytes());
        doc.Set("/c", 5);
        byte[] expected = [.. enc.GetPreamble(), .. enc.GetBytes(text + "c: 5\n")];
        Assert.Equal(expected, doc.ToBytes());
        using var ms = new MemoryStream();
        doc.WriteTo(ms);
        Assert.Equal(expected, ms.ToArray());
        using YamlDocument again = YamlDocument.Parse(ms.ToArray());
        Assert.Equal(5, again.Get<int>("/c"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_bom_less_wide_document_stays_readable_when_an_edit_changes_its_first_character(bool utf32)
    {
        Encoding enc = utf32 ? new UTF32Encoding(false, false) : new UnicodeEncoding(false, false);
        using YamlDocument doc = YamlDocument.Parse(enc.GetBytes("a\n"));
        Assert.Equal(enc.GetBytes("a\n"), doc.ToBytes());
        doc.Set(string.Empty, "\u4e2d\u6587");
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.Equal("\u4e2d\u6587", again.Get<string>(string.Empty));
        Assert.Equal(doc.ToString(), again.ToString().TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task Every_input_and_output_form_agrees()
    {
        const string text = "\uFEFFa: 1\r\nb:  [x,  y]  # c\r\n";
        using YamlDocument fromString = YamlDocument.Parse(text);
        Assert.Equal(text, fromString.ToString());
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        using YamlDocument fromBytes = YamlDocument.Parse(utf8);
        Assert.Equal(utf8, fromBytes.ToUtf8Bytes());
        Assert.Equal(utf8, fromBytes.ToBytes());
        using YamlDocument fromChars = YamlDocument.Parse(text.AsSpan());
        Assert.Equal(utf8, fromChars.ToUtf8Bytes());
        using YamlDocument fromStream = YamlDocument.Parse(new MemoryStream(utf8));
        Assert.Equal(text, fromStream.ToString());
        using YamlDocument fromAsync = await YamlDocument.ParseAsync(new MemoryStream(utf8), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(text, fromAsync.ToString());
        var sw = new StringWriter();
        fromString.WriteTo(sw);
        Assert.Equal(text, sw.ToString());
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        fromString.WriteTo(buffer);
        Assert.Equal(utf8, buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Utf16_input_is_written_back_as_utf16_but_utf8_bytes_are_available()
    {
        byte[] utf16 = [.. new UnicodeEncoding(false, true).GetPreamble(), .. new UnicodeEncoding(false, false).GetBytes("a: 1\n")];
        using YamlDocument doc = YamlDocument.Parse(utf16);
        Assert.Equal(Encoding.UTF8.GetBytes("a: 1\n"), doc.ToUtf8Bytes());
        Assert.Equal(utf16, doc.ToBytes());
    }

    // ---- Exceptions and limits ----

    [Fact]
    public void Malformed_input_is_a_yaml_exception_with_a_position()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("a: [1, 2\nb: 3\n"));
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.True(ex.Line >= 1);
        Assert.Equal(YamlErrorCategory.Encoding, Assert.Throws<YamlException>(() => YamlDocument.Parse("a: \ud800")).Category);
        Assert.Equal(YamlErrorCategory.Encoding, Assert.Throws<YamlException>(() => YamlDocument.Parse([0x61, 0x3A, 0x20, 0xFF])).Category);
        Assert.Equal(YamlErrorCategory.Syntax, Assert.Throws<YamlException>(() => YamlDocument.Parse("a: *nothing\n")).Category);
        Assert.Throws<ArgumentNullException>(() => YamlDocument.Parse((string)null!));
    }

    [Fact]
    public void Deep_nesting_is_a_limit_error_by_default_and_costs_no_stack_when_allowed()
    {
        string deep = new string('[', 20_000) + new string(']', 20_000);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlDocument.Parse(deep)).Category);
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlDocument.Parse(deep, new YamlDocumentOptions { Limits = YamlLimits.Untrusted })).Category);

        using YamlDocument doc = YamlDocument.Parse(deep, new YamlDocumentOptions { Limits = YamlLimits.None });
        Assert.Equal(deep, doc.ToString());
        string path = string.Concat(Enumerable.Repeat("/0", 19_999));
        Assert.True(doc.Contains(path));
        doc.Set(path + "/-", 1);
        Assert.Equal(new string('[', 19_999) + "[1]" + new string(']', 19_999), doc.ToString());
        doc.Set(path, new List<object?> { new List<object?> { 1, 2 } });
        Assert.StartsWith("[[[[", doc.ToString(), StringComparison.Ordinal);

        string deepBlock = string.Concat(Enumerable.Range(0, 3000).Select(i => new string(' ', i) + "a:\n")) + new string(' ', 3000) + "a: 1\n";
        using YamlDocument block = YamlDocument.Parse(deepBlock, new YamlDocumentOptions { Limits = YamlLimits.None });
        Assert.Equal(deepBlock, block.ToString());
        Assert.Equal(1, block.Get<int>(string.Concat(Enumerable.Repeat("/a", 3001))));
    }

    [Fact]
    public void A_deeply_nested_value_is_a_limit_error()
    {
        object? value = 1;
        for (int i = 0; i < 2000; i++)
        {
            value = new List<object?> { value };
        }

        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => doc.Set("/a", value)).Category);
        Assert.Equal("a: 1\n", doc.ToString());
    }

    // ---- Values ----

    [Fact]
    public void Numbers_booleans_nulls_and_text_are_written_so_they_read_back_exactly()
    {
        using YamlDocument doc = YamlDocument.Parse("v: x\n");
        object?[] values = [1, -2L, 3u, 4ul, 1.5, 100.0, -0.0, 1e300, 0.1f, 2.5m, 3m, true, false, null, "", " lead", "yes", "0x1F", "multi\nline", "a: b", BigInteger.Pow(10, 30), 'c'];
        foreach (object? value in values)
        {
            doc.Set("/v", value);
            using YamlDocument back = YamlDocument.Parse(doc.ToString());
            Assert.True(back.TryGet("/v", out object? read), doc.ToString());
            switch (value)
            {
                case null: Assert.Null(read); break;
                case bool b: Assert.Equal(b, read); break;
                case string s: Assert.Equal(s, read); break;
                case char c: Assert.Equal(c.ToString(), read); break;
                case BigInteger big: Assert.Equal(big, read); break;
                case float f: Assert.Equal((double)f, (double)read!, 6); break;
                case decimal m: Assert.Equal((double)m, (double)read!); break;
                case double d: Assert.Equal(d, (double)read!); break;
                default: Assert.Equal(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(read, System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }
    }

    [Fact]
    public void Nested_values_are_written_in_the_style_of_their_surroundings()
    {
        var value = new Dictionary<string, object?> { ["name"] = "n", ["tags"] = new List<object?> { "a", 2 }, ["empty"] = new List<object?>(), ["none"] = new Dictionary<string, object?>() };
        Assert.Equal("a: 1\nb:\n  name: n\n  tags:\n    - a\n    - 2\n  empty: []\n  none: {}\n", Edit("a: 1\n", d => d.Set("/b", value)));
        Assert.Equal("a:\n    x: 1\nb:\n    name: n\n    tags:\n        - a\n        - 2\n    empty: []\n    none: {}\n", Edit("a:\n    x: 1\n", d => d.Set("/b", value)));
        Assert.Equal("a: 1\nb: {name: n, tags: [a, 2], empty: [], none: {}}\n", Edit("a: 1\nb: {x: 1}\n", d => d.Set("/b", value)));
        Assert.Equal("- 1\n- name: n\n  tags:\n    - a\n    - 2\n  empty: []\n  none: {}\n", Edit("- 1\n", d => d.Set("/-", value)));
        Assert.Equal("- - 1\n  - 2\n", Edit("- x\n", d => d.Set("/0", new List<object?> { 1, 2 })));
    }

    [Fact]
    public void A_sequence_under_a_key_copies_the_layout_of_a_sibling_sequence()
    {
        Assert.Equal("k:\n- a\nm: x\nn:\n- 1\n", Edit("k:\n- a\nm: x\n", d => d.Set("/n", new List<object?> { 1 })));
        Assert.Equal("k:\n  - a\nm: x\nn:\n  - 1\n", Edit("k:\n  - a\nm: x\n", d => d.Set("/n", new List<object?> { 1 })));
    }

    // ---- Edits as a batch ----

    [Fact]
    public void Edits_chain_on_states_so_a_failure_in_the_middle_can_be_dropped()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\n");
        DocState start = doc.State;
        YamlLimits limits = YamlLimits.Default;
        DocState one = PointerOps.Write(start, limits, 0, Pointer.Parse("/a"), ValueTree.FromObject(10, 100), PointerOps.Mode.Set);
        DocState two = PointerOps.Write(one, limits, 0, Pointer.Parse("/c"), ValueTree.FromObject("x", 100), PointerOps.Mode.Set);
        Assert.Equal("a: 1\nb: 2\n", Encoding.UTF8.GetString(start.Text));
        Assert.Equal("a: 10\nb: 2\n", Encoding.UTF8.GetString(one.Text));
        Assert.Equal("a: 10\nb: 2\nc: x\n", Encoding.UTF8.GetString(two.Text));
        Assert.Throws<YamlException>(() => PointerOps.Write(two, limits, 0, Pointer.Parse("/zz/y"), ValueTree.FromObject(1, 100), PointerOps.Mode.Set));
        one.Dispose();
        two.Dispose();
        Assert.Equal("a: 1\nb: 2\n", doc.ToString());
    }

    [Fact]
    public void A_pooled_document_can_be_disposed_and_then_refuses_use()
    {
        YamlDocument doc = YamlDocument.Parse("a: 1\n");
        YamlNode a = doc.Root["a"];
        doc.Dispose();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => doc.ToString());
        Assert.Throws<ObjectDisposedException>(() => a.GetValue<int>());
        Assert.Throws<ObjectDisposedException>(() => a.Count);
        Assert.Throws<ObjectDisposedException>(() => a.Set(2));
    }
}

/// <summary>A type the serializer refuses, because a multi-dimensional array has no YAML form.</summary>
internal sealed class NoYamlForm
{
    public int[,] Grid { get; set; } = new int[1, 1];
}
