using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Edits next to block scalars, blank lines and comments; moves under tight limits; first writes to an
/// empty stream. Every state an edit makes is also compared with a fresh parse by the check in
/// <see cref="SpliceCheck"/>, so a state that reads differently from its own bytes fails here.
/// </summary>
public class EditBoundaryTests
{
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

    // The edit gives the same bytes with and without incremental parsing, and the bytes read back.
    private static string Edit(string yaml, Action<YamlDocument> edit)
    {
        byte[] spliced = Run(yaml, edit, splice: true);
        Assert.Equal(Run(yaml, edit, splice: false), spliced);
        return Encoding.UTF8.GetString(spliced);
    }

    private static List<object?> Items(params object?[] items) => [.. items];

    // ---- A block scalar replaced by a collection that holds a string ending in blank lines ----

    [Theory]
    [InlineData("a: |\n  x\n\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: |-\n  x\n\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: >\n  x\n\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: |\n  x\n  \n\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: |+\n  x\n\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: |\n  x\n\n# note\nb: 1\n", "/a", "/a/0", true)]
    [InlineData("a: |\n  x\n\n", "/a", "/a/0", true)]
    [InlineData("- |\n  x\n\n- z\n", "/0", "/0/0", true)]
    [InlineData("a: |\n  x\n\nb: 1\n", "/a", "/a/k", false)]
    public void A_string_ending_in_blank_lines_keeps_its_value_when_blank_lines_follow(string yaml, string at, string read, bool asList)
    {
        object value = asList ? Items("body\n\n") : new Dictionary<string, object?> { ["k"] = "body\n\n" };
        string result = Edit(yaml, d => d.Set(at, value));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("body\n\n", again.Get<string>(read));

        // Whatever came after the old scalar's text is still there, byte for byte.
        string tail = yaml[(yaml.IndexOf("x\n", StringComparison.Ordinal) + 2)..];
        if (!yaml.Contains("|+", StringComparison.Ordinal))
        {
            Assert.EndsWith(tail, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_following_key_is_not_changed_by_the_replacement()
    {
        string result = Edit("a: |\n  x\n\nb: 1\n", d => d.Set("/a", Items("body\n\n")));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(1L, again.Get<long>("/b"));
    }

    // ---- Removing an entry next to a kept block scalar ----

    [Fact]
    public void Removing_an_entry_after_a_keep_scalar_does_not_let_the_scalar_take_the_blank_line()
    {
        string result = Edit("a:\n  k: |+\n    v\n  x: 1\n  \nb: 2\n", d => d.Remove("/a/x"));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("v\n", again.Get<string>("/a/k"));
        Assert.Equal(2L, again.Get<long>("/b"));
    }

    [Fact]
    public void Removing_an_entry_after_a_keep_scalar_leaves_a_more_indented_comment_out_of_the_scalar()
    {
        string result = Edit("a:\n  k: |+\n    v\n  x: 1\n    # c\nb: 2\n", d => d.Remove("/a/x"));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(2L, again.Get<long>("/b"));
        Assert.Equal(1, again.Root["a"].Count);
    }

    [Fact]
    public void Removing_then_adding_in_one_patch_reads_like_a_fresh_parse()
    {
        string patch = "[{\"op\":\"remove\",\"path\":\"/a/x\"},{\"op\":\"add\",\"path\":\"/a/z\",\"value\":1}]";
        string result = Edit("a:\n  k: |+\n    v\n  x: 1\n  \nb: 2\n", d => d.ApplyPatch(patch));
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("v\n", again.Get<string>("/a/k"));
        Assert.Equal(1L, again.Get<long>("/a/z"));
    }

    [Fact]
    public void Replacing_a_block_scalar_by_a_mapping_and_then_adding_a_member_reads_like_a_fresh_parse()
    {
        string result = Edit("s: |-\n  x\n  \nb: 1\n", d =>
        {
            d.Set("/s", new Dictionary<string, object?> { ["k"] = "v\n\n" });
            d.Set("/s/z", 1);
        });
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("v\n\n", again.Get<string>("/s/k"));
        Assert.Equal(1L, again.Get<long>("/s/z"));
        Assert.Equal(1L, again.Get<long>("/b"));
    }

    // ---- Moves are not copies ----

    private static string BigList()
    {
        var sb = new StringBuilder("a:\n");
        for (int i = 0; i < 60000; i++)
        {
            sb.Append("  - ").Append(i).Append('\n');
        }

        return sb.Append("c: 1\n").ToString();
    }

    [Fact]
    public void Repeated_moves_of_a_large_subtree_are_not_charged_as_copies()
    {
        string yaml = BigList();
        string patch = "[" + string.Join(",", Enumerable.Range(0, 4).Select(i => i % 2 == 0
            ? "{\"op\":\"move\",\"from\":\"/a\",\"path\":\"/b\"}"
            : "{\"op\":\"move\",\"from\":\"/b\",\"path\":\"/a\"}")) + "]";
        using YamlDocument doc = YamlDocument.Parse(yaml, new YamlDocumentOptions { Limits = YamlLimits.Untrusted });
        doc.ApplyPatch(patch);
        Assert.Equal(59999L, doc.Get<long>("/a/59999"));
        Assert.Equal(1L, doc.Get<long>("/c"));
    }

    [Fact]
    public void Copies_of_a_large_subtree_are_still_limited_and_say_so()
    {
        string yaml = BigList();
        string patch = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"op\":\"copy\",\"from\":\"/a\",\"path\":\"/b" + i + "\"}")) + "]";
        using YamlDocument doc = YamlDocument.Parse(yaml, new YamlDocumentOptions { Limits = YamlLimits.Untrusted });
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(patch));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Contains("The patch copies more than the configured limit.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(yaml, doc.ToString());
    }

    // ---- The first write to a stream with no content ----

    [Theory]
    [InlineData("add", "/a", "a: 1\n")]
    [InlineData("add", "/-", "- 1\n")]
    [InlineData("add", "/0", "- 1\n")]
    [InlineData("replace", "/a", null)]
    public void A_patch_can_make_the_first_member_or_item_of_an_empty_stream(string op, string path, string? expected)
    {
        using YamlDocument doc = YamlDocument.Parse(string.Empty);
        string patch = "[{\"op\":\"" + op + "\",\"path\":\"" + path + "\",\"value\":1}]";
        if (expected is null)
        {
            Assert.Throws<YamlException>(() => doc.ApplyPatch(patch));
            Assert.Equal(string.Empty, doc.ToString());
            return;
        }

        doc.ApplyPatch(patch);
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void Pointer_set_and_index_assignment_agree_on_an_empty_stream()
    {
        using YamlDocument byIndex = YamlDocument.Parse(string.Empty);
        byIndex.Root[0] = 1;
        using YamlDocument byPointer = YamlDocument.Parse(string.Empty);
        byPointer.Set("/-", 1);
        Assert.Equal(byIndex.ToString(), byPointer.ToString());
    }

    // ---- Properties on a line of their own are dropped with the line ----

    [Fact]
    public void Dropping_properties_that_fill_their_own_line_removes_the_line()
    {
        string result = Edit("a: # c\n  !!map\n  b: 1\nc: 2\n", d => d.Set("/a", 5));
        Assert.DoesNotContain("\n\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("!!map", result, StringComparison.Ordinal);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal(5L, again.Get<long>("/a"));
        Assert.Equal(2L, again.Get<long>("/c"));
    }

    // ---- Floats beyond the double range are different keys ----

    [Fact]
    public void Two_finite_floats_beyond_the_double_range_are_different_keys()
    {
        using YamlDocument doc = YamlDocument.Parse("{1e400: a, 2e400: b}\n");
        Assert.Equal(2, doc.Root.Count);
    }

    [Theory]
    [InlineData("{1e400: a, 1E400: b}\n")]
    [InlineData("{.inf: a, .INF: b}\n")]
    public void The_same_float_written_twice_is_a_duplicate_key(string yaml)
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }
}
