using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
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

    /// <summary>Nodes whose offsets an edit rewrote one by one.</summary>
    [ThreadStatic]
    internal static long NodesMoved;

    /// <summary>Chunks of nodes whose move an edit only noted.</summary>
    [ThreadStatic]
    internal static long ChunksMoved;

    /// <summary>Times every chunk of a node array had to be brought up to date at once.</summary>
    [ThreadStatic]
    internal static long FullSettles;
}

internal sealed partial class DocState
{
    /// <summary>What <see cref="IndexedFind"/> returns when the mapping has no index and must be searched in order.</summary>
    internal const int NotIndexed = -2;

    // A mapping is indexed from this many child nodes (keys and values) on: below it, reading the keys in order
    // costs less than the index would. Tests lower the numbers so that small documents use the same code.
    internal static int IndexMinNodes = 512;

    // A layout is remembered only when working it out took at least this many steps; a cheaper one is not worth a cache.
    internal static int LayoutMinSteps = 2048;

    // An index is built once the searches in order of one mapping have cost this many times the mapping's own size:
    // the build costs about two and a half searches through all of it, so a mapping read a few times is never indexed.
    internal const int BuildAfterScans = 3;

    private MapCache? _cache;

    // Several threads may read one document at once, so the cache is made with a compare-and-swap.
    private MapCache EnsureCache()
    {
        MapCache? c = _cache;
        if (c is null)
        {
            c = new MapCache();
            MapCache? other = Interlocked.CompareExchange(ref _cache, c, null);
            if (other is not null)
            {
                c = other;
            }
        }

        return c;
    }

    // An edit that works on a copy takes the cache along: the original is dropped when the edit succeeds, and when it
    // does not the original merely loses a cache, which it builds again when it needs one.
    private MapCache? TakeCache()
    {
        MapCache? c = _cache;
        _cache = null;
        return c;
    }

    /// <summary>How many key indexes this state holds: none until a large mapping has been searched often enough.</summary>
    internal int BuiltIndexes => _cache?.BuiltCount ?? 0;

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
        KeyTable? t = _cache?.FindTable(container);
        if (t is null || t.Refused || t.Slots is null)
        {
            return NotIndexed;
        }

        int hash = HashOfText(token);
        int mask = t.Capacity - 1;
        int[] slots = t.Slots;
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
                    result = key + N(key).Size;
                    break;
                }
            }
        }

        _cache!.Touch(t);
        EditWork.KeysExamined += examined;
        EditWork.IndexedLookups++;
        return result;
    }

    /// <summary>
    /// Tells the state that a search in order through a large mapping looked at this many entries. Once the searches of
    /// one mapping have cost more than building its index would, the index is built. Reads call this, so it touches
    /// only what a reader may touch: plain arrays, published with a compare-and-swap.
    /// </summary>
    internal void NoteScan(int container, int pairsScanned)
    {
        MapCache c = EnsureCache();
        int slot = c.Charge(container, pairsScanned, out long work);
        if (slot >= 0 && work >= (long)BuildAfterScans * (N(container).Count / 2))
        {
            c.Publish(slot, BuildTable(container, Exclusive));
        }
    }

    // The index of a mapping, or a refused one when two of its keys have the same text. A state that only its edit
    // owns may take the arrays from the pool: a read never does, as several threads can read one document, and an
    // array that goes back to the pool twice is handed to two renters.
    private KeyTable BuildTable(int container, bool pooled)
    {
        int pairs = N(container).Count / 2;
        int capacity = 64;
        while (capacity * 5 < pairs * 8)
        {
            capacity <<= 1;
        }

        var t = new KeyTable { Node = container, Capacity = capacity, Pooled = pooled };
        if (pooled)
        {
            t.Slots = ArrayPool<int>.Shared.Rent(capacity);
            t.Hashes = ArrayPool<int>.Shared.Rent(capacity);
            Array.Clear(t.Slots, 0, capacity);
        }
        else
        {
            t.Slots = new int[capacity];
            t.Hashes = new int[capacity];
        }

        int c = container + 1;
        for (int i = 0; i < pairs; i++)
        {
            int key = c;
            int value = key + N(key).Size;
            if (N(key).Kind == NodeKind.Scalar && !InsertKey(t, key))
            {
                t.Release();
                t.Refused = true;
                break;
            }

            c = value + N(value).Size;
        }

        EditWork.KeysExamined += pairs;
        return t;
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

        // Only an edit grows a table, and an edit's tables are its own: the arrays go through the pool.
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

        t.ReturnArrays();
        t.Slots = slots;
        t.Hashes = hashes;
        t.Pooled = true;
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

    internal bool TryGetLayoutMemo(int scope, out int step, out int seqOffset)
    {
        step = seqOffset = 0;
        return _cache is not null && _cache.TryGetLayout(scope, out step, out seqOffset);
    }

    internal void StoreLayoutMemo(int scope, int step, int seqOffset) => EnsureCache().StoreLayout(scope, step, seqOffset);

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

        bool neutral = !HasCollection(NodeSpan(first, end - first)) && !HasCollection(sub._nodes.AsSpan(1, sub.NodeCount - 1));
        cache.ShiftLayouts(neutral, end, d);
        for (int i = 0; i < MapCache.Slots; i++)
        {
            int node = cache.NodeAt(i);
            if (node < 0)
            {
                continue;
            }

            if (node >= end)
            {
                cache.MoveSlot(i, d);
                continue;
            }

            if (node >= first)
            {
                // The mapping is inside what is replaced; the new text brings its own nodes.
                cache.ClearSlot(i);
                continue;
            }

            KeyTable? t = cache.TableAt(i);
            if (t is null || node + N(node).Size <= first || t.Slots is null)
            {
                continue;
            }

            if (node == n)
            {
                bool ok = true;
                int c = first;
                while (c < end)
                {
                    // Children alternate key and value from the region's first node, which is a key.
                    if (N(c).Kind == NodeKind.Scalar && !RemoveKey(t, c))
                    {
                        ok = false;
                        break;
                    }

                    c += N(c).Size;
                    c += N(c).Size;
                }

                if (!ok)
                {
                    t.Release();
                    t.Refused = true;
                    continue;
                }
            }

            ShiftTable(t, end - node, d);
        }
    }

    // After the arrays changed: the keys the region brought go into the index of its mapping. Returns whether the
    // new keys are known to differ from every other key in the mapping, so that no duplicate check is needed.
    private bool CacheAfterSplice(int n, int first, int newCount)
    {
        MapCache? cache = _cache;
        KeyTable? t = cache?.FindTable(n);
        if (t is null || t.Refused || t.Slots is null)
        {
            return false;
        }

        bool unique = true;
        int c = first;
        int stop = first + newCount;
        while (c < stop)
        {
            if (N(c).Kind == NodeKind.Scalar)
            {
                if (!InsertKey(t, c))
                {
                    t.Release();
                    t.Refused = true;
                    return false;
                }

                // The index only knows that the text is new. A key that reads as a number, a boolean or null can still equal
                // a key of another spelling (`1` and `0x1`), so the full duplicate check decides for those.
                if (!IsPlainlyString(c) && KindOf(c, out _) != ScalarKind.String)
                {
                    unique = false;
                }
            }

            c += N(c).Size;
            c += N(c).Size;
        }

        return unique;
    }

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
        for (int slotIndex = 0; slotIndex < MapCache.Slots; slotIndex++)
        {
            KeyTable? t = cache.TableAt(slotIndex);
            if (t is null || t.Slots is null || cache.NodeAt(slotIndex) != t.Node || N(t.Node).Count > VerifyMaxNodes)
            {
                continue;
            }

            int expected = 0;
            int pairs = N(t.Node).Count / 2;
            int c = t.Node + 1;
            for (int i = 0; i < pairs; i++)
            {
                int key = c;
                int value = key + N(key).Size;
                if (N(key).Kind == NodeKind.Scalar)
                {
                    expected++;
                    if (IndexedFind(t.Node, RawOrDecoded(key)) != value)
                    {
                        throw new InvalidOperationException("The key index does not find the key at node " + key);
                    }
                }

                c = value + N(value).Size;
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

    /// <summary>Set when two keys have the same text: the mapping is searched in order.</summary>
    public bool Refused;

    /// <summary>Per slot: the key's node index minus the mapping's, 0 for a free slot, -1 for a removed key.</summary>
    public int[]? Slots;

    /// <summary>Per slot: the hash of the key's text.</summary>
    public int[]? Hashes;

    public int Capacity;

    public int Used;

    public int Tombs;

    /// <summary>When the table last answered a lookup, in the cache's count; only kept up once the cache is full.</summary>
    public int LastUse;

    /// <summary>Set when the arrays came from the pool and go back to it.</summary>
    public bool Pooled;

    // Gives the arrays back if they are the pool's. Only the edit side or the disposal of a state calls this.
    public void ReturnArrays()
    {
        if (Pooled && Slots is not null)
        {
            ArrayPool<int>.Shared.Return(Slots);
            ArrayPool<int>.Shared.Return(Hashes!);
        }

        Pooled = false;
    }

    public void Release()
    {
        ReturnArrays();
        Slots = null;
        Hashes = null;
        Used = Tombs = Capacity = 0;
    }
}

/// <summary>
/// What a state remembers besides its tree: the work spent searching its large mappings in order and the key indexes
/// built from it, the layouts worked out for edits, and the expansion sizes of a read through aliases. One object
/// holds all of it, made when the first is needed, so that a state nobody searches or edits costs one reference.
/// </summary>
/// <remarks>
/// Reads may run on several threads at once, so what they touch is plain arrays of fixed size, written with
/// compare-and-swap or in a way where a lost update only delays an index. Nothing a read touches goes back to a pool.
/// Edits are not concurrent with anything and change the tables in place.
/// </remarks>
internal sealed class MapCache
{
    /// <summary>How many large mappings a state follows at once.</summary>
    public const int Slots = 16;

    // A full cache gives up a built table only when it has gone unused for this many lookups.
    private const int StaleAfter = 4096;

    private const int MaxLayouts = 4;

    // Per slot: (node + 1) in the high half, the work spent searching that mapping in order in the low half; 0 is free.
    private long[]? _track;

    private KeyTable?[]? _tables;

    private int _tick;

    private bool _crowded;

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

    /// <summary>
    /// How far the offsets of each chunk of nodes are behind while a state is edited in place; null otherwise.
    /// It lives here so that a state that is only read takes no more room.
    /// </summary>
    public int[]? Behind;

    /// <summary>The expansion sizes of a read through aliases, returned to the pool with the state.</summary>
    public NodeReplay.ExpansionSizes? Expansion;

    public int BuiltCount
    {
        get
        {
            int built = 0;
            KeyTable?[]? tables = _tables;
            if (tables is not null)
            {
                for (int i = 0; i < Slots; i++)
                {
                    built += tables[i]?.Slots is null ? 0 : 1;
                }
            }

            return built;
        }
    }

    public int NodeAt(int i)
    {
        long[]? track = _track;
        return track is null ? -1 : (int)(track[i] >> 32) - 1;
    }

    public KeyTable? TableAt(int i) => _tables?[i];

    public void MoveSlot(int i, int delta)
    {
        long[] track = _track!;
        track[i] += (long)delta << 32;
        KeyTable? t = _tables?[i];
        if (t is not null)
        {
            t.Node += delta;
        }
    }

    public void ClearSlot(int i)
    {
        _track![i] = 0;
        if (_tables is not null)
        {
            _tables[i]?.Release();
            _tables[i] = null;
        }
    }

    public KeyTable? FindTable(int node)
    {
        KeyTable?[]? tables = _tables;
        long[]? track = _track;
        if (tables is null || track is null)
        {
            return null;
        }

        long want = (long)(node + 1);
        for (int i = 0; i < Slots; i++)
        {
            if ((track[i] >> 32) == want)
            {
                KeyTable? t = tables[i];
                return t is not null && t.Node == node ? t : null;
            }
        }

        return null;
    }

    public void Touch(KeyTable t)
    {
        if (_crowded)
        {
            t.LastUse = ++_tick;
        }
    }

    // Adds the work of one search in order to the mapping's account and returns its slot, or -1 when the cache is full of
    // indexes that are in use. A mapping that already has an index, or a refusal, is not charged.
    public int Charge(int node, int pairs, out long work)
    {
        long[] track = _track ?? CreateTrack();
        KeyTable?[]? tables = _tables;
        long want = node + 1;
        int free = -1;
        int victim = -1;
        long victimWork = long.MaxValue;
        for (int i = 0; i < Slots; i++)
        {
            long w = track[i];
            KeyTable? table = tables?[i];

            // A reader that lost a race can leave the table of one mapping in the slot the account gives to another.
            // Nobody finds that table, so the slot counts as free: otherwise the mapping would stay unindexed.
            bool stale = table is not null && table.Node != (int)(w >> 32) - 1;
            if ((w >> 32) == want)
            {
                if (table is not null && !stale)
                {
                    work = 0;
                    return -1;
                }

                long sum = Math.Min((long)(w & 0xFFFFFFFFL) + pairs, int.MaxValue);
                track[i] = (want << 32) | sum;
                work = sum;
                return i;
            }

            if (w == 0 || stale)
            {
                if (free < 0)
                {
                    free = i;
                }
            }
            else if (table is null)
            {
                long spent = w & 0xFFFFFFFFL;
                if (spent < victimWork)
                {
                    victimWork = spent;
                    victim = i;
                }
            }
        }

        int at = free >= 0 ? free : victim;
        if (at < 0)
        {
            // Every slot holds an index. One that has gone unused for long gives way, so a changed working set can move in.
            _crowded = true;
            int oldest = -1;
            int age = 0;
            for (int i = 0; i < Slots; i++)
            {
                KeyTable? t = tables![i];
                int unused = t is null ? 0 : _tick - t.LastUse;
                if (unused > age)
                {
                    age = unused;
                    oldest = i;
                }
            }

            if (oldest < 0 || age < StaleAfter)
            {
                work = 0;
                return -1;
            }

            at = oldest;
            tables![at] = null;
        }

        long first = Math.Min((long)pairs, int.MaxValue);
        track[at] = (want << 32) | first;
        work = first;
        return at;
    }

    private long[] CreateTrack()
    {
        Interlocked.CompareExchange(ref _track, new long[Slots], null);
        return _track!;
    }

    // Makes a built table the mapping's index, unless another reader got there first or the slot has moved on.
    public void Publish(int slot, KeyTable t)
    {
        long[] track = _track!;
        if ((track[slot] >> 32) != t.Node + 1L)
        {
            return;
        }

        Install(slot, t);
    }

    // The step of Publish after the slot was found to belong to the table's mapping; a reader that was slow to get
    // here may find the slot has since been given to another mapping, and then leaves its table behind.
    internal void Install(int slot, KeyTable t)
    {
        KeyTable?[] tables = _tables ?? CreateTables();
        KeyTable? seen = Interlocked.CompareExchange(ref tables[slot], t, null);
        if (seen is not null && seen.Node != t.Node)
        {
            Interlocked.CompareExchange(ref tables[slot], t, seen);
        }
    }

    private KeyTable?[] CreateTables()
    {
        Interlocked.CompareExchange(ref _tables, new KeyTable?[Slots], null);
        return _tables!;
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

    public void Release()
    {
        if (_tables is not null)
        {
            foreach (KeyTable? t in _tables)
            {
                t?.Release();
            }
        }

        _track = null;
        _tables = null;
        Behind = null;
        _layouts = 0;
        Expansion?.Release();
        Expansion = null;
    }
}
