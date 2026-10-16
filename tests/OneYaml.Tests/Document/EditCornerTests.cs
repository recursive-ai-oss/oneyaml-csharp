using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Edits next to explicit keys, colon-led lines, whitespace-only last lines and empty flow keys.</summary>
public class EditCornerTests
{
    private static string Patch(string yaml, string patch)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml, new YamlDocumentOptions { AllowDuplicateKeys = true });
        doc.ApplyPatch(patch);
        string result = Encoding.UTF8.GetString(doc.ToBytes());

        // The edited text must read again.
        using YamlDocument again = YamlDocument.Parse(result, new YamlDocumentOptions { AllowDuplicateKeys = true });
        return result;
    }

    // A colon directly before text is a value indicator only inside a flow collection: ":x" on the line after
    // an explicit key of any style is a new plain key.
    [Theory]
    [InlineData("? 'a'\n:x: 1\n", "[{\"op\":\"replace\",\"path\":\"/a\",\"value\":2}]", "? 'a'\n: 2\n:x: 1\n")]
    [InlineData("? \"a\"\n:x: 1\n", "[{\"op\":\"replace\",\"path\":\"/a\",\"value\":2}]", "? \"a\"\n: 2\n:x: 1\n")]
    [InlineData("? 'gg'\n:hh: 17\n", "[{\"op\":\"remove\",\"path\":\"/gg\"}]", ":hh: 17\n")]
    [InlineData("? \"gg\"\n:hh: 17\n", "[{\"op\":\"remove\",\"path\":\"/gg\"}]", ":hh: 17\n")]
    [InlineData("? [a]\n:x: 1\n", "[{\"op\":\"remove\",\"path\":\"/:x\"}]", "? [a]\n")]
    [InlineData("? {a: 1}\n:x: 1\n", "[{\"op\":\"remove\",\"path\":\"/:x\"}]", "? {a: 1}\n")]
    public void A_colon_led_line_after_an_explicit_key_is_a_key_of_its_own(string yaml, string patch, string expected)
    {
        Assert.Equal(expected, Patch(yaml, patch));
    }

    [Fact]
    public void Merging_a_value_for_a_quoted_explicit_key_keeps_the_colon_led_key()
    {
        using YamlDocument doc = YamlDocument.Parse("? 'a'\n:x: 1\n");
        doc.ApplyMergePatch("{\"a\":5}");

        Assert.Equal("? 'a'\n: 5\n:x: 1\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void A_colon_that_touches_the_value_still_ends_a_key_in_a_flow_mapping()
    {
        Assert.Equal("{\"a\":2}", Patch("{\"a\":1}", "[{\"op\":\"replace\",\"path\":\"/a\",\"value\":2}]"));
    }

    [Fact]
    public void Removing_the_entry_after_a_literal_does_not_pull_a_whitespace_line_into_it()
    {
        Assert.Equal("a: |\n  old\n", Patch("a: |\n  old\nb: 1\n   ", "[{\"op\":\"remove\",\"path\":\"/b\"}]"));
    }

    [Fact]
    public void Removing_the_first_entry_of_a_flow_mapping_keeps_the_question_mark_of_an_empty_key_after_it()
    {
        Assert.Equal("{? , b: y}\n", Patch("{a, ? , b: y}\n", "[{\"op\":\"remove\",\"path\":\"/a\"}]"));
    }

    [Fact]
    public void Merge_patch_removing_the_first_entry_of_a_flow_mapping_keeps_an_empty_key_after_it()
    {
        using YamlDocument doc = YamlDocument.Parse("{a, ? , b: y}\n");
        doc.ApplyMergePatch("{\"a\":null}");

        Assert.Equal("{? , b: y}\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Theory]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/\",\"value\":\"True\"}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/\",\"value\":\"True\"}]")]
    public void A_value_for_a_tagged_empty_key_in_a_flow_mapping_is_set_apart_from_the_tag(string patch)
    {
        string result = Patch("{!<t>}", patch);

        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("True", again.Get<string>("/"));
    }

    [Fact]
    public void A_copied_float_tag_keeps_its_own_spelling()
    {
        Assert.Equal("a: !!float 017\nb: !!float 017\n", Patch("a: !!float 017\nb: x\n", "[{\"op\":\"copy\",\"path\":\"/b\",\"from\":\"/a\"}]"));
    }

    [Fact]
    public void A_copied_float_over_a_tagged_float_does_not_get_a_second_tag()
    {
        Assert.Equal("a: !!float 1\nb: !!float 1\n", Patch("a: !!float 1\nb: !!float 2\n", "[{\"op\":\"copy\",\"path\":\"/b\",\"from\":\"/a\"}]"));
    }

    // The colon-led line is a key of its own, so removing it takes that entry and no other.
    [Fact]
    public void Removing_a_key_made_of_colons_keeps_the_entries_around_it()
    {
        string result = Patch(
            "? [a, b>]\n::  v%YA b>]\n: v%,YAML 1.2ML 1.2",
            "[{\"op\":\"replace\",\"path\":\"/\",\"value\":{}},{\"op\":\"copy\",\"path\":\"//\",\"from\":\"/\"},{\"op\":\"remove\",\"path\":\"/:\"}]");

        Assert.Equal("? [a, b>]\n:\n: {'': {}}", result);
    }

    [Fact]
    public void Removing_an_entry_between_a_bare_explicit_key_and_a_colon_line_with_comments_keeps_both_entries()
    {
        using YamlDocument doc = YamlDocument.Parse("---\n? key b\n\nase: &b\n\n  # c\n: v\n");
        doc.ApplyMergePatch("{\"ase\":null}");

        Assert.Equal("---\n? key b\n\n:\n\n  # c\n: v\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }
}
