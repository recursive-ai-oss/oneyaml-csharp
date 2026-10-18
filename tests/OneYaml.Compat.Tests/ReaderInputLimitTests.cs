using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnRep = global::OneYaml.Compat.YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// A text reader and a string of the same text meet the same input limit, byte order mark included.
public class ReaderInputLimitTests
{
    // Hands out a text in pieces of at most `piece` characters, so the chunked counting loop of the core is the one that runs
    // (a StringReader is counted as a whole string instead).
    private sealed class PieceReader(string text, int piece) : TextReader
    {
        private int _at;

        public override int Read(char[] buffer, int index, int count)
        {
            int n = Math.Min(Math.Min(count, piece), text.Length - _at);
            text.CopyTo(_at, buffer, index, n);
            _at += n;
            return n;
        }

        public override int Read(Span<char> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, piece), text.Length - _at);
            text.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
    }

    private static readonly int[] Pieces = [1, 3, 4096];

    [Fact]
    public void YamlDotNet_a_reader_one_byte_over_the_limit_after_a_byte_order_mark_is_refused_not_cut()
    {
        string text = "a: " + new string('x', 4091) + "\nb: 2\n"; // 4,095 bytes of text and 5 more
        var limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4095 };

        // The mark is not counted, so the text is over the limit by five bytes whether or not it is there.
        var stream = new YdnRep.YamlStream();
        Assert.ThrowsAny<YdnCore.YamlException>(() => stream.Load(new StringReader("\uFEFF" + text), limits));
        Assert.ThrowsAny<YdnCore.YamlException>(() => new YdnRep.YamlStream().Load(new StringReader(text), limits));
    }

    // A boundary pin: the same text one byte under the over-limit case above. It also loads at the unfixed commit.
    [Fact]
    public void YamlDotNet_a_reader_exactly_at_the_limit_after_a_byte_order_mark_loads_whole()
    {
        string text = "a: " + new string('x', 4091) + "\n"; // 4,095 bytes
        var limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4095 };
        var stream = new YdnRep.YamlStream();
        stream.Load(new StringReader("\uFEFF" + text), limits);
        Assert.Single(stream.Documents);
    }

    [Fact]
    public void SharpYaml_a_reader_exactly_at_the_limit_after_a_byte_order_mark_reads_as_a_string_does()
    {
        string text = "k: " + new string('x', 1000) + "\n";
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = text.Length } };

        Dictionary<string, string> viaString = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>("\uFEFF" + text, options)!;
        Dictionary<string, string> viaReader = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new StringReader("\uFEFF" + text), options)!;
        Assert.Equal(viaString["k"], viaReader["k"]);

        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new StringReader(text + "#"), options));
    }

    private static Cs.YamlSerializerOptions Limited(string? source = null) =>
        new() { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 1000 }, SourceName = source };

    [Theory]
    [InlineData(1)]
    [InlineData(0xD800)]
    public void SharpYaml_a_bad_character_after_the_first_document_is_not_read_so_not_refused_with_a_limit_set(int bad)
    {
        // Built here: a lone surrogate does not survive being passed through test discovery as text.
        string text = "a: 1\n---\nb: " + (char)bad + "\n";
        Dictionary<string, string> viaString = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(text, Limited())!;
        Dictionary<string, string> viaReader = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new StringReader(text), Limited())!;
        Assert.Equal(viaString["a"], viaReader["a"]);
    }

    [Fact]
    public void SharpYaml_an_error_in_the_first_document_keeps_its_source_and_index_with_a_limit_set()
    {
        const string Text = "a: 1\nb: \u0001\n";
        Cs.YamlException viaString = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(Text, Limited("f.yaml")));
        Cs.YamlException viaReader = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new StringReader(Text), Limited("f.yaml")));
        Assert.Equal("f.yaml", viaReader.SourceName);
        Assert.Equal(viaString.Start.Index, viaReader.Start.Index);
        Assert.Equal(8, viaReader.Start.Index);
        Assert.Equal(viaString.Message, viaReader.Message);
    }

    [Fact]
    public void YamlDotNet_a_reader_loaded_with_a_limit_set_keeps_the_byte_order_mark_and_the_marks()
    {
        const string Text = "\uFEFFk: \U0001F600\nm: v\n";
        var limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 1000 };
        var viaString = new YdnRep.YamlStream();
        viaString.Load(new StringReader(Text), global::OneYaml.YamlLimits.Default);
        var viaReader = new YdnRep.YamlStream();
        viaReader.Load(new StringReader(Text), limits);

        var a = new StringWriter();
        var b = new StringWriter();
        viaString.Save(a, assignAnchors: false);
        viaReader.Save(b, assignAnchors: false);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.StartsWith("\uFEFF", b.ToString(), StringComparison.Ordinal);
        var keyA = (YdnRep.YamlScalarNode)((YdnRep.YamlMappingNode)viaString.Documents[0].RootNode).Children.Keys.Last();
        var keyB = (YdnRep.YamlScalarNode)((YdnRep.YamlMappingNode)viaReader.Documents[0].RootNode).Children.Keys.Last();
        Assert.Equal(keyA.Start.Index, keyB.Start.Index);
    }

    private static string SharpOutcome(Func<object?> read)
    {
        try
        {
            return "value " + read();
        }
        catch (Cs.YamlException e)
        {
            return e.GetType().Name + "|" + e.SourceName + "|" + e.Start.Index + "|" + e.Message;
        }
    }

    // The string overload reads only the first document; a reader is counted whole, so a text over the limit is refused
    // however it is handed over and wherever the text beyond the first document is bad.
    private static void AssertRefusedOrRead(string text, int limit, bool fits)
    {
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = limit }, SourceName = "m.yaml" };
        var readers = new List<Func<TextReader>> { () => new StringReader(text) };
        readers.AddRange(Pieces.Select<int, Func<TextReader>>(piece => () => new PieceReader(text, piece)));
        foreach (Func<TextReader> make in readers)
        {
            string outcome = SharpOutcome(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(make(), options));
            if (fits)
            {
                Assert.StartsWith("value", outcome, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("YamlException|m.yaml|", outcome, StringComparison.Ordinal);
                Assert.Contains("MaxInputBytes", outcome, StringComparison.Ordinal);
            }

            Assert.Equal(fits, Cs.YamlSerializer.TryDeserialize(make(), out Dictionary<string, string>? _, options));
        }
    }

    [Theory]
    [InlineData("a: 1\n---\nb: 2\n", 13, false)]
    [InlineData("a: 1\n---\nb: 2\n", 14, true)]
    [InlineData("\uFEFFa: 1\n---\nb: 2\n", 13, false)]
    [InlineData("\uFEFFa: 1\n---\nb: 2\n", 14, true)]
    public void SharpYaml_a_text_with_several_documents_over_the_limit_is_refused_through_every_kind_of_reader(string text, int limit, bool fits)
        => AssertRefusedOrRead(text, limit, fits);

    [Theory]
    [InlineData(1, 14)]
    [InlineData(0xD800, 16)]
    public void SharpYaml_a_bad_character_after_the_first_document_counts_against_the_limit_but_is_not_parsed(int bad, int size)
    {
        // Built here: a lone surrogate does not survive being passed through test discovery as text.
        string text = "a: 1\n---\nb: " + (char)bad + "\n";
        Assert.Equal(size, System.Text.Encoding.UTF8.GetByteCount(text));
        AssertRefusedOrRead(text, size - 1, fits: false);
        AssertRefusedOrRead(text, size, fits: true);
    }

    [Fact]
    public void SharpYaml_a_reader_in_pieces_at_the_limit_after_a_byte_order_mark_reads_and_one_byte_over_is_refused()
    {
        string text = "k: " + new string('x', 1000) + "\n";
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = text.Length } };
        foreach (int piece in Pieces)
        {
            Assert.Equal(1000, Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader("\uFEFF" + text, piece), options)!["k"].Length);
            Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader("\uFEFF" + text + "#", piece), options));
        }
    }

    [Fact]
    public void YamlDotNet_a_reader_in_pieces_at_the_limit_after_a_byte_order_mark_loads_whole_and_one_byte_over_is_refused()
    {
        string text = "a: " + new string('x', 4091) + "\n"; // 4,095 bytes
        var limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4095 };
        foreach (int piece in Pieces)
        {
            var stream = new YdnRep.YamlStream();
            stream.Load(new PieceReader("\uFEFF" + text, piece), limits);
            Assert.Single(stream.Documents);
            Assert.ThrowsAny<YdnCore.YamlException>(() => new YdnRep.YamlStream().Load(new PieceReader("\uFEFF" + text + "#", piece), limits));
        }
    }

    // The pair's high surrogate is the last character of the first 4,096-character chunk, its low surrogate the first of the next.
    [Fact]
    public void A_surrogate_pair_split_across_the_chunk_boundary_counts_once_as_four_bytes()
    {
        string text = "k: " + new string('x', 4092) + "\U0001F600\n"; // 4,095 characters, then the pair, then a line break: 4,100 bytes
        Assert.Equal(4100, System.Text.Encoding.UTF8.GetByteCount(text));
        Assert.True(char.IsHighSurrogate(text[4095]));

        var atLimit = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4100 } };
        var under = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4099 } };
        string expected = Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(text, atLimit)!["k"];
        Assert.Equal(expected, Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader(text, 4096), atLimit)!["k"]);
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader(text, 4096), under));

        var ydnAt = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 4100 };
        new YdnRep.YamlStream().Load(new PieceReader(text, 4096), ydnAt);
        Assert.ThrowsAny<YdnCore.YamlException>(() => new YdnRep.YamlStream().Load(new PieceReader(text, 4096), ydnAt with { MaxInputBytes = 4099 }));
    }

    // A high surrogate that ends the text has no partner: it counts as three bytes and is left for the string path to report.
    [Fact]
    public void A_trailing_lone_high_surrogate_counts_against_the_limit_and_is_reported_as_from_a_string()
    {
        string text = "k: v\n" + (char)0xD800; // 5 bytes and 3
        foreach (int limit in new[] { 7, 8 })
        {
            var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = limit }, SourceName = "s.yaml" };
            string viaString = SharpOutcome(() => Cs.YamlSerializer.Deserialize<object>(text, options));
            foreach (int piece in Pieces)
            {
                Assert.Equal(viaString, SharpOutcome(() => Cs.YamlSerializer.Deserialize<object>(new PieceReader(text, piece), options)));
            }
        }
    }

    [Fact]
    public void An_error_in_the_first_document_keeps_its_source_and_index_through_a_reader_in_pieces()
    {
        const string Text = "a: 1\nb: \u0001\n";
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 1000 }, SourceName = "f.yaml" };
        foreach (int piece in Pieces)
        {
            Cs.YamlException e = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader(Text, piece), options));
            Assert.Equal("f.yaml", e.SourceName);
            Assert.Equal(8, e.Start.Index);
        }
    }

    [Fact]
    public void An_over_limit_reader_error_carries_the_source_name()
    {
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 5 }, SourceName = "big.yaml" };
        Assert.Equal("big.yaml", Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(new PieceReader("k: value\n", 4), options)).SourceName);
        Assert.Equal("big.yaml", Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(new StringReader("k: value\n"), options)).SourceName);
    }

    [Fact]
    public void YamlDotNet_a_reader_in_pieces_loaded_with_a_limit_keeps_the_byte_order_mark_and_the_marks()
    {
        const string Text = "\uFEFFk: \U0001F600\nm: v\n";
        var viaString = new YdnRep.YamlStream();
        viaString.Load(new StringReader(Text), global::OneYaml.YamlLimits.Default);
        var limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = 1000 };
        foreach (int piece in Pieces)
        {
            var viaReader = new YdnRep.YamlStream();
            viaReader.Load(new PieceReader(Text, piece), limits);
            var a = new StringWriter();
            var b = new StringWriter();
            viaString.Save(a, assignAnchors: false);
            viaReader.Save(b, assignAnchors: false);
            Assert.Equal(a.ToString(), b.ToString());
            Assert.StartsWith("\uFEFF", b.ToString(), StringComparison.Ordinal);
        }
    }
}
