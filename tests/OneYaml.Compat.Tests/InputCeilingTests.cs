using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;

namespace OneYaml.Compat.Tests;

// An input larger than one buffer can hold is refused with the adapter's own exception, whatever the input limit says:
// never an IOException, an OutOfMemoryException or a hang. The ceiling is lowered so no gigabyte is needed.
public class InputCeilingTests
{
    // Spaces for ever, counting what it hands out. It fails the test after a few times the ceiling, so a reader that does
    // not stop ends in a failure and not in a hang.
    private sealed class EndlessStream : Stream
    {
        public long Supplied { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            Supplied += count;
            if (Supplied > 8L << 20)
            {
                throw new InvalidOperationException("The reader did not stop.");
            }

            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The same refusals at a ceiling low enough to test without a gigabyte, so they run on every build.
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 42)]
    public void SharpYaml_a_stream_over_the_buffer_ceiling_is_a_limit_error_not_an_IOException(long max)
    {
        Cs.YamlException e = Assert.ThrowsAny<Cs.YamlException>(() => Cs.StreamText.Read(new EndlessStream(), "src", max, 1 << 20));
        Assert.Equal("src", e.SourceName);
    }

    // The stream is read to the limit and one chunk more, however much room the buffer has left: asking for all the room
    // read up to twice the limit.
    [Theory]
    [InlineData(524_289)]
    [InlineData(100_000)]
    [InlineData(5_000)]
    public void SharpYaml_an_endless_stream_over_a_limit_is_read_to_the_limit_and_one_chunk_not_to_the_end_of_the_buffer(int max)
    {
        var stream = new EndlessStream();
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { MaxInputBytes = max } };

        Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>(stream, options));
        Assert.InRange(stream.Supplied, max, max + 5 + 8192);
    }

    // A text whose characters would not fit a string is a limit error, one whose bytes are many but characters few is read.
    [Fact]
    public void SharpYaml_a_text_longer_than_a_string_can_hold_is_a_limit_error_and_a_short_one_with_many_bytes_is_read()
    {
        byte[] longText = System.Text.Encoding.UTF8.GetBytes("# " + new string('x', 98) + "\n");
        Assert.ThrowsAny<Cs.YamlException>(() => Cs.StreamText.Read(new MemoryStream(longText), "src", long.MaxValue, 1 << 20, 50));

        // 40 two-byte characters: 80 bytes, 40 characters.
        byte[] manyBytes = System.Text.Encoding.UTF8.GetBytes(new string('\u00e9', 40));
        Assert.Equal(new string('\u00e9', 40), Cs.StreamText.Read(new MemoryStream(manyBytes), "src", long.MaxValue, 1 << 20, 50));
    }
}
