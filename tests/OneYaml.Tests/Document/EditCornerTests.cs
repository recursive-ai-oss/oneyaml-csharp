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

    // Each entry's own "?" goes with it or stays with it: one indicator is left in front of the next key.
    [Theory]
    [InlineData("{? a, ? : 1}", "{? : 1}")]
    [InlineData("{? a, ?}", "{?}")]
    [InlineData("{? a, ? , b}", "{? , b}")]
    [InlineData("{b: 1, ? a, ? : 2}", "{b: 1, ? : 2}")]
    [InlineData("{\n  ? a,\n  ? : 1\n}", "{\n  ? : 1\n}")]
    [InlineData("{\n  ? a\n  ,\n  ? : 1\n}", "{\n  ? : 1\n}")]
    [InlineData("k: {\n  ? a,\n  ? : 1}", "k: {\n  ? : 1}")]
    public void Removing_an_explicit_key_before_an_empty_explicit_key_in_a_flow_mapping_leaves_one_indicator(string yaml, string expected)
    {
        string path = yaml.StartsWith("k:", StringComparison.Ordinal) ? "/k/a" : "/a";
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"" + path + "\"}]"));

        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyMergePatch(path == "/a" ? "{\"a\":null}" : "{\"k\":{\"a\":null}}");
        Assert.Equal(expected, Encoding.UTF8.GetString(doc.ToBytes()));
    }

    // An empty key whose "?" is followed by a comment stays when the entry before it goes.
    [Theory]
    [InlineData("{a: 1, ?  # c\n}", "{?  # c\n}")]
    [InlineData("{\n a: 1\n ,?   # c\n , !t}", "{\n ?   # c\n , !t}")]
    public void An_empty_key_with_a_comment_after_its_question_mark_stays_when_the_entry_before_goes(string yaml, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"/a\"}]"));
    }

    // The "?" before the colon line belongs to the nested mapping's last key, not to the empty key of the outer one.
    [Fact]
    public void Removing_an_empty_key_that_follows_a_nested_mapping_ending_in_an_empty_key_is_an_edit_not_a_crash()
    {
        Assert.Equal("top:\n  a: 1\n  ?\n", Patch("top:\n  a: 1\n  ?\n: 9\n", "[{\"op\":\"remove\",\"path\":\"/\"}]"));
    }

    // A byte order mark that starts a later document is not indentation.
    [Theory]
    [InlineData("a: 1\n...\n\uFEFFa: |\n  t\n", "/n", "a: 1\n...\n\uFEFFa: |\n  t\nn: 1\n")]
    [InlineData("a: 1\n...\n\uFEFF- x\n", "/-", "a: 1\n...\n\uFEFF- x\n- 1\n")]
    [InlineData("a: 1\n...\n\uFEFFa: 1\n", "/n", "a: 1\n...\n\uFEFFa: 1\nn: 1\n")]
    public void An_add_to_a_document_that_starts_with_a_byte_order_mark_is_not_indented_by_it(string yaml, string path, string expected)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyPatch("[{\"op\":\"add\",\"path\":\"" + path + "\",\"value\":1}]", 1);
        string result = Encoding.UTF8.GetString(doc.ToBytes());

        Assert.Equal(expected, result);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(2, again.Documents.Count);
    }

    // Emptying a collection keeps the tag it has, also a scalar tag the collection never fitted.
    [Theory]
    [InlineData("!!str\na: 1\n", "/a", "!!str {}\n")]
    [InlineData("k: !!int\n  - 1\n", "/k/0", "k: !!int []\n")]
    public void Emptying_a_collection_keeps_its_tag(string yaml, string path, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"" + path + "\"}]"));
    }

    // A line of spaces after a replaced block scalar, wider than the new block scalar's indentation, would read as one
    // more line of the new text.
    [Theory]
    [InlineData(">\n   ", "a: >2\n    two\n    lines\n")]
    [InlineData("k: >\n     \nz: 1\n", "k:\n  a: >\n    two\n\nz: 1\n")]
    public void A_block_scalar_replaced_by_a_mapping_ending_in_a_block_scalar_does_not_absorb_a_line_of_spaces(string yaml, string expected)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        if (yaml.StartsWith('>'))
        {
            doc.ApplyMergePatch("{\"a\":\"  two\\n  lines\\n\"}");
        }
        else
        {
            doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/k\",\"value\":{\"a\":\"two\\n\"}}]");
        }

        string result = Encoding.UTF8.GetString(doc.ToBytes());
        Assert.Equal(expected, result);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(yaml.StartsWith('>') ? "  two\n  lines\n" : "two\n", again.Get<string>(yaml.StartsWith('>') ? "/a" : "/k/a"));
    }

    // The text had no final break and the entry before the removed one is a block scalar key with no value: that
    // key needs its final break to keep its value.
    [Theory]
    [InlineData("? |\n  k1\n? |\n  k2", "k2", "k1\n")]
    [InlineData("? |\n  k1\n? x", "x", "k1\n")]
    [InlineData("? |-\n  k1\n? |\n  k2", "k2", "k1")]
    public void Removing_the_last_entry_after_a_block_scalar_key_without_a_value_keeps_that_key_unchanged(string yaml, string remove, string key)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/" + remove + "\"}]");

        using YamlDocument again = YamlDocument.Parse(doc.ToBytes());
        YamlNode only = Assert.Single(again.Root.Entries).Key;
        Assert.Equal(key, only.GetValue<string>());
    }

    // The comment after a block scalar's header may be set apart by a tab; the new header keeps that blank.
    [Theory]
    [InlineData("wo: |2\t# c\n", "wo: |2-\t# c\n  123\n")]
    [InlineData("wo: >-\t # c\n", "wo: >-\t # c\n 123\n")]
    public void Replacing_a_block_scalar_whose_header_comment_follows_a_tab_keeps_the_comment_apart(string yaml, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"replace\",\"path\":\"/wo\",\"value\":\"123\"}]"));
    }

    // Blank lines after a kept block scalar would become its text. They go with the removed entry also when comment
    // lines stand between the entry and the blank lines.
    [Theory]
    [InlineData("keep: |+\nmmstrp:\n   #m\n\nz: 1\n", "keep: |+\nz: 1\n")]
    [InlineData("keep: |+\nmmstrp:\n\n   #m\n\n  #n\nz: 1\n", "keep: |+\nz: 1\n")]
    public void Removing_an_entry_after_a_kept_block_scalar_takes_the_comments_and_blank_lines_that_would_join_it(string yaml, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"/mmstrp\"}]"));

        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyMergePatch("{\"mmstrp\":null}");
        Assert.Equal(expected, Encoding.UTF8.GetString(doc.ToBytes()));
    }

    // The entries between a key with no value and a colon line are removed in one merge: the colon line must stay an
    // entry of its own and not become the value of the key before.
    [Theory]
    [InlineData("? k\nz: 1\ny: 2\n:  3\n", "{\"z\":null,\"y\":null}", "? k\n:\n:  3\n")]
    [InlineData("? ke? key:\n:a\tky:\n:\t-0.0 ", "{\"b\":null,\":a\\tky\":null}", "? ke? key:\n:\n:\t-0.0 ")]
    public void A_merge_removing_the_entries_before_a_colon_line_keeps_the_key_before_them_without_a_value(string yaml, string patch, string expected)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.ApplyMergePatch(patch);
        string result = Encoding.UTF8.GetString(doc.ToBytes());

        Assert.Equal(expected, result);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(2, again.Root.Entries.Count());
    }

    // An item that would start a line with "---" after the one before it is cut away reads as a document marker.
    [Theory]
    [InlineData("[\r1,---\n 2\n , 3]", "[\r ---\n 2\n , 3]")]
    [InlineData("{\ra: 1,---\n 2\n : 3}", "{\r ---\n 2\n : 3}")]
    public void Removing_the_first_item_of_a_flow_collection_does_not_leave_the_next_one_at_a_document_marker(string yaml, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"/" + (yaml[0] == '[' ? "0" : "a") + "\"}]"));
    }

    // A line of blanks with a tab in it is not allowed right after a block scalar, so it goes with the entry removed
    // from between them.
    [Theory]
    [InlineData("k: |\n:  \n\t\nk: ", "k: |\nk: ")]
    [InlineData("k: |\n: c\n a\n\n\t", "k: |\n")]
    public void Removing_an_entry_after_a_block_scalar_takes_a_following_line_with_a_tab_in_it(string yaml, string expected)
    {
        Assert.Equal(expected, Patch(yaml, "[{\"op\":\"remove\",\"path\":\"/\"}]"));
    }

    // Emptying a collection keeps a sequence or mapping tag that does not fit it.
    [Fact]
    public void Emptying_a_mapping_that_carries_a_sequence_tag_keeps_the_tag()
    {
        using YamlDocument doc = YamlDocument.Parse("--- !!seq\n-hree: 1\n");
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/-hree\"}]");

        Assert.Equal("--- !!seq {}\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }
}
