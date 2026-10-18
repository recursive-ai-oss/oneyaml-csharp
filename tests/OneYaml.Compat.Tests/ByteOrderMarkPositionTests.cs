using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rl = global::YamlDotNet;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests;

// Where a leading U+FEFF counts as a column: the real libraries count it when they read text, we never do.
public class ByteOrderMarkPositionTests
{
    private const string Mark = "\uFEFF";
    private const string Good = "a: 1\nb: [x, y]\n";
    private const string Bad = "a: 1\nb: [x, y\n";

    private static Stream Utf8(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static Rl.RepresentationModel.YamlStream RealLoad(TextReader r)
    {
        var s = new Rl.RepresentationModel.YamlStream();
        s.Load(r);
        return s;
    }

    private static Cm.RepresentationModel.YamlStream CompatLoad(TextReader r)
    {
        var s = new Cm.RepresentationModel.YamlStream();
        s.Load(r);
        return s;
    }

    private static (int Line, int Column, int Index) Pos(Rl.Core.Mark m) => ((int)m.Line, (int)m.Column, (int)m.Index);

    private static (int Line, int Column, int Index) Pos(Cm.Core.Mark m) => ((int)m.Line, (int)m.Column, (int)m.Index);

    // Start and End of every node in document order.
    private static List<((int, int, int) Start, (int, int, int) End)> RealMarks(TextReader r) =>
        [.. RealLoad(r).Documents[0].AllNodes.Select(n => (Pos(n.Start), Pos(n.End)))];

    private static List<((int, int, int) Start, (int, int, int) End)> CompatMarks(TextReader r) =>
        [.. CompatLoad(r).Documents[0].AllNodes.Select(n => (Pos(n.Start), Pos(n.End)))];

    [Fact]
    public void YamlDotNet_counts_a_leading_mark_in_the_nodes_after_the_first_when_the_text_is_a_string()
    {
        var plain = RealMarks(new StringReader(Good));
        var marked = RealMarks(new StringReader(Mark + Good));

        Assert.Equal(plain.Count, marked.Count);
        Assert.Equal(plain[0], marked[0]);
        for (int i = 1; i < plain.Count; i++)
        {
            // The mark is one UTF-16 unit on the first line, so only the index moves on later lines.
            Assert.Equal(plain[i].End.Item3 + 1, marked[i].End.Item3);
        }

        Assert.NotEqual(plain, marked);
    }

    [Fact]
    public void YamlDotNet_does_not_count_the_mark_when_the_text_comes_through_a_StreamReader()
    {
        Assert.Equal(RealMarks(new StringReader(Good)), RealMarks(new StreamReader(Utf8(Mark + Good))));
    }

    [Fact]
    public void The_YamlDotNet_adapter_never_counts_the_mark()
    {
        var plain = RealMarks(new StringReader(Good));

        Assert.Equal(plain, CompatMarks(new StringReader(Mark + Good)));
        Assert.Equal(plain, CompatMarks(new StreamReader(Utf8(Mark + Good))));
        Assert.Equal(plain, CompatMarks(new StringReader(Good)));
    }

    [Fact]
    public void YamlDotNet_counts_the_mark_in_an_error_mark_for_a_string_and_the_adapter_does_not()
    {
        var plain = Assert.ThrowsAny<Rl.Core.YamlException>(() => RealLoad(new StringReader(Bad)));
        var real = Assert.ThrowsAny<Rl.Core.YamlException>(() => RealLoad(new StringReader(Mark + Bad)));
        var viaStream = Assert.ThrowsAny<Rl.Core.YamlException>(() => RealLoad(new StreamReader(Utf8(Mark + Bad))));

        Assert.Equal(Pos(plain.Start).Index + 1, Pos(real.Start).Index);
        Assert.Equal(Pos(plain.Start), Pos(viaStream.Start));

        foreach (Func<string, TextReader> open in new Func<string, TextReader>[] { t => new StringReader(t), t => new StreamReader(Utf8(t)) })
        {
            var compat = Assert.ThrowsAny<Cm.Core.YamlException>(() => CompatLoad(open(Mark + Bad)));
            Assert.Equal(Pos(plain.Start), Pos(compat.Start));
            Assert.Equal(Pos(plain.End), Pos(compat.End));
        }
    }

    [Fact]
    public void SharpYaml_counts_a_leading_mark_for_a_string_or_a_reader_but_not_for_a_stream()
    {
        int plain = Assert.ThrowsAny<Rs.YamlException>(() => Rs.YamlSerializer.Deserialize<object>(Bad)).Start.Index;

        Assert.Equal(plain + 1, Assert.ThrowsAny<Rs.YamlException>(() => Rs.YamlSerializer.Deserialize<object>(Mark + Bad)).Start.Index);
        Assert.Equal(plain + 1, Assert.ThrowsAny<Rs.YamlException>(() => Rs.YamlSerializer.Deserialize<object>(new StringReader(Mark + Bad))).Start.Index);
        Assert.Equal(plain, Assert.ThrowsAny<Rs.YamlException>(() => Rs.YamlSerializer.Deserialize<object>(Utf8(Mark + Bad))).Start.Index);
    }

    [Fact]
    public void The_SharpYaml_adapter_never_counts_the_mark()
    {
        int plain = Assert.ThrowsAny<Rs.YamlException>(() => Rs.YamlSerializer.Deserialize<object>(Bad)).Start.Index;

        Assert.Equal(plain, Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(Bad)).Start.Index);
        Assert.Equal(plain, Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(Mark + Bad)).Start.Index);
        Assert.Equal(plain, Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(new StringReader(Mark + Bad))).Start.Index);
        Assert.Equal(plain, Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(Utf8(Mark + Bad))).Start.Index);
    }

    [Theory]
    [InlineData("x: 😀\ny: [1\n")]
    [InlineData("😀😀: 1\nb: {a: 1\n")]
    [InlineData("a: 1\nb: \"😀\" x\n")]
    public void The_YamlDotNet_adapter_counts_UTF_16_units_as_YamlDotNet_does_for_text_with_non_BMP_characters(string text)
    {
        var real = Assert.ThrowsAny<Rl.Core.YamlException>(() => RealLoad(new StringReader(text)));
        var compat = Assert.ThrowsAny<Cm.Core.YamlException>(() => CompatLoad(new StringReader(text)));

        // Start only: the end of a scanner error is not always the same width.
        Assert.Equal(Pos(real.Start), Pos(compat.Start));
    }
}
