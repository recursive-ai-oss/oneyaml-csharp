using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

// Hostile input aimed at the document's hash tables and integer parsing, and oversized streams.
public class DocumentHostileInputTests
{
    [Fact]
    public void Integer_keys_beyond_the_digit_limit_are_refused_before_they_are_compared()
    {
        string yaml = "? " + new string('1', 6000) + "\n: 1\n? " + new string('2', 6000) + "\n: 1\n";

        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Integer_keys_within_the_digit_limit_still_compare_exactly()
    {
        string same = "? " + new string('1', 4000) + "\n: 1\n? 0x" + new string('f', 10) + "\n: 1\n? " + new string('1', 4000) + "0\n: 1\n";
        using YamlDocument ok = YamlDocument.Parse(same);

        string dup = "? " + new string('1', 4000) + "\n: 1\n? +" + new string('1', 4000) + "\n: 1\n";
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(dup));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }

    [Fact]
    public void A_seekable_stream_over_the_input_limit_is_refused_without_being_read()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 1000 } };
        var stream = new CountingStream(new byte[5000]);

        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(stream, options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task A_seekable_stream_over_the_input_limit_is_refused_without_being_read_asynchronously()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 1000 } };
        var stream = new CountingStream(new byte[5000]);

        YamlException ex = await Assert.ThrowsAsync<YamlException>(() => YamlDocument.ParseAsync(stream, options, TestContext.Current.CancellationToken));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, stream.Reads);
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
