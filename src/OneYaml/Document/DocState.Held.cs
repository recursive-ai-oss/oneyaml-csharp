using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OneYaml.Documents;

// An edit that keeps the number of nodes only moves the offsets of every node behind it, which on a large document
// costs far more than the edit itself. While a state is being edited in place that move is held back: a count per chunk
// of nodes says how far the offsets in the chunk are behind, and a chunk is brought up to date the moment any of its
// nodes is read or written through N. Code that reads nodes through N therefore always sees true offsets; code that
// needs a range or the whole array asks for it through NodeSpan and NodesSettled. Nothing is ever held back in a state
// that readers share (see Exclusive), and the parser, which runs before any edit, reads the array as it is.
internal sealed partial class DocState
{
    // 64 nodes: a chunk is a few cache lines, so bringing one up to date is cheap, and the count array stays small.
    private const int ChunkShift = 6;

    // Whether any chunk may be behind. A flag in the state, and the counts in the cache object, so that a state
    // that is only read takes no more room.
    private bool _held;

    /// <summary>The node at an index, with true offsets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref CstNode N(int index)
    {
        if (_held)
        {
            UpdateChunk(index >> ChunkShift);
        }

        return ref _nodes[index];
    }

    /// <summary>A range of nodes with true offsets.</summary>
    internal Span<CstNode> NodeSpan(int start, int length)
    {
        if (_held && length > 0)
        {
            int last = (start + length - 1) >> ChunkShift;
            for (int c = start >> ChunkShift; c <= last; c++)
            {
                UpdateChunk(c);
            }
        }

        return _nodes.AsSpan(start, length);
    }

    /// <summary>The node array with every chunk up to date, for code that works on the array as a whole.</summary>
    internal CstNode[] NodesSettled()
    {
        SettleNodes();
        return _nodes;
    }

    /// <summary>Brings every chunk up to date.</summary>
    internal void SettleNodes()
    {
        if (!_held || _cache?.Behind is not int[] behind)
        {
            return;
        }

        for (int c = 0; c < behind.Length; c++)
        {
            if (behind[c] != 0)
            {
                UpdateChunk(c);
            }
        }

        EditWork.FullSettles++;
    }

    // Brings everything up to date and stops holding moves back: from here on the state may be shared.
    private void ReleaseHeld()
    {
        SettleNodes();
        _held = false;
        if (_cache is not null)
        {
            _cache.Behind = null;
        }
    }

    /// <summary>
    /// Moves the offsets of the nodes from an index on by an amount: the chunk the index is in is moved now, the
    /// chunks behind it are noted to be moved when they are next used.
    /// </summary>
    private void HoldMoveFrom(int from, int count, int by)
    {
        if (from >= count)
        {
            return;
        }

        int[]? behind = _cache?.Behind;
        int lastChunk = (count - 1) >> ChunkShift;
        if (behind is null || behind.Length <= lastChunk)
        {
            // Counts that do not reach the last chunk belong to an array that has since grown.
            SettleNodes();
            behind = EnsureCache().Behind = new int[(_nodes.Length >> ChunkShift) + 1];
        }

        _held = true;
        int chunk = from >> ChunkShift;
        UpdateChunk(chunk);
        int stop = Math.Min((chunk + 1) << ChunkShift, count);
        for (int i = from; i < stop; i++)
        {
            MoveOffsets(ref _nodes[i], by);
        }

        EditWork.NodesMoved += stop - from;
        if (lastChunk > chunk)
        {
            Span<int> rest = behind.AsSpan(chunk + 1, lastChunk - chunk);
            int i = 0;
            if (Vector.IsHardwareAccelerated && rest.Length >= Vector<int>.Count)
            {
                var by1 = new Vector<int>(by);
                for (; i <= rest.Length - Vector<int>.Count; i += Vector<int>.Count)
                {
                    (new Vector<int>(rest.Slice(i)) + by1).CopyTo(rest.Slice(i));
                }
            }

            for (; i < rest.Length; i++)
            {
                rest[i] += by;
            }

            EditWork.ChunksMoved += rest.Length;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UpdateChunk(int chunk)
    {
        int[]? behind = _cache?.Behind;
        if (behind is null || (uint)chunk >= (uint)behind.Length)
        {
            return;
        }

        int by = behind[chunk];
        if (by == 0)
        {
            return;
        }

        behind[chunk] = 0;
        int from = chunk << ChunkShift;
        int to = Math.Min(from + (1 << ChunkShift), _nodes.Length);
        for (int i = from; i < to; i++)
        {
            MoveOffsets(ref _nodes[i], by);
        }

        EditWork.NodesMoved += to - from;
    }

    // The same offsets a splice moves: both ends, and the extra offset a collection or an empty scalar holds.
    private static void MoveOffsets(ref CstNode x, int by)
    {
        x.Start += by;
        x.End += by;
        if (x.Kind is NodeKind.Mapping or NodeKind.Sequence || (x.Kind == NodeKind.Scalar && (x.Flags & NodeFlags.Empty) != 0))
        {
            x.Aux += by;
        }
    }
}
