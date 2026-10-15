using System;
using System.Collections.Generic;

namespace OneYaml.Documents;

/// <summary>What one primitive edit did to the node array, in the coordinates of the state it was applied to.</summary>
internal enum NodeEditKind : byte
{
    /// <summary>The node at <c>At</c> keeps its place and gets a new subtree; the old subtree's other nodes are gone.</summary>
    Replace,

    /// <summary>The nodes in a range are gone.</summary>
    Remove,

    /// <summary>New nodes appear at a position; the nodes from there on move up.</summary>
    Insert,

    /// <summary>Several ranges are gone at once, given in ascending order of the old coordinates.</summary>
    RemoveRanges,
}

/// <summary>One change to the node array. Views follow their node through these.</summary>
internal readonly struct NodeEdit
{
    public NodeEdit(NodeEditKind kind, int at, int oldCount, int newCount, int[]? ranges = null)
    {
        Kind = kind;
        At = at;
        OldCount = oldCount;
        NewCount = newCount;
        Ranges = ranges;
    }

    public NodeEditKind Kind { get; }

    public int At { get; }

    public int OldCount { get; }

    public int NewCount { get; }

    /// <summary>For <see cref="NodeEditKind.RemoveRanges"/>: start, end pairs of the old coordinates.</summary>
    public int[]? Ranges { get; }

    /// <summary>The change in the number of nodes.</summary>
    public int Delta
    {
        get
        {
            if (Kind != NodeEditKind.RemoveRanges)
            {
                return NewCount - OldCount;
            }

            int removed = 0;
            for (int i = 0; i < Ranges!.Length; i += 2)
            {
                removed += Ranges[i + 1] - Ranges[i];
            }

            return -removed;
        }
    }

    /// <summary>Where a node was before this change is where, afterwards.</summary>
    /// <param name="index">A node of the old array.</param>
    /// <returns>Its index in the new array, or -1 when the change removed it.</returns>
    public int Map(int index)
    {
        switch (Kind)
        {
            case NodeEditKind.Replace:
                if (index <= At)
                {
                    return index;
                }

                return index < At + OldCount ? -1 : index + NewCount - OldCount;
            case NodeEditKind.Remove:
                if (index < At)
                {
                    return index;
                }

                return index < At + OldCount ? -1 : index - OldCount;
            case NodeEditKind.Insert:
                return index >= At ? index + NewCount : index;
            default:
                return MapRanges(index);
        }
    }

    private int MapRanges(int index)
    {
        int[] r = Ranges!;
        int lo = 0;
        int hi = (r.Length / 2) - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (r[mid * 2] <= index)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return index;
        }

        if (index < r[(found * 2) + 1])
        {
            return -1;
        }

        int removed = 0;
        for (int i = 0; i <= found; i++)
        {
            removed += r[(i * 2) + 1] - r[i * 2];
        }

        return index - removed;
    }
}

/// <summary>
/// The edits one operation made, collected while it runs. A state carries one only when the document has
/// handed out views, so documents that are only edited by pointer pay nothing.
/// </summary>
internal sealed class EditTrace
{
    private readonly List<NodeEdit> _edits = [];

    public int Count => _edits.Count;

    public void Add(in NodeEdit edit) => _edits.Add(edit);

    public NodeEdit[] ToArray() => _edits.ToArray();

    public int NetDelta
    {
        get
        {
            int total = 0;
            foreach (NodeEdit e in _edits)
            {
                total += e.Delta;
            }

            return total;
        }
    }
}

/// <summary>
/// A point in the life of a document. A view holds the epoch it was last valid in; epochs link to the next
/// one, so a view that is behind walks the edits made since. Epochs nobody holds are collected.
/// </summary>
internal sealed class EditEpoch
{
    /// <summary>How many edits the document had had when this epoch began.</summary>
    public int Number { get; init; }

    /// <summary>The epoch after this one, or <see langword="null"/> while this is the latest.</summary>
    public EditEpoch? Next { get; set; }

    /// <summary>The edits that led from the previous epoch to this one; <see langword="null"/> when they are not known.</summary>
    public NodeEdit[]? Edits { get; init; }

    /// <summary>Follows an index through this epoch's edits.</summary>
    /// <param name="index">A node of the previous epoch.</param>
    /// <returns>The node in this epoch, or -1.</returns>
    public int Map(int index)
    {
        NodeEdit[]? edits = Edits;
        if (edits is null)
        {
            return -1;
        }

        foreach (NodeEdit e in edits)
        {
            index = e.Map(index);
            if (index < 0)
            {
                return -1;
            }
        }

        return index;
    }
}
