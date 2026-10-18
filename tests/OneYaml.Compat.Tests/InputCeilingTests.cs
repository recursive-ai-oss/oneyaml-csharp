using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;

namespace OneYaml.Compat.Tests;

// An input larger than one buffer can hold is refused with the adapter's own exception, whatever the input limit says:
// never an IOException, an OutOfMemoryException or a hang. The ceiling is lowered so no gigabyte is needed.
public class InputCeilingTests
{
    private sealed class EndlessStream : Stream
    {
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
}
