using System;
using System.Buffers;

namespace OneYaml.Documents;

/// <summary>A growable byte buffer on pooled arrays, used for the text of an edit and for output.</summary>
internal sealed class ByteBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _array;
    private int _count;

    public ByteBuffer(int capacity = 256)
    {
        _array = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 16));
    }

    public int Count => _count;

    public ReadOnlySpan<byte> WrittenSpan => _array.AsSpan(0, _count);

    public byte[] Array => _array;

    public void Advance(int count) => _count += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _array.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _array.AsSpan(_count);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_array.AsSpan(_count));
        _count += bytes.Length;
    }

    public void Write(byte b)
    {
        Ensure(1);
        _array[_count++] = b;
    }

    public void WriteSpaces(int n)
    {
        if (n <= 0)
        {
            return;
        }

        Ensure(n);
        _array.AsSpan(_count, n).Fill((byte)' ');
        _count += n;
    }

    public void WriteAscii(string s)
    {
        Ensure(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            _array[_count++] = (byte)s[i];
        }
    }

    public void Truncate(int count) => _count = count;

    private void Ensure(int extra)
    {
        if (extra < 1)
        {
            extra = 1;
        }

        if (_array.Length - _count >= extra)
        {
            return;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(checked(Math.Max(_array.Length * 2, _count + extra)));
        Buffer.BlockCopy(_array, 0, bigger, 0, _count);
        ArrayPool<byte>.Shared.Return(_array);
        _array = bigger;
    }

    public void Dispose()
    {
        byte[] a = _array;
        _array = [];
        if (a.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(a);
        }
    }
}

/// <summary>
/// Charges the alias nodes and bytes a read expands against the limits. A read that follows an
/// alias adds the size of what the alias stands for; the budget is per operation.
/// </summary>
internal struct AliasBudget
{
    private long _nodes;
    private long _bytes;

    public AliasBudget(DocState state, YamlLimits limits)
    {
        _nodes = limits.AliasNodeBudget(state.NodeCount);
        _bytes = limits.AliasByteBudget(state.Length);
    }

    /// <summary>Charges for following an alias to <paramref name="target"/>.</summary>
    /// <param name="state">The document.</param>
    /// <param name="alias">The alias node (for the error position).</param>
    /// <param name="target">The node it refers to.</param>
    public void Charge(DocState state, int alias, int target)
    {
        ref readonly CstNode t = ref state.Node(target);
        _nodes -= t.Size;
        _bytes -= Math.Max(t.End - t.Start, 1);
        if (_nodes < 0 || _bytes < 0)
        {
            throw YamlException.At(YamlErrorCategory.Limit, "Following aliases expands to more than the configured limit.", state.Text, state.Node(alias).Start);
        }
    }
}
