using System.Text;
using OneYaml.Text;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Tests that hold a very large buffer share this collection, so they never run beside each other or beside other collections.</summary>
[CollectionDefinition(nameof(InputCeilingAlone), DisableParallelization = true)]
public sealed class InputCeilingAlone;

/// <summary>
/// An input that is larger than one buffer can hold ends in the limit error, whatever the input size limit says
/// (none, or more than an array can take). The ceiling is lowered in the fast tests so no gigabyte is needed; the
/// slow tests cross the real one.
/// </summary>
[Collection(nameof(InputCeilingAlone))]
public class InputCeilingTests
{
    private const int Ceiling = 1 << 20;

    private static readonly YamlSerializerOptions NoLimit = new() { Limits = YamlLimits.None };

    private static readonly YamlSerializerOptions HugeLimit = new() { Limits = YamlLimits.Default with { MaxInputBytes = 1L << 42 } };

    /// <summary>
    /// Spaces for ever, in the pieces the caller asks for; counts what it handed out. It fails the test after four times the
    /// ceiling, so a reader that does not stop ends in a failure and not in a hang.
    /// </summary>
    private sealed class EndlessStream(long cap = 4L * Ceiling) : Stream
    {
        public long Supplied { get; private set; }

        private void Count(int n)
        {
            Supplied += n;
            if (Supplied > cap)
            {
                throw new InvalidOperationException("The reader did not stop.");
            }
        }

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
            Count(count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)' ');
            Count(buffer.Length);
            return new ValueTask<int>(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Spaces for ever, and a read-to-end call that fails the test: that call cannot be bounded.</summary>
    private sealed class EndlessReader : TextReader
    {
        public long Supplied { get; private set; }

        private void Count(int n)
        {
            Supplied += n;
            if (Supplied > 4L * Ceiling)
            {
                throw new InvalidOperationException("The reader did not stop.");
            }
        }

        public override string ReadToEnd() => throw new InvalidOperationException("The reader was asked to read to its end in one call.");

        public override int Read(char[] buffer, int index, int count)
        {
            buffer.AsSpan(index, count).Fill(' ');
            Count(count);
            return count;
        }

        public override int Read(Span<char> buffer)
        {
            buffer.Fill(' ');
            Count(buffer.Length);
            return buffer.Length;
        }
    }

    /// <summary>A finite text, read in pieces; its read-to-end call fails the test.</summary>
    private sealed class PieceReader(string text) : TextReader
    {
        private int _at;

        public override string ReadToEnd() => throw new InvalidOperationException("The reader was asked to read to its end in one call.");

        public override int Read(char[] buffer, int index, int count)
        {
            int n = Math.Min(Math.Min(count, 4096), text.Length - _at);
            text.CopyTo(_at, buffer, index, n);
            _at += n;
            return n;
        }

        public override int Read(Span<char> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, 4096), text.Length - _at);
            text.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
    }

    private static void AssertLimit(YamlException e) => Assert.Equal(YamlErrorCategory.Limit, e.Category);

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 42)]
    public void A_stream_larger_than_one_buffer_is_a_limit_error_not_a_failure_of_the_runtime(long max)
    {
        var stream = new EndlessStream();
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.ReadAllBytes(stream, max, "src", Ceiling));
        AssertLimit(e);
        Assert.Equal("src", e.SourceName);
        Assert.True(stream.Supplied <= 2L * Ceiling, $"read {stream.Supplied} bytes");
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 42)]
    public async Task An_async_stream_larger_than_one_buffer_is_a_limit_error(long max)
    {
        var stream = new EndlessStream();
        YamlException e = await Assert.ThrowsAsync<YamlException>(async () => await YamlSerializer.ReadAllBytesAsync(stream, max, null, TestContext.Current.CancellationToken, Ceiling));
        AssertLimit(e);
        Assert.True(stream.Supplied <= 2L * Ceiling, $"read {stream.Supplied} bytes");
    }

    [Fact]
    public void A_stream_that_fits_under_the_ceiling_is_read_whole()
    {
        byte[] data = Encoding.UTF8.GetBytes("a: 1\n" + new string('#', 300_000) + "\n");
        (byte[] buffer, int length) = YamlSerializer.ReadAllBytes(new MemoryStream(data), long.MaxValue, null, Ceiling);
        try
        {
            Assert.Equal(data, buffer.AsSpan(0, length).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 42)]
    public void A_reader_larger_than_one_buffer_is_a_limit_error(long max)
    {
        var reader = new EndlessReader();
        var limits = YamlLimits.Default with { MaxInputBytes = max };
        YamlException e = Assert.Throws<YamlException>(() => YamlInput.FromReader(reader, limits, Ceiling));
        AssertLimit(e);
        Assert.True(reader.Supplied <= 2L * Ceiling, $"read {reader.Supplied} chars");
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 42)]
    public void A_reader_read_to_a_string_larger_than_one_buffer_is_a_limit_error(long max)
    {
        var reader = new EndlessReader();
        var limits = YamlLimits.Default with { MaxInputBytes = max };
        YamlException e = Assert.Throws<YamlException>(() => YamlInput.ReadToString(reader, limits, Ceiling));
        AssertLimit(e);
        Assert.True(reader.Supplied <= 2L * Ceiling, $"read {reader.Supplied} chars");
    }

    [Fact]
    public void Without_an_input_limit_a_reader_is_read_in_pieces_not_in_one_call()
    {
        string text = "key: " + new string('v', 200_000) + "\nother: 1\n";

        Dictionary<string, string>? one = YamlSerializer.Deserialize<Dictionary<string, string>>(new PieceReader(text), NoLimit);
        Assert.Equal(200_000, one!["key"].Length);

        IReadOnlyList<Dictionary<string, string>?> all = YamlSerializer.DeserializeAll<Dictionary<string, string>>(new PieceReader(text + "---\nz: 2\n"), NoLimit);
        Assert.Equal(2, all.Count);

        using YamlInput input = YamlInput.FromReader(new PieceReader(text), YamlLimits.None);
        Assert.Equal(Encoding.UTF8.GetBytes(text), input.Utf8Text.ToArray());

        Assert.Equal(text, YamlInput.ReadToString(new PieceReader(text), YamlLimits.None));
    }

    [Fact]
    public void Without_an_input_limit_a_reader_with_a_byte_order_mark_and_a_surrogate_pair_reads_as_a_string_does()
    {
        string text = "\uFEFFk: 😀\n";
        using YamlInput viaReader = YamlInput.FromReader(new PieceReader(text), YamlLimits.None);
        using YamlInput viaString = YamlInput.FromChars(text, YamlLimits.None);
        Assert.Equal(viaString.Utf8Text.ToArray(), viaReader.Utf8Text.ToArray());
        Assert.True(viaReader.HasByteOrderMark);
    }

    // The next tests cross the real ceiling (about two gigabytes of buffer at the most), so they are slow.

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    // Each read can hold a buffer of a gigabyte or two; give it back before the next, so the peak stays at one read's.
    private static void Release()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static void Refused(Action read)
    {
        AssertLimit(Assert.Throws<YamlException>(read));
        Release();
    }

    private static async Task RefusedAsync(Func<Task> read)
    {
        AssertLimit(await Assert.ThrowsAsync<YamlException>(read));
        Release();
    }

    // One test crosses the real ceiling (a buffer of about two gigabytes). It runs alone: no other test starts while it
    // holds that memory. It guards the default ceiling: it also passes before the ceiling was handled, because a stream
    // reader already ended in the limit error there; the seam tests above are the ones that cover the new code.
    [Fact]
    [Trait("Category", "Slow")]
    public void An_endless_stream_with_no_input_limit_is_a_limit_error_at_the_real_ceiling()
    {
        Refused(() => YamlSerializer.Deserialize<object>(new EndlessStream(long.MaxValue), NoLimit));
    }

    // The over-buffer refusal of a document read carries the source name and does not speak of a limit that was not set.
    [Fact]
    public void A_document_stream_over_the_buffer_ceiling_names_its_source()
    {
        // ByteBuffer cannot be lowered to a small ceiling, so the name is checked on the refusal of the input limit that
        // shares the read loop, and the buffer message is checked for its wording below.
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 1000 }, SourceName = "cfg.yaml" };
        YamlException e = Assert.Throws<YamlException>(() => YamlDocument.Parse(new EndlessStream(), options));
        Assert.Equal("cfg.yaml", e.SourceName);
        Assert.Equal(YamlErrorCategory.Limit, e.Category);

        var buffer = new OneYaml.Documents.ByteBuffer(16);
        YamlException big = Assert.Throws<YamlException>(() => buffer.GetSpan(int.MaxValue));
        Assert.DoesNotContain("configured limit", big.Message);
    }
}
