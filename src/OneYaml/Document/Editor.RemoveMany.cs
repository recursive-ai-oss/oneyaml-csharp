using System;
using System.Collections.Generic;
using OneYaml.Parsing;

namespace OneYaml.Documents;

internal static partial class Editor
{
    /// <summary>
    /// Removes several entries of one block mapping in a single pass: one walk over the mapping finds the
    /// keys, and one rebuild applies every cut. The result is byte-identical to removing the entries one
    /// by one. When any entry needs the careful single-entry rules (an alias anywhere in the stream, a kept block scalar before it,
    /// a last line without a break, a line that does not start with the entry, an ambiguous key), nothing is
    /// done and the caller removes the entries one at a time.
    /// </summary>
    /// <param name="s">The current state.</param>
    /// <param name="limits">Limits for the new state.</param>
    /// <param name="map">The block mapping.</param>
    /// <param name="keys">The unescaped keys to remove. Keys the mapping does not have are ignored.</param>
    /// <param name="next">The next state, or <paramref name="s"/> when no key matched.</param>
    /// <returns><see langword="true"/> when the batch was applied.</returns>
    public static bool TryRemoveMany(DocState s, YamlLimits limits, int map, List<byte[]> keys, out DocState next)
    {
        next = s;
        ref readonly CstNode m = ref s.NodeH(map);
        if (m.Kind != NodeKind.Mapping || m.IsFlow || (m.Flags & NodeFlags.Pair) != 0 || s.AliasCount != 0 || keys.Count == 0)
        {
            return false;
        }

        int items = m.Count / 2;
        var index = new KeyIndex(keys);
        var cuts = new List<TextEdit>();
        List<int>? gone = s.Trace is null ? null : [];
        int deleted = 0;
        int lastWanted = -1;
        int previousStop = 0;
        bool runKeep = false;
        bool runBlock = false;
        bool gapMatters = false;
        bool cutSincePrevious = false;
        bool keptBare = false;
        int k = map + 1;
        for (int i = 0; i < items; i++)
        {
            int v = k + s.NodeS(k).Size;
            int after = v + s.NodeS(v).Size;
            ref readonly CstNode last = ref s.NodeH(after - 1);
            bool keepHere = IsKeepBlock(last);
            bool blockHere = last.Kind == NodeKind.Scalar && (ScalarStyle)last.Style is ScalarStyle.Literal or ScalarStyle.Folded;
            if (gapMatters && s.LineStart(s.EntryStart(k)) != previousStop)
            {
                // Lines left between the cut and this entry would follow a block scalar once the cut is made.
                return false;
            }

            gapMatters = false;
            int wanted = s.NodeS(k).Kind == NodeKind.Scalar ? index.Find(s, k) : -1;
            if (wanted == KeyIndex.Twice)
            {
                return false;
            }

            if (wanted < 0)
            {
                // A key with no value, cuts, then an entry that starts with a colon: with the entries between gone the
                // colon would turn into the key's value. The one-at-a-time removal knows how to keep both.
                if (cutSincePrevious && keptBare && (s.NodeS(k).Flags & NodeFlags.Empty) != 0)
                {
                    return false;
                }

                cutSincePrevious = false;
                keptBare = (last.Flags & NodeFlags.Bare) != 0;
                runKeep = keepHere;
                runBlock = blockHere;
            }
            else
            {
                // A kept block scalar right before the entry takes the blank lines after it into its value.
                if (runKeep || !TryCut(s, k, v, previousStop, out int start, out int stop))
                {
                    return false;
                }

                deleted++;
                cutSincePrevious = true;
                gone?.Add(k);
                gone?.Add(after);
                lastWanted = Math.Max(lastWanted, wanted);
                gapMatters = runBlock;
                runKeep |= keepHere;
                runBlock |= blockHere;
                if (cuts.Count > 0 && cuts[^1].End == start)
                {
                    cuts[^1] = new TextEdit(cuts[^1].Start, stop, 0, 0);
                }
                else
                {
                    cuts.Add(new TextEdit(start, stop, 0, 0));
                }

                previousStop = stop;
            }

            k = after;
        }

        if (gapMatters && previousStop != s.Length)
        {
            return false;
        }

        if (deleted == 0)
        {
            return true;
        }

        if (deleted == items)
        {
            return RemoveAll(s, limits, map, keys, lastWanted, out next);
        }

        next = DocState.Rebuild(s, cuts.ToArray(), ReadOnlySpan<byte>.Empty, limits);
        s.Trace?.Add(new NodeEdit(NodeEditKind.RemoveRanges, 0, 0, 0, gone!.ToArray()));
        return true;
    }

    // Every entry goes: the mapping stays, empty, as when the entries are removed one at a time. All entries but
    // the one removed last are cut in one pass, and the last removal is the ordinary single-entry one.
    private static bool RemoveAll(DocState s, YamlLimits limits, int map, List<byte[]> keys, int lastWanted, out DocState next)
    {
        next = s;
        DocState mid = s;
        if (s.NodeS(map).Count > 2)
        {
            var rest = new List<byte[]>(keys.Count - 1);
            for (int i = 0; i < keys.Count; i++)
            {
                if (i != lastWanted)
                {
                    rest.Add(keys[i]);
                }
            }

            if (!TryRemoveMany(s, limits, map, rest, out mid) || ReferenceEquals(mid, s))
            {
                return false;
            }
        }

        try
        {
            int value = map + 1;
            value += mid.NodeS(value).Size;
            next = Remove(mid, limits, value);
            return true;
        }
        finally
        {
            if (!ReferenceEquals(mid, s))
            {
                mid.Dispose();
            }
        }
    }

    // The cut for one block entry that starts its own line: the same lines the single-entry removal takes.
    private static bool TryCut(DocState s, int key, int value, int previousStop, out int start, out int stop)
    {
        start = 0;
        stop = 0;
        ReadOnlySpan<byte> src = s.Text;
        int entryStart = s.EntryStart(key);
        if (!s.StartsLine(entryStart))
        {
            return false;
        }

        start = s.OwnedCommentsStart(s.LineStart(entryStart));
        stop = s.PastLine(s.NodeH(value).End);
        if (start < previousStop)
        {
            return false;
        }

        // The last line has no break: the single-entry rules also take the break before it.
        return !(stop == s.Length && stop > 0 && !DocState.IsBreak(src[stop - 1]));
    }

    /// <summary>Finds which of a few wanted keys a mapping key is, by hash, without making strings.</summary>
    private sealed class KeyIndex
    {
        /// <summary>The result when a wanted key matched two keys of the mapping.</summary>
        public const int Twice = -2;

        private readonly List<byte[]> _keys;
        private readonly int[] _slots;
        private readonly int[] _hashes;
        private readonly bool[] _seen;

        public KeyIndex(List<byte[]> keys)
        {
            _keys = keys;
            int size = 8;
            while (size < keys.Count * 2)
            {
                size <<= 1;
            }

            _slots = new int[size];
            _hashes = new int[keys.Count];
            _seen = new bool[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                int h = Hash(keys[i]);
                _hashes[i] = h;
                int at = h & (size - 1);
                while (_slots[at] != 0)
                {
                    at = (at + 1) & (size - 1);
                }

                _slots[at] = i + 1;
            }
        }

        /// <summary>The index of the wanted key that the scalar key equals, -1 for none, or <see cref="Twice"/>.</summary>
        public int Find(DocState s, int key)
        {
            byte[]? rented = null;
            try
            {
                ReadOnlySpan<byte> text;
                if (s.IsRaw(key))
                {
                    text = s.RawContent(key);
                }
                else
                {
                    text = s.DecodedScalar(key, out rented);
                }

                int h = Hash(text);
                int mask = _slots.Length - 1;
                for (int at = h & mask; _slots[at] != 0; at = (at + 1) & mask)
                {
                    int w = _slots[at] - 1;
                    if (_hashes[w] == h && text.SequenceEqual(_keys[w]))
                    {
                        if (_seen[w])
                        {
                            return Twice;
                        }

                        _seen[w] = true;
                        return w;
                    }
                }

                return -1;
            }
            finally
            {
                DocState.ReleaseDecoded(rented);
            }
        }

        // The BCL hash is seeded randomly for each process, so member names chosen to collide
        // under a known hash cannot push every lookup into one probe chain.
        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            var h = new HashCode();
            h.AddBytes(bytes);
            return h.ToHashCode();
        }
    }
}
