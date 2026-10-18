using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnRep = global::OneYaml.Compat.YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// A text reader and a string of the same text meet the same input limit, byte order mark included.
public class ReaderInputLimitTests
{
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
}
