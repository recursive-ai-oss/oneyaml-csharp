using System.Text;
using System.Text.Json;
using Xunit;

namespace OneYaml.Tests;

/// <summary>The helper that builds the input of a kit case from its recipe.</summary>
public class KitInputTests
{
    private static string Build(string recipe)
    {
        using JsonDocument doc = JsonDocument.Parse(recipe);
        return Encoding.UTF8.GetString(KitInput.Build(doc.RootElement));
    }

    [Fact]
    public void Text_parts_repeat_and_numbers_parts_count_with_padding()
    {
        Assert.Equal("{aaa\nk007: v,\nk008: v,\nk009: v,\n}", Build("""
            [{"text": "{"}, {"text": "a", "repeat": 3}, {"text": "\n"},
             {"numbers": {"count": 3, "start": 7, "prefix": "k", "suffix": ": v,\n", "width": 3}}, {"text": "}"}]
            """));
        Assert.Equal("", Build("""[{"text": "x", "repeat": 0}]"""));
        Assert.Equal("é€" + "é€", Build("""[{"text": "é€", "repeat": 2}]"""));
    }

    [Fact]
    public void A_repeat_that_spans_several_pieces_is_exact()
    {
        using JsonDocument doc = JsonDocument.Parse("""[{"text": "abc", "repeat": 1000003}]""");
        byte[] bytes = KitInput.Build(doc.RootElement);
        Assert.Equal(3_000_009, bytes.Length);
        Assert.True(bytes.AsSpan().ToArray().Chunk(3).All(c => c.SequenceEqual("abc"u8.ToArray())));
    }

    [Theory]
    [InlineData("""[{"bytes": "x"}]""")]
    [InlineData("""[{"text": "x", "times": 2}]""")]
    [InlineData("""[{"text": "x", "repeat": -1}]""")]
    [InlineData("""[{"numbers": {"count": 2, "step": 2}}]""")]
    [InlineData("""[{"numbers": {"start": 2}}]""")]
    [InlineData("""[{"text": "x", "repeat": 70000000}]""")]
    [InlineData("""[]""")]
    [InlineData("""{"text": "x"}""")]
    public void An_unknown_or_invalid_recipe_fails_instead_of_being_skipped(string recipe)
    {
        Assert.Throws<InvalidOperationException>(() => Build(recipe));
    }

    [Fact]
    public void A_case_has_a_file_or_a_recipe_not_both()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"input": "a.yaml", "input_recipe": [{"text": "x"}]}""");
        Assert.Throws<InvalidOperationException>(() => KitInput.Read(".", doc.RootElement));
    }
}
