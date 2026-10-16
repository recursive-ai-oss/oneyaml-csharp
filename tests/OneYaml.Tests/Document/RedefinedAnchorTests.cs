using Xunit;

namespace OneYaml.Tests;

/// <summary>An alias means the node whose anchor was written last before it, even when an enclosing node with the same name ends later.</summary>
public class RedefinedAnchorTests
{
    // The inner node is written after the outer anchor and the alias after both.
    private const string Nested = "a: &b\n se: &b\n   1\ncopy: *b\n";
    private const string Flow = "[&b [&b 1, *b], *b]\n";

    private sealed class Typed
    {
        public Dictionary<string, long>? A { get; set; }
        public long Copy { get; set; }
    }

    [Fact]
    public void Generic_load_resolves_to_the_inner_node()
    {
        var map = (Dictionary<string, object?>)YamlSerializer.Deserialize<object>(Nested)!;
        Assert.Equal(1L, map["copy"]);

        var seq = (List<object?>)YamlSerializer.Deserialize<object>(Flow)!;
        Assert.Equal(1L, seq[1]);
        Assert.Equal(new object?[] { 1L, 1L }, ((List<object?>)seq[0]!).ToArray());
    }

    [Fact]
    public void Typed_load_resolves_to_the_inner_node()
    {
        Typed t = YamlSerializer.Deserialize<Typed>(Nested)!;
        Assert.Equal(1L, t.Copy);
        Assert.Equal(1L, t.A!["se"]);
    }

    [Fact]
    public void Document_reads_resolve_to_the_inner_node()
    {
        var doc = YamlDocument.Parse(Nested);
        Assert.Equal(1L, doc.Get<long>("/copy"));
        Assert.Equal(1L, doc.Root["copy"].Deserialize<long>());
    }

    [Fact]
    public void Patch_copy_move_and_test_expand_the_inner_node()
    {
        var doc = YamlDocument.Parse(Nested);
        doc.ApplyPatch("[{\"op\":\"test\",\"path\":\"/copy\",\"value\":1},{\"op\":\"copy\",\"path\":\"/z\",\"from\":\"/copy\"}]");
        Assert.Equal(1L, doc.Get<long>("/z"));

        var moved = YamlDocument.Parse(Nested);
        moved.ApplyPatch("[{\"op\":\"move\",\"path\":\"/z\",\"from\":\"/copy\"}]");
        Assert.Equal(1L, moved.Get<long>("/z"));
    }

    [Fact]
    public void Merge_keys_use_the_inner_node()
    {
        var options = new YamlSerializerOptions { MergeKeys = true };
        var v = YamlSerializer.Deserialize<Dictionary<string, object?>>("o: &m {x: &m {y: 1}}\nuse: {<<: *m}\n", options)!;
        var use = (Dictionary<string, object?>)v["use"]!;
        Assert.Equal("y", Assert.Single(use.Keys));
    }
}

/// <summary>An alias inside the node its own anchor names is a cycle, which a load cannot represent; it is refused, not read as an older definition of the name.</summary>
public class AliasInsideItsOwnNodeTests
{
    // The second anchor reuses the name of the first, and the alias inside it names the node that holds it.
    public static TheoryData<string> Texts => new()
    {
        "- &a 1\n- &a [*a]\n",
        "- &a 1\n- &a\n  - *a\n",
        "- &a 1\n- &a {k: *a}\n",
        "- &a [&a 1, [*a]]\n- &a [*a]\n",
        "&a [*a]\n",
    };

    private static void AssertSyntax(Action read)
    {
        YamlException ex = Assert.Throws<YamlException>(read);
        Assert.Equal(YamlErrorCategory.Syntax, ex.Category);
        Assert.Contains("contains it", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Texts))]
    public void Generic_and_typed_loads_refuse_it(string yaml)
    {
        AssertSyntax(() => YamlSerializer.Deserialize<object>(yaml));
        AssertSyntax(() => YamlSerializer.Deserialize<List<object?>>(yaml));
    }

    [Theory, MemberData(nameof(Texts))]
    public void A_document_refuses_it_when_it_is_opened(string yaml)
    {
        AssertSyntax(() => YamlDocument.Parse(yaml));
    }

    [Fact]
    public void An_alias_after_the_enclosing_node_is_complete_still_means_it()
    {
        var seq = (List<object?>)YamlSerializer.Deserialize<object>("- &a 1\n- &a [2]\n- *a\n")!;
        Assert.Equal(new object?[] { 2L }, ((List<object?>)seq[2]!).ToArray());
    }
}

/// <summary>An entry added after a block scalar that ends the text must not change the scalar's value.</summary>
public class AddAfterKeyBlockScalarTests
{
    [Theory]
    [InlineData("? x: |\n    o", "? x: |-\n    o\nb: 1")]
    [InlineData("? - |\n   o", "? - |-\n   o\nb: 1")]
    [InlineData("? ? x: >\n      o", "? ? x: >-\n      o\nb: 1")]
    public void A_block_scalar_inside_an_explicit_key_keeps_its_value(string text, string expected)
    {
        var doc = YamlDocument.Parse(text);
        doc.ApplyPatch("[{\"op\":\"add\",\"path\":\"/b\",\"value\":1}]");
        Assert.Equal(expected, doc.ToString());
    }
}

/// <summary>A block scalar with no text of its own, followed by lines of spaces, keeps its place when the value is replaced.</summary>
public class ReplaceEmptyBlockScalarTests
{
    [Theory]
    [InlineData("k: >\n          ", "k: >-\n  null\n")]
    [InlineData("k: |\n          \nz: 1\n", "k: |-\n  null\nz: 1\n")]
    public void Spaces_that_were_not_text_do_not_become_text(string text, string expected)
    {
        var doc = YamlDocument.Parse(text);
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/k\",\"value\":\"null\"}]");
        Assert.Equal(expected, doc.ToString());
        Assert.Equal("null", YamlDocument.Parse(doc.ToString()).Get<string>("/k"));
    }
}

/// <summary>Edits in flow mappings where the old value touched the colon.</summary>
public class FlowValueSpacingTests
{
    [Theory]
    [InlineData("{a:{}}", "{\"a\":5}", "{a: 5}")]
    [InlineData("{a:[1]}", "{\"a\":\"s\"}", "{a: s}")]
    [InlineData("{a:{b: 1}, c: 2}", "{\"a\":true}", "{a: true, c: 2}")]
    [InlineData("{a:{}}", "{\"a\":[2]}", "{a:[2]}")]
    [InlineData("{\"a\":{}}", "{\"a\":2}", "{\"a\":2}")]
    public void A_replaced_value_does_not_join_the_key(string text, string mergePatch, string expected)
    {
        var doc = YamlDocument.Parse(text);
        doc.ApplyMergePatch(mergePatch);
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void A_patch_replace_keeps_the_key_apart_from_its_value()
    {
        var doc = YamlDocument.Parse("{a:{}}");
        doc.ApplyPatch("[{\"op\":\"replace\",\"path\":\"/a\",\"value\":5}]");
        Assert.Equal(5L, doc.Get<long>("/a"));
    }
}

/// <summary>A colon at the margin after a block scalar key belongs to the mapping around, not to that key.</summary>
public class BlockKeyThenMarginColonTests
{
    [Theory]
    [InlineData("c:\n  ? >\n   a\n: 1\n", "c: {}\n: 1\n")]
    [InlineData("c:\n  b: 1\n  ? |\n   a\n: 1\n", "c:\n  b: 1\n: 1\n")]
    public void Removing_the_key_keeps_the_margin_entry(string text, string expected)
    {
        var doc = YamlDocument.Parse(text);
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/c/a\\n\"}]");
        Assert.Equal(expected, doc.ToString());
    }
}
