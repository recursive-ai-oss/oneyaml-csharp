using System.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// The generic loader builds small mappings from a staging area and large ones entry by entry. These tests walk
/// the sizes on both sides of that boundary and require the same keys in the same order, the same duplicate-key
/// errors at the same place, and the same merge-key results.
/// </summary>
public class GenericLoadShapeTests
{
    private static string Map(int count, string prefix = "k") =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"{prefix}{i}: {i}\n"));

    [Fact]
    public void Mappings_of_every_size_keep_their_entries_in_order()
    {
        for (int n = 0; n <= 20; n++)
        {
            var map = Assert.IsType<Dictionary<string, object?>>(n == 0 ? YamlSerializer.Deserialize<object>("{}") : YamlSerializer.Deserialize<object>(Map(n)));
            Assert.Equal(Enumerable.Range(0, n).Select(i => $"k{i}"), map.Keys);
            Assert.Equal(Enumerable.Range(0, n).Select(i => (object)(long)i), map.Values);
        }
    }

    [Fact]
    public void Nested_mappings_and_lists_keep_each_entry_in_its_own_container()
    {
        string yaml = "a: {x: 1, y: [1, {p: 1, q: 2}, 3], z: 3}\nb: [{m: 1}, {m: 2, n: 3}]\nc: 1\n";
        var root = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(["a", "b", "c"], root.Keys);
        var a = Assert.IsType<Dictionary<string, object?>>(root["a"]);
        Assert.Equal(["x", "y", "z"], a.Keys);
        var y = Assert.IsType<List<object?>>(a["y"]);
        var inner = Assert.IsType<Dictionary<string, object?>>(y[1]);
        Assert.Equal(["p", "q"], inner.Keys);
        Assert.Equal(3L, a["z"]);
        var b = Assert.IsType<List<object?>>(root["b"]);
        Assert.Equal(2, Assert.IsType<Dictionary<string, object?>>(b[1]).Count);
        Assert.Equal(1L, root["c"]);
    }

    [Fact]
    public void A_repeated_key_is_reported_where_it_repeats_whatever_the_size()
    {
        for (int n = 1; n <= 12; n++)
        {
            for (int at = 0; at < n; at++)
            {
                string head = Map(n);
                string yaml = head + $"k{at}: again\n";
                YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
                Assert.Equal(YamlErrorCategory.DuplicateKey, e.Category);
                Assert.Equal(Encoding.UTF8.GetByteCount(head), e.Offset);
            }
        }
    }

    [Fact]
    public void A_repeated_key_wins_over_a_syntax_error_further_on()
    {
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("a: 1\nb: 2\na: 3\nc: [\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, e.Category);
    }

    [Fact]
    public void Different_spellings_of_one_number_are_the_same_key_in_a_staged_mapping()
    {
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("1: a\n0x1: b\n"));
        Assert.Equal(YamlErrorCategory.DuplicateKey, e.Category);
    }

    [Fact]
    public void A_merge_key_after_staged_entries_fills_in_only_what_is_missing()
    {
        string yaml = "base: &b {x: 1, y: 2, z: 3}\nout:\n  a: 0\n  y: own\n  <<: *b\n  b: 9\n";
        var options = new YamlSerializerOptions { MergeKeys = true };
        var root = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml, options));
        var outMap = Assert.IsType<Dictionary<string, object?>>(root["out"]);
        Assert.Equal(["a", "y", "x", "z", "b"], outMap.Keys);
        Assert.Equal("own", outMap["y"]);
        Assert.Equal(1L, outMap["x"]);
    }

    [Fact]
    public void A_key_after_a_merge_key_overrides_the_merged_value()
    {
        string yaml = "base: &b {x: 1, y: 2}\nout:\n  <<: *b\n  x: 5\n";
        var options = new YamlSerializerOptions { MergeKeys = true };
        var root = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml, options));
        var outMap = Assert.IsType<Dictionary<string, object?>>(root["out"]);
        Assert.Equal(5L, outMap["x"]);
        Assert.Equal(2L, outMap["y"]);
    }
}
