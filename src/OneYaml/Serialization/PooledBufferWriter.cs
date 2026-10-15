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

        // A segment may end inside a character, so the segments go through one decoder.
        Decoder decoder = Encoding.UTF8.GetDecoder();
        int chars = 0;
        foreach ((byte[] segment, int count) in _full)
        {
            chars += decoder.GetCharCount(segment.AsSpan(0, count), flush: false);
        }

        chars += decoder.GetCharCount(_buffer.AsSpan(0, _written), flush: true);
        return string.Create(chars, this, static (span, self) =>
        {
            Decoder d = Encoding.UTF8.GetDecoder();
            int at = 0;
            foreach ((byte[] segment, int count) in self._full!)
            {
                at += d.GetChars(segment.AsSpan(0, count), span[at..], flush: false);
            }

            d.GetChars(self._buffer.AsSpan(0, self._written), span[at..], flush: true);
        });
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
