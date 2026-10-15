using System;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using OneYaml.Documents;
using OneYaml.Parsing;
using OneYaml.Schema;

namespace OneYaml;

internal enum NumberStatus : byte
{
    Ok = 0,
    NotNumber,
    OutOfRange,
}

public ref partial struct Utf8YamlReader
{
    private const int ScratchSize = 128;

    /// <summary>Whether the current token is a null scalar: empty, <c>~</c> or a Core spelling of null.</summary>
    public readonly bool IsNull
    {
        get
        {
            if (Type != YamlTokenType.Scalar)
            {
                return false;
            }

            if (Tag == TagClass.Null)
            {
                return CoreSchema.IsNull(_src.Slice(ContentStart, ContentEnd - ContentStart));
            }

            if (Style != ScalarStyle.Plain || Tag is TagClass.Str or TagClass.Int or TagClass.Float or TagClass.Bool)
            {
                return false;
            }

            return ContentEnd == ContentStart || CoreSchema.IsNull(_src.Slice(ContentStart, ContentEnd - ContentStart));
        }
    }

    // The scalar's value as UTF-8: the source text itself unless it needs unescaping or folding.
    internal readonly ReadOnlySpan<byte> Text(Span<byte> scratch, ref byte[]? rented)
    {
        ReadOnlySpan<byte> raw = _src.Slice(ContentStart, ContentEnd - ContentStart);
        if (!Escaped)
        {
            return raw;
        }

        int max = ScalarDecoder.MaxDecodedLength(raw.Length);
        Span<byte> dest;
        if (max <= scratch.Length)
        {
            dest = scratch;
        }
        else
        {
            rented = ArrayPool<byte>.Shared.Rent(max);
            dest = rented;
        }

        int n = ScalarDecoder.Decode(raw, Style, BlockIndent, Chomping, dest);
        return dest[..n];
    }

    private readonly bool TryResolve(ReadOnlySpan<byte> text, out ScalarKind kind)
    {
        switch (Tag)
        {
            case TagClass.Str:
                kind = ScalarKind.String;
                return true;
            case TagClass.Int:
                return CoreSchema.TryResolveTagged(CoreTag.Int, text, out kind);
            case TagClass.Float:
                return CoreSchema.TryResolveTagged(CoreTag.Float, text, out kind);
            case TagClass.Bool:
                return CoreSchema.TryResolveTagged(CoreTag.Bool, text, out kind);
            case TagClass.Null:
                return CoreSchema.TryResolveTagged(CoreTag.Null, text, out kind);
            default:
                kind = Style == ScalarStyle.Plain ? CoreSchema.ResolvePlain(text) : ScalarKind.String;
                return true;
        }
    }

    internal readonly void RequireScalar()
    {
        if (Type != YamlTokenType.Scalar)
        {
            throw CreateException("Expected a scalar value.");
        }
    }

    /// <summary>Returns the text of the current scalar, whatever its style or type.</summary>
    /// <returns>The scalar's value as a string.</returns>
    /// <exception cref="YamlException">The reader is not on a scalar.</exception>
    public readonly string GetString()
    {
        RequireScalar();
        if (!Escaped)
        {
            return ContentEnd == ContentStart ? string.Empty : Encoding.UTF8.GetString(_src.Slice(ContentStart, ContentEnd - ContentStart));
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> text = Text(scratch, ref rented);
            return text.IsEmpty ? string.Empty : Encoding.UTF8.GetString(text);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // The scalar's value as UTF-8, copied to a new array. For binary data and the like.
    internal readonly byte[] GetUtf8Copy()
    {
        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            return Text(scratch, ref rented).ToArray();
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Whether the scalar's text, as written, equals the given UTF-8 text without any unescaping.</summary>
    internal readonly bool RawEquals(ReadOnlySpan<byte> text) =>
        !Escaped && _src.Slice(ContentStart, ContentEnd - ContentStart).SequenceEqual(text);

    /// <summary>Reads the current scalar as a boolean.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not a boolean.</exception>
    public readonly bool GetBoolean() =>
        TryReadBoolean(out bool v) == NumberStatus.Ok ? v : throw CreateException("The value is not a boolean.");

    /// <summary>Reads the current scalar as a 32-bit integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not an integer in range.</exception>
    public readonly int GetInt32() => TryReadInt64(out long v) switch
    {
        NumberStatus.Ok when v is >= int.MinValue and <= int.MaxValue => (int)v,
        NumberStatus.NotNumber => throw CreateException("The value is not an integer."),
        _ => throw CreateException("The integer does not fit in a 32-bit integer."),
    };

    /// <summary>Reads the current scalar as a 64-bit integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not an integer in range.</exception>
    public readonly long GetInt64() => TryReadInt64(out long v) switch
    {
        NumberStatus.Ok => v,
        NumberStatus.NotNumber => throw CreateException("The value is not an integer."),
        _ => throw CreateException("The integer does not fit in a 64-bit integer."),
    };

    /// <summary>Reads the current scalar as a double.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not a number.</exception>
    public readonly double GetDouble() =>
        TryReadDouble(out double v) == NumberStatus.Ok ? v : throw CreateException("The value is not a number.");

    /// <summary>Reads the current scalar as a decimal.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not a number in range.</exception>
    public readonly decimal GetDecimal() => TryReadDecimal(out decimal v) switch
    {
        NumberStatus.Ok => v,
        NumberStatus.NotNumber => throw CreateException("The value is not a number."),
        _ => throw CreateException("The number does not fit in a decimal."),
    };

    /// <summary>Reads the current scalar as an integer of any size.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="YamlException">The scalar is not an integer.</exception>
    public readonly BigInteger GetBigInteger() =>
        TryReadBigInteger(out BigInteger v) == NumberStatus.Ok ? v : throw CreateException("The value is not an integer.");

    // Refuse a very long integer before parsing it: converting one takes more than linear time.
    private readonly void CheckIntegerLength(ReadOnlySpan<byte> text)
    {
        if (!CoreSchema.IsWithinIntegerDigits(text, _limits.MaxIntegerDigits))
        {
            throw Error(YamlErrorCategory.Limit, "The integer has more digits than the configured limit.");
        }
    }

    internal readonly NumberStatus TryReadBoolean(out bool value)
    {
        value = false;
        if (Type != YamlTokenType.Scalar)
        {
            return NumberStatus.NotNumber;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind k) || k != ScalarKind.Bool)
            {
                return NumberStatus.NotNumber;
            }

            return CoreSchema.TryParseBool(t, out value) ? NumberStatus.Ok : NumberStatus.NotNumber;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal readonly NumberStatus TryReadInt64(out long value)
    {
        value = 0;
        if (Type != YamlTokenType.Scalar)
        {
            return NumberStatus.NotNumber;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind k) || k != ScalarKind.Int)
            {
                return NumberStatus.NotNumber;
            }

            return CoreSchema.TryParseInt64(t, out value) ? NumberStatus.Ok : NumberStatus.OutOfRange;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal readonly NumberStatus TryReadUInt64(out ulong value)
    {
        value = 0;
        NumberStatus s = TryReadInt64(out long l);
        if (s == NumberStatus.Ok)
        {
            if (l < 0)
            {
                return NumberStatus.OutOfRange;
            }

            value = (ulong)l;
            return NumberStatus.Ok;
        }

        if (s == NumberStatus.NotNumber)
        {
            return s;
        }

        s = TryReadBigInteger(out BigInteger big);
        if (s != NumberStatus.Ok)
        {
            return s;
        }

        if (big.Sign < 0 || big > ulong.MaxValue)
        {
            return NumberStatus.OutOfRange;
        }

        value = (ulong)big;
        return NumberStatus.Ok;
    }

    internal readonly NumberStatus TryReadBigInteger(out BigInteger value)
    {
        value = default;
        if (Type != YamlTokenType.Scalar)
        {
            return NumberStatus.NotNumber;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind k) || k != ScalarKind.Int)
            {
                return NumberStatus.NotNumber;
            }

            if (CoreSchema.TryParseInt64(t, out long l))
            {
                value = l;
                return NumberStatus.Ok;
            }

            CheckIntegerLength(t);
            return CoreSchema.TryParseBigInteger(t, out value) ? NumberStatus.Ok : NumberStatus.NotNumber;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal readonly NumberStatus TryReadDouble(out double value)
    {
        value = 0;
        if (Type != YamlTokenType.Scalar)
        {
            return NumberStatus.NotNumber;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind k))
            {
                return NumberStatus.NotNumber;
            }

            if (k == ScalarKind.Float)
            {
                if (!CoreSchema.TryParseDouble(t, out value))
                {
                    return NumberStatus.NotNumber;
                }

                // Only the .inf spellings may read as infinity; a finite number that overflows is an error.
                return double.IsInfinity(value) && t.IndexOfAny((byte)'f', (byte)'F') < 0 ? NumberStatus.OutOfRange : NumberStatus.Ok;
            }

            if (k != ScalarKind.Int)
            {
                return NumberStatus.NotNumber;
            }

            if (CoreSchema.TryParseInt64(t, out long l))
            {
                value = l;
                return NumberStatus.Ok;
            }

            CheckIntegerLength(t);
            if (!CoreSchema.TryParseBigInteger(t, out BigInteger big))
            {
                return NumberStatus.NotNumber;
            }

            value = (double)big;
            return double.IsInfinity(value) ? NumberStatus.OutOfRange : NumberStatus.Ok;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal readonly NumberStatus TryReadDecimal(out decimal value)
    {
        value = 0;
        if (Type != YamlTokenType.Scalar)
        {
            return NumberStatus.NotNumber;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind k))
            {
                return NumberStatus.NotNumber;
            }

            if (k == ScalarKind.Int)
            {
                if (CoreSchema.TryParseInt64(t, out long l))
                {
                    value = l;
                    return NumberStatus.Ok;
                }

                CheckIntegerLength(t);
                if (!CoreSchema.TryParseBigInteger(t, out BigInteger big))
                {
                    return NumberStatus.NotNumber;
                }

                if (big < new BigInteger(decimal.MinValue) || big > new BigInteger(decimal.MaxValue))
                {
                    return NumberStatus.OutOfRange;
                }

                value = (decimal)big;
                return NumberStatus.Ok;
            }

            if (k != ScalarKind.Float || !decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return k == ScalarKind.Float && char.IsDigit((char)(t.Length > 0 ? t[^1] : 0)) ? NumberStatus.OutOfRange : NumberStatus.NotNumber;
            }

            return NumberStatus.Ok;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Whether the scalar is surely a string without looking at its value: untagged, and either quoted or a block
    // scalar, or plain with a first byte that no Core null, boolean or number starts with. This is most scalars, and
    // they skip the scratch buffer and the resolver.
    private readonly bool IsPlainlyText()
    {
        if (Tag != TagClass.None)
        {
            return false;
        }

        if (Style != ScalarStyle.Plain)
        {
            return true;
        }

        return ContentEnd > ContentStart && _src[ContentStart] is not ((byte)'~' or (byte)'n' or (byte)'N' or (byte)'t' or (byte)'T'
            or (byte)'f' or (byte)'F' or (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'));
    }

    private static readonly object s_true = true;
    private static readonly object s_false = false;

    // The scalar as the generic model has it: null, bool, long (else BigInteger), double or string.
    internal object? ReadGenericScalar()
    {
        if (IsPlainlyText())
        {
            return GetSharedString();
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind kind))
            {
                throw CreateException("The scalar does not match its tag.");
            }

            switch (kind)
            {
                case ScalarKind.Null:
                    return null;
                case ScalarKind.Bool:
                    return CoreSchema.TryParseBool(t, out bool b) && b ? s_true : s_false;
                case ScalarKind.Int:
                    if (CoreSchema.TryParseInt64(t, out long l))
                    {
                        return l;
                    }

                    CheckIntegerLength(t);
                    return CoreSchema.TryParseBigInteger(t, out BigInteger big) ? big : throw CreateException("The integer is not valid.");
                case ScalarKind.Float:
                    if (!CoreSchema.TryParseDouble(t, out double d))
                    {
                        throw CreateException("The number is not valid.");
                    }

                    // Only the .inf spellings read as infinity; a finite literal that overflows would silently change the data.
                    return double.IsInfinity(d) && t.IndexOfAny((byte)'f', (byte)'F') < 0 ? throw CreateException("The number is outside the range of double.") : d;
                default:
                    return t.IsEmpty ? string.Empty : Escaped ? Encoding.UTF8.GetString(t) : GetSharedString();
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // For a mapping key that resolves to something other than a string: a text that is the same for
    // every spelling of the same value (so 1 and 0x1 are one key). Null for string keys.
    internal readonly string? KeyIdentity()
    {
        if (IsPlainlyText())
        {
            return null;
        }

        byte[]? rented = null;
        try
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            ReadOnlySpan<byte> t = Text(scratch, ref rented);
            if (!TryResolve(t, out ScalarKind kind))
            {
                return null;
            }

            switch (kind)
            {
                case ScalarKind.Null:
                    return "null";
                case ScalarKind.Bool:
                    return CoreSchema.TryParseBool(t, out bool b) && b ? "true" : "false";
                case ScalarKind.Int:
                    if (CoreSchema.TryParseInt64(t, out long l))
                    {
                        return "i" + l.ToString(CultureInfo.InvariantCulture);
                    }

                    // Hex of the bytes: BigInteger.ToString is quadratic in the digits, and this only needs to be one text per value.
                    CheckIntegerLength(t);
                    return CoreSchema.TryParseBigInteger(t, out BigInteger big) ? "i" + Convert.ToHexString(big.ToByteArray()) : null;
                case ScalarKind.Float:
                    return FloatKeyIdentity.Of(t);
                default:
                    return null;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Like GetString, but short ASCII strings that repeat (mapping keys, enum-like values, tags) share one
    // instance per reader, so a document of records allocates each distinct word once. Inputs under 8 KiB skip it:
    // the cache itself would cost more than the repeats save.
    internal string GetSharedString()
    {
        int length = ContentEnd - ContentStart;
        if (Escaped || length is 0 or > 32 || _src.Length < 8192)
        {
            return GetString();
        }

        ReadOnlySpan<byte> raw = _src.Slice(ContentStart, length);
        string?[] cache = _strings ??= new string?[128];
        ulong a;
        ulong b;
        if (length >= 8)
        {
            a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw);
            b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw[^8..]);
        }
        else
        {
            a = 0;
            b = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                a = (a << 8) | raw[i];
            }
        }

        ulong h = (a ^ (b * 0x9E3779B97F4A7C15UL)) + (uint)length;
        h *= 0xFF51AFD7ED558CCDUL;
        int slot = (int)(h >> 57);
        string? known = cache[slot];
        if (known is not null && known.Length == length && Ascii.Equals(raw, known))
        {
            return known;
        }

        string made = Encoding.UTF8.GetString(raw);
        cache[slot] = made;
        return made;
    }
}
