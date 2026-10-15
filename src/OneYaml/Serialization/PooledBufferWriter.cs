using System;
using System.Buffers;

namespace OneYaml;

// A growing UTF-8 buffer from the array pool, for building the text of one Serialize call.
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    // The size of the last output on this thread: a program that writes similar documents over and over then
    // starts with a buffer that fits, instead of growing and copying on every call.
    [ThreadStatic]
    private static int s_lastLength;

    public PooledBufferWriter() => _buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(s_lastLength, 4096, 1 << 24));

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

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

    public void Dispose()
    {
        s_lastLength = _written;
        byte[] buffer = _buffer;
        _buffer = [];
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

        long wanted = Math.Max((long)_written + sizeHint, (long)_buffer.Length * 2);
        if (wanted > Array.MaxLength)
        {
            wanted = Math.Max((long)_written + sizeHint, Array.MaxLength);
            if (wanted > Array.MaxLength)
            {
                throw new YamlException("The YAML text is larger than an array can hold.", YamlErrorCategory.Limit, 0, 0, 0);
            }
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)wanted);
        Buffer.BlockCopy(_buffer, 0, bigger, 0, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }
}
