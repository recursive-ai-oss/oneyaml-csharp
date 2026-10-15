using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneYaml;

// A growing UTF-8 buffer from the array pool, for building the text of one Serialize call. The text is kept in
// segments, so growing never copies and a long text never rents one array of twice its size (the pool rounds
// every request up to a power of two).
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int MaxSegment = 1 << 16;

    private byte[] _buffer;
    private int _written;

    // The finished segments before the current one, in order, each with the bytes it holds.
    private List<(byte[] Buffer, int Count)>? _full;
    private long _fullBytes;

    // The size of the last output on this thread: a program that writes similar documents over and over then
    // starts with a buffer that fits, instead of growing on every call.
    [ThreadStatic]
    private static int s_lastLength;

    public PooledBufferWriter() => _buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(s_lastLength, 256, MaxSegment));

    /// <summary>The number of bytes written so far, over all segments.</summary>
    public long Length => _fullBytes + _written;

    public void Advance(int count)
    {
        if (count < 0 || _written > _buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Grow(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Grow(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public byte[] ToArray()
    {
        if (_full is null)
        {
            return _buffer.AsSpan(0, _written).ToArray();
        }

        byte[] result = new byte[Length];
        int at = 0;
        foreach ((byte[] segment, int count) in _full)
        {
            segment.AsSpan(0, count).CopyTo(result.AsSpan(at));
            at += count;
        }

        _buffer.AsSpan(0, _written).CopyTo(result.AsSpan(at));
        return result;
    }

    public string ToUtf8String()
    {
        if (_full is null)
        {
            return Encoding.UTF8.GetString(_buffer.AsSpan(0, _written));
        }

        // A segment may end inside a character, so the count and the decode both carry the unfinished bytes over.
        int chars = Decode(default);
        return string.Create(chars, this, static (span, self) => self.Decode(span));
    }

    // Decodes all segments in order. With an empty destination it only counts. A character split between two
    // segments is put together from its pieces, so the result is that of decoding the whole text in one go, whatever
    // the bytes are.
    private int Decode(Span<char> destination)
    {
        Span<byte> carry = stackalloc byte[4];
        int carried = 0;
        int chars = 0;
        if (_full is not null)
        {
            foreach ((byte[] segment, int count) in _full)
            {
                chars += DecodeSegment(segment.AsSpan(0, count), destination, chars, carry, ref carried);
            }
        }

        chars += DecodeSegment(_buffer.AsSpan(0, _written), destination, chars, carry, ref carried);
        if (carried > 0)
        {
            // The text ends inside a character: the decoder reports what is left as replacement characters.
            chars += Emit(carry[..carried], destination, chars);
        }

        return chars;
    }

    private static int DecodeSegment(ReadOnlySpan<byte> bytes, Span<char> destination, int at, Span<byte> carry, ref int carried)
    {
        int chars = 0;
        if (carried > 0)
        {
            int need = SequenceLength(carry[0]) - carried;
            while (need > 0 && !bytes.IsEmpty && (bytes[0] & 0xC0) == 0x80)
            {
                carry[carried++] = bytes[0];
                bytes = bytes[1..];
                need--;
            }

            if (need > 0 && bytes.IsEmpty)
            {
                // The segment ended before the character did.
                return 0;
            }

            chars += Emit(carry[..carried], destination, at);
            carried = 0;
        }

        int tail = IncompleteTail(bytes);
        chars += Emit(bytes[..^tail], destination, at + chars);
        bytes[^tail..].CopyTo(carry);
        carried = tail;
        return chars;
    }

    private static int Emit(ReadOnlySpan<byte> bytes, Span<char> destination, int at) =>
        destination.IsEmpty ? Encoding.UTF8.GetCharCount(bytes) : Encoding.UTF8.GetChars(bytes, destination[at..]);

    private static int SequenceLength(byte lead) => lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;

    // The number of bytes at the end that start a multi-byte character the segment does not finish.
    private static int IncompleteTail(ReadOnlySpan<byte> bytes)
    {
        for (int back = 1; back <= 3 && back <= bytes.Length; back++)
        {
            byte b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue;
            }

            return b >= 0xC0 && b < 0xF8 && SequenceLength(b) > back ? back : 0;
        }

        return 0;
    }

    public void WriteTo(Stream stream)
    {
        if (_full is not null)
        {
            foreach ((byte[] segment, int count) in _full)
            {
                stream.Write(segment, 0, count);
            }
        }

        stream.Write(_buffer, 0, _written);
    }

    public async Task WriteToAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_full is not null)
        {
            foreach ((byte[] segment, int count) in _full)
            {
                await stream.WriteAsync(segment.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.WriteAsync(_buffer.AsMemory(0, _written), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        s_lastLength = (int)Math.Min(Length, int.MaxValue);
        if (_full is not null)
        {
            foreach ((byte[] segment, _) in _full)
            {
                ArrayPool<byte>.Shared.Return(segment);
            }

            _full = null;
        }

        byte[] buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Grow(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        if (Length + sizeHint > Array.MaxLength)
        {
            throw new YamlException("The YAML text is larger than an array can hold.", YamlErrorCategory.Limit, 0, 0, 0);
        }

        // The next segment is as large as the one before, up to the cap, and always holds the hint.
        int size = Math.Max(Math.Min(_buffer.Length * 2, MaxSegment), sizeHint);
        byte[] next = ArrayPool<byte>.Shared.Rent(size);
        (_full ??= []).Add((_buffer, _written));
        _fullBytes += _written;
        _buffer = next;
        _written = 0;
    }
}
