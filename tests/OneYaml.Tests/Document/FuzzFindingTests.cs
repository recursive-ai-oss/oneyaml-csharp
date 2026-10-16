using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Inputs the fuzz campaign found, kept as tests that describe the expected behaviour of the edit engine.
/// </summary>
public class FuzzFindingTests
{
    private static YamlDocument Open(string yaml) => YamlDocument.Parse(yaml, new YamlDocumentOptions { AllowDuplicateKeys = true });

    // What the edited stream says when it is read again from its bytes.
    private static YamlDocument Reread(YamlDocument edited) => YamlDocument.Parse(edited.ToBytes());

    [Fact]
    public void Removing_the_last_entry_after_a_literal_block_keeps_the_literal_value()
    {
        using YamlDocument doc = Open("a: |\n  old\nb: 1");
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/b\"}]");

        Assert.Equal("old\n", doc.Get<string>("/a"));
        using YamlDocument again = Reread(doc);
        Assert.Equal("old\n", again.Get<string>("/a"));
    }

    [Fact]
    public void Adding_an_entry_after_a_literal_block_at_the_end_of_the_stream_keeps_the_literal_value()
    {
        using YamlDocument doc = Open("a: |\n  o");
        Assert.Equal("o", doc.Get<string>("/a"));

        doc.ApplyPatch("[{\"op\":\"add\",\"path\":\"/b\",\"value\":1}]");

        using YamlDocument again = Reread(doc);
        Assert.Equal("o", again.Get<string>("/a"));
    }

    [Fact]
    public void Copying_a_float_keeps_it_a_float()
    {
        using YamlDocument doc = Open("- !!float 1\n- x\n");
        doc.ApplyPatch("[{\"op\":\"copy\",\"path\":\"/1\",\"from\":\"/0\"}]");

        using YamlDocument again = Reread(doc);
        Assert.IsType<double>(again.GetNode("/0").GetValue<object>());
        Assert.IsType<double>(again.GetNode("/1").GetValue<object>());
    }

    [Theory]
    [InlineData("! #c\n")]
    [InlineData("!!null # c\n x\n")]
    public void Merging_into_a_tagged_empty_root_with_a_comment_keeps_the_member(string yaml)
    {
        using YamlDocument doc = Open(yaml);
        doc.ApplyMergePatch("{\"a\":1}");

        using YamlDocument again = Reread(doc);
        Assert.Equal(1, again.Get<int>("/a"));
    }

    [Fact]
    public void Replacing_a_tagged_root_that_has_a_comment_with_a_sequence_keeps_the_items()
    {
        using YamlDocument doc = Open("!!str #c\nr");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"\",\"value\":[1]}]");

        using YamlDocument again = Reread(doc);
        Assert.Equal(1, again.Get<int>("/0"));
    }

    [Fact]
    public void Replacing_the_value_of_an_explicit_empty_key_followed_by_a_colon_led_scalar_writes_valid_yaml()
    {
        using YamlDocument doc = Open("?\n  \n:*b: 1\n");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/\",\"value\":\"v\"}]");

        using YamlDocument again = Reread(doc);
        Assert.Equal("v", again.Get<string>("/"));
        Assert.Equal(1, again.Get<int>("/:*b"));
    }

    [Fact]
    public void Merging_into_an_explicit_key_mapping_followed_by_a_colon_led_scalar_writes_valid_yaml()
    {
        using YamlDocument doc = Open("? a\n:key:\tb\n -");
        doc.ApplyMergePatch("{\"a\":{}}");

        using YamlDocument again = Reread(doc);
        Assert.Equal(YamlNodeKind.Mapping, again.GetNode("/a").Kind);
    }

    [Fact]
    public void Replacing_the_value_of_a_key_whose_block_scalar_header_has_no_body_is_not_refused()
    {
        using YamlDocument doc = Open("a:\n>\nx1:");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/a\",\"value\":1}]");

        using YamlDocument again = Reread(doc);
        Assert.Equal(1, again.Get<int>("/a"));
    }

    [Fact]
    public void A_merge_into_the_first_document_stays_in_it_when_a_byte_order_mark_precedes_the_next_marker()
    {
        using YamlDocument doc = Open("?\n\uFEFF---\n ");
        Assert.Equal(2, doc.Documents.Count);
        doc.ApplyMergePatch("{\"a\":1}", document: 0);

        using YamlDocument again = Reread(doc);
        Assert.Equal(2, again.Documents.Count);
        Assert.True(again.Documents[0].ContainsKey("a"));
        Assert.False(again.Documents[1].ContainsKey("a"));
    }

    [Theory]
    [InlineData("a: |\n  old\nb: 1", "[{\"op\":\"remove\",\"path\":\"/b\"}]", "a: |\n  old\n")]
    [InlineData("a: |\n  o", "[{\"op\":\"add\",\"path\":\"/b\",\"value\":1}]", "a: |-\n  o\nb: 1")]
    [InlineData("a: |+\n  o", "[{\"op\":\"add\",\"path\":\"/b\",\"value\":1}]", "a: |-\n  o\nb: 1")]
    [InlineData("- !!float 1\n- x\n", "[{\"op\":\"copy\",\"path\":\"/1\",\"from\":\"/0\"}]", "- !!float 1\n- 1.0\n- x\n")]
    [InlineData("!!str #c\nr", "[{\"op\":\"replace\",\"path\":\"\",\"value\":[1]}]", " #c\n- 1")]
    [InlineData("?\n  \n:*b: 1\n", "[{\"op\":\"replace\",\"path\":\"/\",\"value\":\"v\"}]", "?\n: v\n  \n:*b: 1\n")]
    [InlineData("a:\n>\nx1:", "[{\"op\":\"replace\",\"path\":\"/a\",\"value\":1}]", "a:\n  1\nx1:")]
    public void Patches_write_exactly_these_bytes(string yaml, string patch, string expected)
    {
        using YamlDocument doc = Open(yaml);
        doc.ApplyPatch(patch);

        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Theory]
    [InlineData("a: # c\n- e\n", "[{\"op\":\"remove\",\"path\":\"/a/0\"}]", "a: # c\n  []\n")]
    [InlineData(" ? \n yes:\n   a: null\n", "[{\"op\":\"add\",\"path\":\"/\",\"value\":\"!t\"}]", " ? \n : '!t'\n yes:\n   a: null\n")]
    public void Patches_that_leave_an_empty_collection_or_a_value_for_an_empty_key_write_these_bytes(string yaml, string patch, string expected)
    {
        using YamlDocument doc = Open(yaml);
        doc.ApplyPatch(patch);

        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(doc.ToBytes()));
    }
}
