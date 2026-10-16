using Xunit;

namespace OneYaml.Tests;

/// <summary>Escaped surrogates in double-quoted scalars: a high half followed directly by a low half is one character.</summary>
public class EscapedSurrogateTests
{
    [Theory]
    [InlineData("\"\\ud83d\\ude00\"", "\U0001F600")]
    [InlineData("\"\\uD83D\\uDE00\"", "\U0001F600")]
    [InlineData("\"a\\ud800\\udc00b\"", "a\U00010000b")]
    [InlineData("\"\\udbff\\udfff\"", "\U0010FFFF")]
    [InlineData("\"\\ud83d\\ude00\\ud83d\\ude01\"", "\U0001F600\U0001F601")]
    public void A_high_and_a_low_escape_in_a_row_are_one_character(string yaml, string expected)
    {
        Assert.Equal(expected, YamlSerializer.Deserialize<string>(yaml));
        Assert.Equal(expected, YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(expected, YamlSerializer.Deserialize<Dictionary<string, string>>("k: " + yaml)!.Single().Value);
        using YamlDocument doc = YamlDocument.Parse("k: " + yaml);
        Assert.Equal(expected, doc.Root["k"].Deserialize<string>());
        Assert.Equal(["+STR", "+DOC", "=VAL \"" + expected.Replace("\n", "\\n"), "-DOC", "-STR"], EventText.Read(System.Text.Encoding.UTF8.GetBytes(yaml)).Events.ToArray());
    }

    [Theory]
    [InlineData("\"\\ud83d\"")]
    [InlineData("\"\\ude00\"")]
    [InlineData("\"\\ude00\\ud83d\"")]
    [InlineData("\"\\ud83d\\ud83d\"")]
    [InlineData("\"\\ud83dx\"")]
    [InlineData("\"\\ud83d\U0001F600\"")]
    [InlineData("\"\\ud83d\\n\"")]
    [InlineData("\"\\ud83d\\x41\"")]
    [InlineData("\"\\ud83d\\U0000de00\"")]
    [InlineData("\"\\ud83d\\u0041\"")]
    [InlineData("\"\\ud83d \\ude00\"")]
    [InlineData("\"\\ud83d\\ude0\"")]
    [InlineData("\"\\U0000d83d\"")]
    public void A_lone_or_mismatched_surrogate_escape_is_a_syntax_error(string yaml)
    {
        Assert.Equal(YamlErrorCategory.Syntax, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<string>(yaml)).Category);
        Assert.Equal(YamlErrorCategory.Syntax, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml)).Category);
        Assert.Equal(YamlErrorCategory.Syntax, Assert.Throws<YamlException>(() => YamlDocument.Parse("k: " + yaml)).Category);
    }

    [Fact]
    public void Text_outside_the_basic_plane_is_written_so_that_it_reads_back_and_a_lone_surrogate_is_refused()
    {
        string emoji = "x \U0001F600 y";
        Assert.Equal(emoji, YamlSerializer.Deserialize<string>(YamlSerializer.Serialize(emoji)));
        Assert.DoesNotContain("\\ud", YamlSerializer.Serialize(emoji), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<YamlException>(() => YamlSerializer.Serialize("bad\uD800text"));
        Assert.Throws<YamlException>(() => YamlSerializer.Serialize("bad\uDE00text"));
    }
}
