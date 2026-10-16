using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Work that must not grow with hostile input: the hint on a pointer error, try-reads that miss, and the
/// text of errors. Also the rules for values outside the plain model.
/// </summary>
public class DocumentCostAndMessageTests
{
    public sealed class Pair
    {
        public int Left { get; set; }
        public int Right { get; set; }
    }

    private static string Pointer(int tokens) => string.Concat(Enumerable.Repeat("/a", tokens));

    [Fact]
    public void A_pointer_of_many_tokens_costs_one_pass_when_a_key_is_missing()
    {
        using YamlDocument doc = YamlDocument.Parse("x: 1\nb: {c: 1}\n", new YamlDocumentOptions { Limits = YamlLimits.Untrusted });
        string pointer = "/b" + Pointer(20_000);
        Assert.Throws<YamlException>(() => doc.Remove(pointer));

        long before = GC.GetAllocatedBytesForCurrentThread();
        YamlException ex = Assert.Throws<YamlException>(() => doc.Remove(pointer));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(YamlErrorCategory.PatchPath, ex.Category);
        // Every token used to rebuild the joined key and scan the mapping: hundreds of megabytes here.
        Assert.True(allocated < 2_000_000, $"allocated {allocated:N0} bytes");
        Assert.True(ex.Message.Length < 1000, $"the message has {ex.Message.Length} characters");
        Assert.Contains("…", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slash_hint_still_names_the_working_pointer()
    {
        using YamlDocument doc = YamlDocument.Parse("m:\n  a/b/c: 1\n");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<int>("/m/a/b/c"));
        Assert.Contains("a/b/c", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/m/a~1b~1c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_from_a_pointer_in_a_message_is_short_and_on_one_line()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        string token = "ok\n2026-01-01 INFO forged " + new string('k', 5000);
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<int>("/" + token));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 1000, $"the message has {ex.Message.Length} characters");
    }

    [Fact]
    public void A_try_read_that_misses_builds_no_error()
    {
        string yaml = "items:\n" + string.Concat(Enumerable.Range(0, 5000).Select(i => $"  - text{i}\n"));
        using YamlDocument doc = YamlDocument.Parse(yaml);
        YamlNode items = doc.Root["items"];
        Assert.False(items[0].TryGetValue(out int _));
        Assert.False(doc.TryGet("/items/1", out bool _));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++)
        {
            Assert.False(items[i].TryGetValue(out int _));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // An error costs a line-and-column lookup over the text before the node, so the old path allocated
        // kilobytes per miss and grew with the document.
        Assert.True(allocated < 5000 * 300L, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Try_reads_still_answer_for_numbers_and_booleans()
    {
        using YamlDocument doc = YamlDocument.Parse("i: 7\nf: 1.5\nb: true\ns: hello\nq: '7'\nn: null\nbig: 99999999999\n");
        Assert.True(doc.TryGet("/i", out int i) && i == 7);
        Assert.True(doc.TryGet("/i", out double d) && d == 7);
        Assert.True(doc.TryGet("/f", out double f) && f == 1.5);
        Assert.False(doc.TryGet("/f", out int _));
        Assert.True(doc.TryGet("/b", out bool b) && b);
        Assert.False(doc.TryGet("/s", out int _));
        Assert.False(doc.TryGet("/q", out int _));
        Assert.False(doc.TryGet("/i", out bool _));
        Assert.False(doc.TryGet("/big", out int _));
        Assert.True(doc.TryGet("/big", out long big) && big == 99999999999L);
        Assert.True(doc.TryGet("/s", out string? s) && s == "hello");
        Assert.True(doc.TryGet("/n", out int? none) && none is null);
        Assert.False(doc.TryGet("/n", out int _));
    }

    [Fact]
    public void A_value_outside_the_plain_model_is_refused_when_reflection_is_off()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => TypedValues.OfObject(new Pair { Left = 1, Right = 2 }, YamlLimits.Default, reflectionEnabled: false));
        Assert.Contains("YamlTypeInfo", ex.Message, StringComparison.Ordinal);

        // The same value goes through reflection when the switch is on.
        ValueTree tree = TypedValues.OfObject(new Pair { Left = 1, Right = 2 }, YamlLimits.Default, reflectionEnabled: true);
        using YamlDocument doc = YamlDocument.Parse("x: 0\n");
        doc.Set("/x", new Pair { Left = 1, Right = 2 });
        Assert.Equal("x:\n  left: 1\n  right: 2\n", doc.ToString());
        Assert.NotNull(tree);
    }

    [Fact]
    public void Plain_values_never_need_reflection()
    {
        using YamlDocument doc = YamlDocument.Parse("x: 0\n");
        doc.Set("/x", new Dictionary<string, object?> { ["a"] = 1, ["b"] = new List<object?> { "s", true, 2.5 } });
        Assert.Equal(1, doc.Get<int>("/x/a"));
    }

    [Fact]
    public void A_long_integer_key_limit_error_points_at_the_key()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxIntegerDigits = 5 } };
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("a: 1\n1234567: x\n7654321: y\n", options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public void A_repeated_key_in_a_document_reads_like_the_binders_error()
    {
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse("top:\n  a: 1\n  b: 2\n  a: 3\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.Equal("/top/a", ex.Path);
        Assert.Contains("The key 'a' appears twice", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4, ex.Line);
    }

    [Fact]
    public void An_error_in_a_written_value_does_not_report_a_position_in_hidden_text()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxDepth = 5 } };
        using YamlDocument doc = YamlDocument.Parse("x: 0\n", options);
        object deep = new Dictionary<string, object?>();
        var cursor = (Dictionary<string, object?>)deep;
        for (int i = 0; i < 20; i++)
        {
            var next = new Dictionary<string, object?>();
            cursor["k"] = next;
            cursor = next;
        }

        YamlException ex = Assert.ThrowsAny<YamlException>(() => doc.Set("/x", new Box { Inner = deep }));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, ex.Line);
        Assert.StartsWith("In the written value", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line ", ex.Message, StringComparison.Ordinal);
        Assert.Equal("x: 0\n", doc.ToString());
    }

    public sealed class Box
    {
        public object? Inner { get; set; }
    }
}
