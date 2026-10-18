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
}
