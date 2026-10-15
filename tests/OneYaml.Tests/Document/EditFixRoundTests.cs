using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Replacements next to blank lines, removals next to block scalars and comments, first writes to a
/// document that is only a marker, and float keys that a double cannot hold.
/// </summary>
public class EditFixRoundTests
{
    private static string Edit(string yaml, Action<YamlDocument> edit)
    {
        byte[] spliced = Run(yaml, edit, splice: true);
        Assert.Equal(Run(yaml, edit, splice: false), spliced);
        return Encoding.UTF8.GetString(spliced);
    }

    private static byte[] Run(string yaml, Action<YamlDocument> edit, bool splice)
    {
        DocState.NoSplice = !splice;
        try
        {
            using YamlDocument doc = YamlDocument.Parse(yaml);
            edit(doc);
            return doc.ToBytes();
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    private static List<object?> Items(params object?[] items) => [.. items];

    // ---- A block scalar replaced by a list: only a kept last value is written another way ----

    [Theory]
    [InlineData("a: |\n  x\n\nb: 1\n", "a:\n  - |\n    make\n\nb: 1\n")]
    [InlineData("a: |-\n  x\n\nb: 1\n", "a:\n  - |\n    make\n\nb: 1\n")]
    [InlineData("a: >\n  x\n\nb: 1\n", "a:\n  - >\n    make\n\nb: 1\n")]
    public void A_clipped_string_in_a_list_keeps_the_block_style_before_blank_lines(string yaml, string expected)
    {
        Assert.Equal(expected, Edit(yaml, d => d.Set("/a", Items("make\n"))));
    }

    [Fact]
    public void The_literal_stays_a_literal_for_a_list_of_two_strings_before_blank_lines()
    {
        string result = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", Items("make\n", "test\n")));
        Assert.Equal("a:\n  - |\n    make\n  - |\n    test\n\nb: 1\n", result);
    }

    [Fact]
    public void A_single_list_item_with_a_break_inside_keeps_the_literal()
    {
        string result = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", Items("a\nb")));
        Assert.Equal("a:\n  - |-\n    a\n    b\n\nb: 1\n", result);
    }

    [Fact]
    public void A_mapping_value_with_lines_keeps_the_literal_before_blank_lines()
    {
        string result = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", new Dictionary<string, object?> { ["k"] = "l1\nl2\n" }));
        Assert.Equal("a:\n  k: |\n    l1\n    l2\n\nb: 1\n", result);
    }

    [Fact]
    public void Only_the_last_string_that_would_be_kept_is_quoted_before_blank_lines()
    {
        string first = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", Items("x\n\n", "make\n")));
        Assert.Equal("a:\n  - |+\n    x\n\n  - |\n    make\n\nb: 1\n", first);

        string last = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", Items("make\n", "x\n\n")));
        using YamlDocument again = YamlDocument.Parse(last);
        Assert.Equal("x\n\n", again.Get<string>("/a/1"));
        Assert.Equal("make\n", again.Get<string>("/a/0"));
        Assert.StartsWith("a:\n  - |\n    make\n  - ", last, StringComparison.Ordinal);
        Assert.EndsWith("\n\nb: 1\n", last, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_parent_keeps_the_literal_before_blank_lines()
    {
        string result = Edit("p:\n  a: |\n    x\n\nb: 1\n", d => d.Set("/p/a", Items("make\n")));
        Assert.Equal("p:\n  a:\n    - |\n      make\n\nb: 1\n", result);
    }

    // ---- Float keys beyond the double range ----

    [Theory]
    [InlineData("{1e400: a, 10e399: b}")]
    [InlineData("{1e400: a, 1e+400: b}")]
    [InlineData("{1e400: a, 1.0e400: b}")]
    [InlineData("{1e400: a, 01e400: b}")]
    [InlineData("{1e400: a, 1E400: b}")]
    [InlineData("{-1e400: a, -10e399: b}")]
    [InlineData("{1e-400: a, 10e-401: b}")]
    [InlineData("{0.0: a, -0.0: b}")]
    public void The_same_float_value_twice_is_a_duplicate_key_in_both_readers(string yaml)
    {
        YamlException doc = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.DuplicateKey, doc.Category);
        YamlException generic = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(YamlErrorCategory.DuplicateKey, generic.Category);
    }

    [Theory]
    [InlineData("{1e400: a, 2e400: b}")]
    [InlineData("{1e400: a, -1e400: b}")]
    [InlineData("{1e400: a, .inf: b}")]
    [InlineData("{1e-400: a, 2e-400: b}")]
    [InlineData("{1e-400: a, 0.0: b}")]
    [InlineData("{1e-400: a, 1e400: b}")]
    public void Different_float_values_are_two_keys_in_both_readers(string yaml)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(2, doc.Root.Count);
        var generic = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(2, generic.Count);
    }

    // ---- Removing an entry after a block scalar with a more indented comment ----

    [Fact]
    public void Removing_an_entry_after_a_literal_takes_its_more_indented_comment_along()
    {
        string result = Edit("a:\n  k: |\n    v\n  x: 1\n    # c\nb: 2\n", d => d.Remove("/a/x"));
        Assert.Equal("a:\n  k: |\n    v\nb: 2\n", result);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("v\n", again.Get<string>("/a/k"));
    }

    [Fact]
    public void Removing_an_entry_after_a_folded_scalar_takes_comments_and_blank_lines_between_them()
    {
        string result = Edit("a:\n  k: >\n    v\n  x: 1\n    # c\n\n    # d\nb: 2\n", d => d.Remove("/a/x"));
        Assert.Equal("a:\n  k: >\n    v\nb: 2\n", result);
    }

    [Fact]
    public void A_comment_less_indented_than_the_scalar_stays()
    {
        string result = Edit("a:\n  k: |\n    v\n  x: 1\n  # c\nb: 2\n", d => d.Remove("/a/x"));
        Assert.Equal("a:\n  k: |\n    v\n  # c\nb: 2\n", result);
    }

    [Fact]
    public void Removing_an_entry_after_a_keep_scalar_leaves_a_more_indented_comment_out_of_the_scalar()
    {
        string result = Edit("a:\n  k: |+\n    v\n  x: 1\n    # c\nb: 2\n", d => d.Remove("/a/x"));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("v\n", again.Get<string>("/a/k"));
        Assert.Equal(2L, again.Get<long>("/b"));
        Assert.Equal(1, again.Root["a"].Count);
    }

    // ---- A document that is only a marker ----

    [Fact]
    public void Adding_to_a_marker_with_a_comment_keeps_the_comment_on_the_marker_line()
    {
        Assert.Equal("--- # c\na: 1\n", Edit("--- # c\n", d => d.Set("/a", 1)));
        Assert.Equal("--- # c\na: 1\n", Edit("--- # c\n", d => d.ApplyMergePatch("{\"a\":1}")));
    }

    [Fact]
    public void A_merge_patch_on_a_tagged_empty_marker_adds_no_blank_line()
    {
        string result = Edit("--- !!map\n", d => d.ApplyMergePatch("{\"a\":1}"));
        Assert.Equal("--- !!map\na: 1\n", result);
    }

    // ---- Removals around block scalars that end in blank lines and a less indented comment ----

    [Theory]
    [InlineData("a: 1\nkey2: |-\n   text\n\n  # c\nz: 1\n", "/a")]
    [InlineData("a: 1\nkey2: |-\n   text\n\n  # c\nz: 1\n", "/z")]
    [InlineData("a: 1\nkey2: |-\n   text\n\n  # c\nz: 1\n", "/key2")]
    [InlineData("k: |\n  text\n  # c\n\n# d\nz: 1\nw: 2\n", "/z")]
    [InlineData("k: |\n  text\n  # c\n\n# d\nz: 1\nw: 2\n", "/w")]
    [InlineData("o:\n  k: |-\n     text\n\n    # c\n  z: 1\n    # e\n  y: 2\n", "/o/z")]
    [InlineData("o:\n  k: |-\n     text\n\n    # c\n  z: 1\n    # e\n  y: 2\n", "/o/y")]
    public void Removing_an_entry_next_to_a_block_scalar_with_trailing_comments_reads_like_a_fresh_parse(string yaml, string at)
    {
        string byRemove = Edit(yaml, d => d.Remove(at));
        using YamlDocument again = YamlDocument.Parse(byRemove);
        Assert.False(again.TryGet<object>(at, out _));
        string name = at[(at.LastIndexOf('/') + 1)..];
        string path = at.Contains("/o/", StringComparison.Ordinal) ? "/o/" + name : "/" + name;
        Edit(yaml, d => d.ApplyMergePatch(path.StartsWith("/o/", StringComparison.Ordinal) ? "{\"o\":{\"" + name + "\":null}}" : "{\"" + name + "\":null}"));
    }

    // ---- CR line breaks ----

    [Fact]
    public void A_removal_before_a_comment_block_with_carriage_returns_reads_like_a_fresh_parse()
    {
        string result = Edit("a: 1\rb: |\r  x\r  # c\r\r# d\rz: 1\r", d => d.Remove("/a"));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("x\n# c\n", again.Get<string>("/b"));
    }
}
