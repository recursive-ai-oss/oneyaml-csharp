using System;
using System.Buffers;
using System.Numerics;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml.Documents;

internal sealed partial class DocState
{
    // Mappings up to this many pairs are checked in stack memory.
    private const int SmallPairs = 32;

    /// <summary>
    /// Throws when a closed mapping holds the same key twice. Keys are compared after Core resolution,
    /// so <c>1</c> and <c>0x1</c> clash while <c>1</c> and <c>"1"</c> do not. Keys that are not
    /// scalars are not compared.
    /// </summary>
    /// <param name="map">The mapping node, whose subtree is complete.</param>
    private void CheckDuplicateKeys(int map)
    {
        int pairs = _nodes[map].Count / 2;
        int[]? rented = null;
        Span<int> hashes = pairs <= SmallPairs ? stackalloc int[SmallPairs] : (rented = ArrayPool<int>.Shared.Rent(pairs * 2)).AsSpan(0, pairs);
        Span<int> keys = pairs <= SmallPairs ? stackalloc int[SmallPairs] : rented.AsSpan(pairs, pairs);
        int tableSize = 16;
        while (tableSize < pairs * 2)
        {
            tableSize <<= 1;
        }

        int[]? rentedTable = null;
        Span<int> table = tableSize <= SmallPairs * 2 ? stackalloc int[SmallPairs * 2] : (rentedTable = ArrayPool<int>.Shared.Rent(tableSize));
        table = table.Slice(0, tableSize);
        table.Clear();
        try
        {
            int mask = tableSize - 1;
            int count = 0;
            int c = map + 1;
            for (int i = 0; i < pairs; i++)
            {
                int key = c;
                c += _nodes[key].Size;
                c += _nodes[c].Size;
                if (_nodes[key].Kind != NodeKind.Scalar)
                {
                    continue;
                }

                int h = KeyHash(key);
                int slot = h & mask;
                while (true)
                {
                    int e = table[slot];
                    if (e == 0)
                    {
                        hashes[count] = h;
                        keys[count] = key;
                        count++;
                        table[slot] = count;
                        break;
                    }

                    if (hashes[e - 1] == h && SameKey(keys[e - 1], key))
                    {
                        throw YamlException.At(YamlErrorCategory.DuplicateKey, "The mapping has the same key twice.", Text, _nodes[key].Start);
                    }

                    slot = (slot + 1) & mask;
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }

            if (rentedTable is not null)
            {
                ArrayPool<int>.Shared.Return(rentedTable);
            }
        }
    }

    // Plain text that starts with one of these may be a null, boolean or number; anything else is a string.
    private static bool MayBeNonString(byte first) => first is >= (byte)'0' and <= (byte)'9'
        or (byte)'+' or (byte)'-' or (byte)'.' or (byte)'~' or (byte)'n' or (byte)'N'
        or (byte)'t' or (byte)'T' or (byte)'f' or (byte)'F';

    private bool IsPlainlyString(int key)
    {
        ref readonly CstNode n = ref _nodes[key];
        if (n.Props >= 0)
        {
            return false;
        }

        if ((ScalarStyle)n.Style != ScalarStyle.Plain)
        {
            return true;
        }

        return n.End > n.Start && !MayBeNonString(_buf[n.Start]);
    }

    private int KeyHash(int key)
    {
        ScalarKind kind = IsPlainlyString(key) ? ScalarKind.String : KindOf(key, out _);
        var h = new HashCode();
        h.Add((int)kind);
        if (kind == ScalarKind.Null)
        {
            return h.ToHashCode();
        }

        ReadOnlySpan<byte> text = DecodedScalar(key, out byte[]? rented);
        try
        {
            switch (kind)
            {
                case ScalarKind.Bool:
                    h.Add(CoreSchema.TryParseBool(text, out bool b) && b);
                    break;
                case ScalarKind.Int:
                    if (CoreSchema.TryParseInt64(text, out long l))
                    {
                        h.Add(l);
                    }
                    else if (CoreSchema.TryParseBigInteger(text, out BigInteger big))
                    {
                        h.Add(big);
                    }

                    break;
                case ScalarKind.Float:
                    if (CoreSchema.TryParseDouble(text, out double d))
                    {
                        // Zero and negative zero are one key, and so are all NaN.
                        h.Add(d == 0 ? 0.0 : d);
                    }

                    break;
                default:
                    h.AddBytes(text);
                    break;
            }
        }
        finally
        {
            ReleaseDecoded(rented);
        }

        return h.ToHashCode();
    }

    private bool SameKey(int a, int b)
    {
        ScalarKind ka = IsPlainlyString(a) ? ScalarKind.String : KindOf(a, out _);
        ScalarKind kb = IsPlainlyString(b) ? ScalarKind.String : KindOf(b, out _);
        if (ka != kb)
        {
            return false;
        }

        if (ka == ScalarKind.Null)
        {
            return true;
        }

        ReadOnlySpan<byte> ta = DecodedScalar(a, out byte[]? ra);
        try
        {
            ReadOnlySpan<byte> tb = DecodedScalar(b, out byte[]? rb);
            try
            {
                return SameValue(ka, ta, tb);
            }
            finally
            {
                ReleaseDecoded(rb);
            }
        }
        finally
        {
            ReleaseDecoded(ra);
        }
    }

    /// <summary>Whether two scalar texts of one Core kind are the same value.</summary>
    /// <param name="kind">The kind both resolve to.</param>
    /// <param name="a">The first text.</param>
    /// <param name="b">The second text.</param>
    /// <returns><see langword="true"/> if the values are equal. All NaN are equal; zero equals negative zero.</returns>
    internal static bool SameValue(ScalarKind kind, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        switch (kind)
        {
            case ScalarKind.Null:
                return true;
            case ScalarKind.Bool:
                return CoreSchema.TryParseBool(a, out bool x) && CoreSchema.TryParseBool(b, out bool y) && x == y;
            case ScalarKind.Int:
                if (CoreSchema.TryParseInt64(a, out long la) && CoreSchema.TryParseInt64(b, out long lb))
                {
                    return la == lb;
                }

                return CoreSchema.TryParseBigInteger(a, out BigInteger ba) && CoreSchema.TryParseBigInteger(b, out BigInteger bb) && ba == bb;
            case ScalarKind.Float:
                if (!CoreSchema.TryParseDouble(a, out double da) || !CoreSchema.TryParseDouble(b, out double db))
                {
                    return false;
                }

                if (da == db && (double.IsInfinity(da) || da == 0))
                {
                    // A literal beyond the double range (1e400, 1e-400) is not infinity or zero: 1e400 and 2e400 differ.
                    return FloatKeyIdentity.Of(a) == FloatKeyIdentity.Of(b);
                }

                return da == db || (double.IsNaN(da) && double.IsNaN(db));
            default:
                return a.SequenceEqual(b);
        }
    }
}
