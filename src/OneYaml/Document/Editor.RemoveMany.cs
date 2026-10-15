using System;
using System.Collections.Generic;
using OneYaml.Parsing;

namespace OneYaml.Documents;

internal static partial class Editor
{
    /// <summary>
    /// Removes several entries of one block mapping in a single pass: one walk over the mapping finds the
    /// keys, and one rebuild applies every cut. The result is byte-identical to removing the entries one
    /// by one. When any entry needs the careful single-entry rules (anchors, a kept block scalar before it,
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
        ref readonly CstNode m = ref s.Node(map);
        if (m.Kind != NodeKind.Mapping || m.IsFlow || (m.Flags & NodeFlags.Pair) != 0 || s.AliasCount != 0 || keys.Count == 0)
        {
            return false;
        }

        int items = m.Count / 2;
        var index = new KeyIndex(keys);
        var cuts = new List<TextEdit>();
        int deleted = 0;
        int previousStop = 0;
        bool runKeep = false;
        bool runBlock = false;
        bool gapMatters = false;
        int k = map + 1;
        for (int i = 0; i < items; i++)
        {
            int v = k + s.Node(k).Size;
            int after = v + s.Node(v).Size;
            ref readonly CstNode last = ref s.Node(after - 1);
            bool keepHere = IsKeepBlock(last);
            bool blockHere = last.Kind == NodeKind.Scalar && (ScalarStyle)last.Style is ScalarStyle.Literal or ScalarStyle.Folded;
            if (gapMatters && s.LineStart(s.EntryStart(k)) != previousStop)
            {
                // Lines left between the cut and this entry would follow a block scalar once the cut is made.
                return false;
            }

            gapMatters = false;
            int wanted = s.Node(k).Kind == NodeKind.Scalar ? index.Find(s, k) : -1;
            if (wanted == KeyIndex.Twice)
            {
                return false;
            }

            if (wanted < 0)
            {
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
            return false;
        }

        next = DocState.Rebuild(s, cuts.ToArray(), ReadOnlySpan<byte>.Empty, limits);
        return true;
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
        stop = s.PastLine(s.Node(value).End);
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
        private readonly uint[] _hashes;
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
            _hashes = new uint[keys.Count];
            _seen = new bool[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                uint h = Hash(keys[i]);
                _hashes[i] = h;
                int at = (int)(h & (uint)(size - 1));
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

                uint h = Hash(text);
                int mask = _slots.Length - 1;
                for (int at = (int)(h & (uint)mask); _slots[at] != 0; at = (at + 1) & mask)
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

        private static uint Hash(ReadOnlySpan<byte> bytes)
        {
            uint h = 2166136261;
            foreach (byte b in bytes)
            {
                h = (h ^ b) * 16777619;
            }

            return h;
        }
    }
}
