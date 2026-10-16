using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Inputs a second round of patch fuzzing found: each edit must give the exact bytes and read back as the same data.
/// </summary>
public class FuzzPatchRegressionTests
{
    private static YamlDocument Open(string yaml) => YamlDocument.Parse(yaml, new YamlDocumentOptions { AllowDuplicateKeys = true });

    private static string Bytes(YamlDocument doc) => Encoding.UTF8.GetString(doc.ToBytes());

    [Fact]
    public void Replacing_a_block_scalar_at_the_end_of_the_stream_with_a_mapping_keeps_a_multi_line_value_exact()
    {
        using YamlDocument doc = Open("a: |\n  x");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/a\",\"value\":{\"1\":\"m\\nl\\n\"}}]");

        Assert.Equal("a:\n  '1': |\n    m\n    l\n", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.Equal("m\nl\n", again.Get<string>("/a/1"));
    }

    [Fact]
    public void Removing_an_entry_between_a_bare_explicit_key_and_a_colon_only_line_keeps_the_entries_apart()
    {
        using YamlDocument doc = Open("? \nk: 1\n: 2\n");
        int before = doc.Root.Count;
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/k\"}]");

        Assert.Equal("? \n:\n: 2\n", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes(), new YamlDocumentOptions { AllowDuplicateKeys = true });
        Assert.Equal(before - 1, again.Root.Count);
    }

    [Fact]
    public void Moving_the_empty_key_of_an_indented_explicit_key_with_a_comment_keeps_the_document()
    {
        using YamlDocument doc = Open("  ? # c\n");
        doc.ApplyPatch("[{\"op\":\"move\",\"path\":\"/\u00E9\",\"from\":\"/\"}]");

        Assert.Equal("  \u00E9: null\n", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.True(again.Root.ContainsKey("\u00E9"));
    }

    [Fact]
    public void Removing_an_empty_key_that_has_a_comment_after_its_mark_keeps_the_next_entry()
    {
        using YamlDocument doc = Open("  ? # c\n  x: null\n");
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/\"}]");

        Assert.Equal("  x: null\n", Bytes(doc));
    }

    [Fact]
    public void A_second_patch_after_replacing_the_root_of_a_colon_only_stream_does_not_throw()
    {
        using YamlDocument doc = Open(":\n:\n  ? skn\n:");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"\",\"value\":{\"a\":\"b\"}},{\"op\":\"add\",\"path\":\"/k\",\"value\":1}]");

        Assert.Equal("a: b\nk: 1", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.Equal(1, again.Get<int>("/k"));
    }

    [Fact]
    public void Replacing_a_stripped_folded_root_with_a_mapping_keeps_a_value_with_an_indented_line_and_a_final_break()
    {
        using YamlDocument doc = Open(">-\naoo tr");
        doc.ApplyPatch("[{\"op\":\"add\",\"path\":\"\",\"value\":{\"1\":\"a\\n b\\n\"}}]");

        Assert.Equal("'1': >\n  a\n   b\n", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.Equal("a\n b\n", again.Get<string>("/1"));
    }

    [Fact]
    public void Removing_the_entry_of_a_flow_mapping_with_a_comma_led_line_keeps_valid_yaml()
    {
        using YamlDocument doc = Open("{\n r,r ::!r,\n  !\n,\n}\n");
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/\"}]");

        Assert.Equal("{\n r,r ::!r\n,\n}\n", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.False(again.Root.ContainsKey(""));
    }

    [Fact]
    public void Adding_an_entry_after_a_block_scalar_key_that_ends_the_stream_keeps_the_key()
    {
        using YamlDocument doc = Open("? x\n? |\n  block k");
        doc.ApplyPatch("[{\"op\":\"add\",\"path\":\"/k\",\"value\":false}]");

        Assert.Equal("? x\n? |-\n  block k\nk: false", Bytes(doc));
        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        Assert.True(again.Root.ContainsKey("block k"));
    }
}
