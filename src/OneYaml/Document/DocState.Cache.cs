using System;
using System.Buffers;
using System.Numerics;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// Counters of the work the edit engine does on large mappings. Tests read them to show that the work
/// per operation does not grow with the size of the mapping.
/// </summary>
internal static class EditWork
{
    /// <summary>Keys looked at while finding a key by its text.</summary>
    [ThreadStatic]
    internal static long KeysExamined;

    /// <summary>Lookups the key index answered.</summary>
    [ThreadStatic]
    internal static long IndexedLookups;

    /// <summary>Entries and nodes looked at while working out the layout new text should copy.</summary>
    [ThreadStatic]
    internal static long LayoutSteps;
}

internal sealed partial class DocState
{
    /// <summary>What <see cref="IndexedFind"/> returns when the mapping has no index and must be searched in order.</summary>
    internal const int NotIndexed = -2;

    // A mapping is indexed from this many child nodes (keys and values) on: below it, reading the keys in order
    // costs less than the index would. Tests lower the numbers so that small documents use the same code.
    internal static int IndexMinNodes = 128;

    // Layout results are remembered for documents of at least this many nodes, or for a scope of that many children.
    internal static int LayoutMinNodes = 512;

    private MapCache? _cache;

    /// <summary>How many key indexes this state holds: none until a large mapping has been searched twice.</summary>
    internal int BuiltIndexes
    {
        get
        {
            int built = 0;
            for (int i = 0; i < (_cache?.TableCount ?? 0); i++)
            {
                built += _cache!.Tables[i].Slots is null ? 0 : 1;
            }

            return built;
        }
    }

    /// <summary>Makes every use of the remembered layouts and key indexes check itself against a fresh computation: tests turn it on.</summary>
    internal static bool VerifyCaches;

    // Checks on large mappings stay within this many children, so a test that edits a huge mapping does not turn quadratic.
    internal const int VerifyMaxNodes = 8192;

    /// <summary>Tells whether a key node's text is the token.</summary>
    internal bool KeyTextMatches(int key, ReadOnlySpan<byte> token)
    {
        if (IsRaw(key))
        {
            return RawContent(key).SequenceEqual(token);
        }

        ReadOnlySpan<byte> text = DecodedScalar(key, out byte[]? rented);
        try
        {
            return text.SequenceEqual(token);
        }
        finally
        {
            ReleaseDecoded(rented);
        }
    }

    private static int HashOfText(ReadOnlySpan<byte> text)
    {
        // The hash is seeded per process: with a fixed one an input could choose keys that all land in one chain.
        HashCode h = default;
        h.AddBytes(text);
        return h.ToHashCode();
    }

    private int KeyTextHash(int key)
    {
        if (IsRaw(key))
        {
            return HashOfText(RawContent(key));
        }

        ReadOnlySpan<byte> text = DecodedScalar(key, out byte[]? rented);
        try
        {
            return HashOfText(text);
        }
        finally
        {
            ReleaseDecoded(rented);
        }
    }

    private bool SameKeyText(int a, int b)
    {
        if (IsRaw(a))
        {
            return KeyTextMatches(b, RawContent(a));
        }

        ReadOnlySpan<byte> text = DecodedScalar(a, out byte[]? rented);
        try
        {
            return KeyTextMatches(b, text);
        }
        finally
        {
            ReleaseDecoded(rented);
        }
    }

    // ---- Finding a key in a large mapping ----

    /// <summary>
    /// Finds the value of the key whose text is the token, in a mapping with many entries, through an index built
    /// when the mapping is looked in a second time. The index exists only while every key text in the mapping is
    /// different, so a match is the only one.
    /// </summary>
    /// <param name="container">A mapping node with at least <see cref="IndexMinNodes"/> children.</param>
    /// <param name="token">The text to find.</param>
    /// <returns>The value node, -1 when no key has that text, or <see cref="NotIndexed"/> when the caller must search in order.</returns>
    internal int IndexedFind(int container, ReadOnlySpan<byte> token)
    {
        KeyTable? t = _cache?.Find(container);
        if (t is null)
        {
            // The first look costs what it always did; only a mapping that is searched again is indexed.
            (_cache ??= new MapCache()).Note(container);
            return NotIndexed;
        }

        if (t.Refused)
        {
            return NotIndexed;
        }

        if (t.Slots is null)
        {
            if (!BuildTable(t))
            {
                return NotIndexed;
            }
        }

        int hash = HashOfText(token);
        int mask = t.Capacity - 1;
        int[] slots = t.Slots!;
        int[] hashes = t.Hashes!;
        long examined = 0;
        int result = -1;
        for (int slot = hash & mask; ; slot = (slot + 1) & mask)
        {
            int rel = slots[slot];
            if (rel == 0)
            {
                break;
            }

            if (rel > 0 && hashes[slot] == hash)
            {
                examined++;
                int key = container + rel;
                if (KeyTextMatches(key, token))
                {
                    result = key + _nodes[key].Size;
                    break;
                }
            }
        }

        EditWork.KeysExamined += examined;
        EditWork.IndexedLookups++;
        return result;
    }

    private bool BuildTable(KeyTable t)
    {
        int container = t.Node;
        int pairs = _nodes[container].Count / 2;
        int capacity = 64;
        while (capacity < pairs * 2)
        {
            capacity <<= 1;
        }

        t.Slots = ArrayPool<int>.Shared.Rent(capacity);
        t.Hashes = ArrayPool<int>.Shared.Rent(capacity);
        Array.Clear(t.Slots, 0, capacity);
        t.Capacity = capacity;
        t.Used = 0;
        t.Tombs = 0;
        int c = container + 1;
        for (int i = 0; i < pairs; i++)
        {
            int key = c;
            int value = key + _nodes[key].Size;
            if (_nodes[key].Kind == NodeKind.Scalar && !InsertKey(t, key))
            {
                t.Release();
                t.Refused = true;
                return false;
            }

            c = value + _nodes[value].Size;
        }

        EditWork.KeysExamined += pairs;
        return true;
    }

    // Adds a key to the index. Returns false when another key has the same text: then the first match in the
    // mapping's order is not the only one, and the mapping is searched in order instead.
    private bool InsertKey(KeyTable t, int key)
    {
        if (t.Slots is null)
        {
            return false;
        }

        if ((t.Used + t.Tombs + 1) * 8 > t.Capacity * 5)
        {
            Rehash(t);
        }

        int hash = KeyTextHash(key);
        int[] slots = t.Slots!;
        int[] hashes = t.Hashes!;
        int mask = t.Capacity - 1;
        int tomb = -1;
        for (int slot = hash & mask; ; slot = (slot + 1) & mask)
        {
            int rel = slots[slot];
            if (rel == 0)
            {
                int at = tomb >= 0 ? tomb : slot;
                if (tomb >= 0)
                {
                    t.Tombs--;
                }

                slots[at] = key - t.Node;
                hashes[at] = hash;
                t.Used++;
                return true;
            }

            if (rel < 0)
            {
                if (tomb < 0)
                {
                    tomb = slot;
                }
            }
            else if (hashes[slot] == hash && SameKeyText(t.Node + rel, key))
            {
                return false;
            }
        }
    }

    private static void Rehash(KeyTable t)
    {
        int capacity = 64;
        while (capacity < (t.Used + 1) * 2)
        {
            capacity <<= 1;
        }

        int[] slots = ArrayPool<int>.Shared.Rent(capacity);
        int[] hashes = ArrayPool<int>.Shared.Rent(capacity);
        Array.Clear(slots, 0, capacity);
        int mask = capacity - 1;
        for (int i = 0; i < t.Capacity; i++)
        {
            int rel = t.Slots![i];
            if (rel <= 0)
            {
                continue;
            }

            int hash = t.Hashes![i];
            int slot = hash & mask;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            slots[slot] = rel;
            hashes[slot] = hash;
        }

        ArrayPool<int>.Shared.Return(t.Slots!);
        ArrayPool<int>.Shared.Return(t.Hashes!);
        t.Slots = slots;
        t.Hashes = hashes;
        t.Capacity = capacity;
        t.Tombs = 0;
    }

    // Takes a key out of the index: its slot stays as a marker so that the chains through it still hold.
    private bool RemoveKey(KeyTable t, int key)
    {
        if (t.Slots is null)
        {
            return true;
        }

        int hash = KeyTextHash(key);
        int rel = key - t.Node;
        int mask = t.Capacity - 1;
        for (int slot = hash & mask; ; slot = (slot + 1) & mask)
        {
            int r = t.Slots[slot];
            if (r == 0)
            {
                return false;
            }

            if (r == rel && t.Hashes![slot] == hash)
            {
                t.Slots[slot] = -1;
                t.Used--;
                t.Tombs++;
                return true;
            }
        }
    }

    private static void ShiftTable(KeyTable t, int fromRel, int delta)
    {
        if (t.Slots is null || delta == 0)
        {
            return;
        }

        // One pass over the slots, several at a time. Free and removed slots hold 0 and -1, below any threshold.
        Span<int> slots = t.Slots.AsSpan(0, t.Capacity);
        var limit = new Vector<int>(fromRel);
        var by = new Vector<int>(delta);
        int i = 0;
        for (; i <= slots.Length - Vector<int>.Count; i += Vector<int>.Count)
        {
            var v = new Vector<int>(slots.Slice(i));
            (v + Vector.BitwiseAnd(Vector.GreaterThanOrEqual(v, limit), by)).CopyTo(slots.Slice(i));
        }

        for (; i < slots.Length; i++)
        {
            if (slots[i] >= fromRel)
            {
                slots[i] += delta;
            }
        }
    }

    // ---- The layout new text copies ----

    internal bool LayoutRemembered(int scope) => _nodes.Length != 0 && (NodeCount >= LayoutMinNodes || (scope >= 0 && _nodes[scope].Count >= IndexMinNodes));

    internal bool TryGetLayoutMemo(int scope, out int step, out int seqOffset)
    {
        step = seqOffset = 0;
        return _cache is not null && _cache.TryGetLayout(scope, out step, out seqOffset);
    }

    internal void StoreLayoutMemo(int scope, int step, int seqOffset) => (_cache ??= new MapCache()).StoreLayout(scope, step, seqOffset);

    // ---- Keeping both up to date across a splice ----

    private static bool HasCollection(ReadOnlySpan<CstNode> nodes)
    {
        foreach (CstNode n in nodes)
        {
            if (n.Kind is NodeKind.Mapping or NodeKind.Sequence)
            {
                return true;
            }
        }

        return false;
    }

    // Before the arrays change: the old keys of the region leave the index, indexes of the mappings around it shift,
    // and remembered layouts are dropped unless the edit touches no collection (then nothing they were worked out
    // from can have changed).
    private void CacheBeforeSplice(DocState sub, int n, int first, int end, int d)
    {
        MapCache? cache = _cache;
        if (cache is null)
        {
            return;
        }

        bool neutral = !HasCollection(_nodes.AsSpan(first, end - first)) && !HasCollection(sub._nodes.AsSpan(1, sub.NodeCount - 1));
        cache.ShiftLayouts(neutral, end, d);
        for (int i = cache.TableCount - 1; i >= 0; i--)
        {
            KeyTable t = cache.Tables[i];
            if (t.Node >= end)
            {
                t.Node += d;
                continue;
            }

            if (t.Node >= first)
            {
                // The mapping is inside what is replaced; the new text brings its own nodes.
                cache.RemoveTable(i);
                continue;
            }

            if (t.Node + _nodes[t.Node].Size <= first || t.Slots is null)
            {
                continue;
            }

            if (t.Node == n)
            {
                bool ok = true;
                int c = first;
                while (c < end)
                {
                    // Children alternate key and value from the region's first node, which is a key.
                    if (_nodes[c].Kind == NodeKind.Scalar && !RemoveKey(t, c))
                    {
                        ok = false;
                        break;
                    }

                    c += _nodes[c].Size;
                    c += _nodes[c].Size;
                }

                if (!ok)
                {
                    t.Release();
                    t.Refused = true;
                    continue;
                }
            }

            ShiftTable(t, end - t.Node, d);
        }
    }

    // After the arrays changed: the keys the region brought go into the index of its mapping. Returns whether the
    // new keys are known to differ from every other key in the mapping, so that no duplicate check is needed.
    private bool CacheAfterSplice(int n, int first, int newCount)
    {
        MapCache? cache = _cache;
        KeyTable? t = cache?.Find(n);
        if (t is null || t.Refused || t.Slots is null)
        {
            return false;
        }

        bool unique = true;
        int c = first;
        int stop = first + newCount;
        while (c < stop)
        {
            if (_nodes[c].Kind == NodeKind.Scalar)
            {
                if (!InsertKey(t, c))
                {
                    t.Release();
                    t.Refused = true;
                    return false;
                }

                // A key that reads as a number, a boolean or null can equal a key of another spelling.
                if (!IsPlainlyString(c) && KindOf(c, out _) != ScalarKind.String)
                {
                    unique = false;
                }
            }

            c += _nodes[c].Size;
            c += _nodes[c].Size;
        }

        return unique;
    }

    // A copy for the state an edit starts from, so that it need not work everything out again.
    private MapCache? CloneCache() => _cache?.Clone();

    /// <summary>Checks the remembered layouts and key indexes against fresh work: for tests.</summary>
    internal void VerifyCache()
    {
        MapCache? cache = _cache;
        if (cache is null)
        {
            return;
        }

        long counted = EditWork.KeysExamined;
        long lookups = EditWork.IndexedLookups;
        foreach (KeyTable t in cache.Tables.AsSpan(0, cache.TableCount))
        {
            if (t.Slots is null || _nodes[t.Node].Count > VerifyMaxNodes)
            {
                continue;
            }

            int expected = 0;
            int pairs = _nodes[t.Node].Count / 2;
            int c = t.Node + 1;
            for (int i = 0; i < pairs; i++)
            {
                int key = c;
                int value = key + _nodes[key].Size;
                if (_nodes[key].Kind == NodeKind.Scalar)
                {
                    expected++;
                    if (IndexedFind(t.Node, RawOrDecoded(key)) != value)
                    {
                        throw new InvalidOperationException("The key index does not find the key at node " + key);
                    }
                }

                c = value + _nodes[value].Size;
            }

            if (t.Used != expected)
            {
                throw new InvalidOperationException($"The key index holds {t.Used} keys, the mapping {expected}.");
            }
        }

        EditWork.KeysExamined = counted;
        EditWork.IndexedLookups = lookups;
    }

    private byte[] RawOrDecoded(int key)
    {
        ReadOnlySpan<byte> text = DecodedScalar(key, out byte[]? rented);
        try
        {
            return text.ToArray();
        }
        finally
        {
            ReleaseDecoded(rented);
        }
    }

    private void ReleaseCache()
    {
        _cache?.Release();
        _cache = null;
    }
}

/// <summary>The index of the keys of one large mapping.</summary>
internal sealed class KeyTable
{
    /// <summary>The mapping's node index in the state the table belongs to.</summary>
    public int Node;

    /// <summary>Set when two keys have the same text or the index would not pay: the mapping is searched in order.</summary>
    public bool Refused;

    /// <summary>Per slot: the key's node index minus the mapping's, 0 for a free slot, -1 for a removed key.</summary>
    public int[]? Slots;

    /// <summary>Per slot: the hash of the key's text.</summary>
    public int[]? Hashes;

    public int Capacity;

    public int Used;

    public int Tombs;

    public KeyTable Clone()
    {
        var copy = new KeyTable { Node = Node, Refused = Refused, Capacity = Capacity, Used = Used, Tombs = Tombs };
        if (Slots is not null)
        {
            copy.Slots = ArrayPool<int>.Shared.Rent(Capacity);
            copy.Hashes = ArrayPool<int>.Shared.Rent(Capacity);
            Array.Copy(Slots, copy.Slots, Capacity);
            Array.Copy(Hashes!, copy.Hashes, Capacity);
        }

        return copy;
    }

    public void Release()
    {
        if (Slots is not null)
        {
            ArrayPool<int>.Shared.Return(Slots);
            ArrayPool<int>.Shared.Return(Hashes!);
            Slots = null;
            Hashes = null;
        }

        Used = Tombs = Capacity = 0;
    }
}

/// <summary>
/// What a state remembers besides its tree: the key indexes of its large mappings, the layouts worked out for
/// them, and the expansion sizes of a read through aliases. One object holds all three, made when the first is
/// needed, so that a state nobody edits or searches costs one reference.
/// </summary>
internal sealed class MapCache
{
    private const int MaxTables = 6;
    private const int MaxLayouts = 4;

    public KeyTable[] Tables = [];
    public int TableCount;
    private int _evict;

    // A few remembered layouts, held in fields: one edit of a mid-size document remembers one, and an array would cost more than it saves.
    private LayoutSlot _l0, _l1, _l2, _l3;
    private int _layouts;
    private int _layoutNext;

    private struct LayoutSlot
    {
        public int Scope;
        public int Step;
        public int Seq;
    }

    private ref LayoutSlot Slot(int i)
    {
        switch (i)
        {
            case 0: return ref _l0;
            case 1: return ref _l1;
            case 2: return ref _l2;
            default: return ref _l3;
        }
    }

    /// <summary>The expansion sizes of a read through aliases, returned to the pool with the state.</summary>
    public NodeReplay.ExpansionSizes? Expansion;

    public KeyTable? Find(int node)
    {
        for (int i = 0; i < TableCount; i++)
        {
            if (Tables[i].Node == node)
            {
                return Tables[i];
            }
        }

        return null;
    }

    // Records that a large mapping was searched once.
    public void Note(int node)
    {
        var t = new KeyTable { Node = node };
        if (TableCount < MaxTables)
        {
            // Sized to what is in use: most edited documents note a single mapping.
            if (TableCount == Tables.Length)
            {
                Array.Resize(ref Tables, TableCount == 0 ? 1 : Math.Min(MaxTables, TableCount * 2));
            }

            Tables[TableCount++] = t;
            return;
        }

        Tables[_evict].Release();
        Tables[_evict] = t;
        _evict = (_evict + 1) % MaxTables;
    }

    public void RemoveTable(int i)
    {
        Tables[i].Release();
        Tables[i] = Tables[--TableCount];
        Tables[TableCount] = null!;
    }

    public bool TryGetLayout(int scope, out int step, out int seq)
    {
        for (int i = 0; i < _layouts; i++)
        {
            ref LayoutSlot slot = ref Slot(i);
            if (slot.Scope == scope)
            {
                step = slot.Step;
                seq = slot.Seq;
                return true;
            }
        }

        step = seq = 0;
        return false;
    }

    public void StoreLayout(int scope, int step, int seq)
    {
        int at;
        if (_layouts < MaxLayouts)
        {
            at = _layouts++;
        }
        else
        {
            at = _layoutNext;
            _layoutNext = (_layoutNext + 1) % MaxLayouts;
        }

        ref LayoutSlot slot = ref Slot(at);
        slot.Scope = scope;
        slot.Step = step;
        slot.Seq = seq;
    }

    public void ShiftLayouts(bool neutral, int end, int delta)
    {
        if (!neutral)
        {
            _layouts = 0;
            _layoutNext = 0;
            return;
        }

        for (int i = 0; i < _layouts; i++)
        {
            ref LayoutSlot slot = ref Slot(i);
            if (slot.Scope >= end)
            {
                slot.Scope += delta;
            }
        }
    }

    // The indexes and layouts, for the state an edit starts from. The expansion sizes belong to the old text and stay behind.
    public MapCache? Clone()
    {
        if (TableCount == 0 && _layouts == 0)
        {
            return null;
        }

        var copy = new MapCache { TableCount = TableCount, _evict = _evict, _layouts = _layouts, _layoutNext = _layoutNext, _l0 = _l0, _l1 = _l1, _l2 = _l2, _l3 = _l3 };
        if (TableCount > 0)
        {
            copy.Tables = new KeyTable[Tables.Length];
            for (int i = 0; i < TableCount; i++)
            {
                copy.Tables[i] = Tables[i].Clone();
            }
        }

        return copy;
    }

    public void Release()
    {
        for (int i = 0; i < TableCount; i++)
        {
            Tables[i].Release();
        }

        TableCount = 0;
        _layouts = 0;
        Expansion?.Release();
        Expansion = null;
    }
}
