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

    // Strings are the common case of an escaped or folded scalar, and a description of a screenful is common
    // enough that renting a buffer for it would be the usual cost. The frame is a leaf, so the stack is only
    // used for the length of the call.
    private const int StringScratchSize = 2048;

    /// <summary>Whether the current token is a null scalar: empty, <c>~</c> or a Core spelling of null.</summary>
    public readonly bool IsNull
    {
        get
        {
            if (Type != YamlTokenType.Scalar)
            {
                return false;
            }

            if (TagKind == TagClass.Null)
            {
                return CoreSchema.IsNull(_src.Slice(ContentStart, ContentEnd - ContentStart));
            }

            if (Style != ScalarStyle.Plain || TagKind is TagClass.Str or TagClass.Int or TagClass.Float or TagClass.Bool)
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

        int max = ScalarDecoder.MaxDecodedLength(raw, Style);
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

    // True for an untagged plain scalar whose text is the source bytes themselves (no folding or escapes).
    private readonly bool HasPlainSourceText => TagKind == TagClass.None && Style == ScalarStyle.Plain && !Escaped;

    private readonly bool TryResolve(ReadOnlySpan<byte> text, out ScalarKind kind)
    {
        switch (TagKind)
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
            Span<byte> scratch = stackalloc byte[StringScratchSize];
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
            throw Error(YamlErrorCategory.Limit, $"The integer has more digits than the limit MaxIntegerDigits ({_limits.MaxIntegerDigits}).");
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

        // An untagged plain integer of up to 18 digits: the value is the answer, no scratch buffer or resolver needed.
        if (HasPlainSourceText && CoreSchema.TryParseSimpleInt64(_src.Slice(ContentStart, ContentEnd - ContentStart), out value))
        {
            return NumberStatus.Ok;
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

        // An untagged plain number the fast path settles. A bare integer keeps the integer route (-0 reads as +0).
        if (HasPlainSourceText && CoreSchema.TryParseSimpleDouble(_src.Slice(ContentStart, ContentEnd - ContentStart), out value, out bool isInteger))
        {
            if (!isInteger)
            {
                return NumberStatus.Ok;
            }

            if (CoreSchema.TryParseSimpleInt64(_src.Slice(ContentStart, ContentEnd - ContentStart), out long whole))
            {
                value = whole;
                return NumberStatus.Ok;
            }

            value = 0;
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

    // Half.TryParse does not round every tie and near-tie correctly on every runtime, so the text goes through double, which
    // does, and the one case where two roundings can disagree is settled from the text: a double that sits exactly on the
    // midpoint of two Half values may stand for a text a little above or below it. A double that is not on a midpoint
    // rounds to the same Half as the text, because no midpoint can lie between the text and its nearest double.
    internal readonly NumberStatus TryReadHalf(out Half value)
    {
        value = default;
        NumberStatus s = TryReadDouble(out double v);
        if (s != NumberStatus.Ok)
        {
            return s;
        }

        value = (Half)v;
        if (double.IsFinite(v) && v != 0 && IsHalfMidpoint(Math.Abs(v)) && Type == YamlTokenType.Scalar)
        {
            byte[]? rented = null;
            try
            {
                Span<byte> scratch = stackalloc byte[ScratchSize];
                ReadOnlySpan<byte> t = Text(scratch, ref rented);
                if (TryResolve(t, out ScalarKind k) && k == ScalarKind.Float)
                {
                    int order = CompareDecimalToMidpoint(t, Math.Abs(v));
                    if (order != 0)
                    {
                        // The cast chose the even neighbour of the midpoint; the text decides instead.
                        ushort bits = BitConverter.HalfToUInt16Bits(value);
                        bool castWentUp = Math.Abs((double)value) > Math.Abs(v);
                        ushort magnitude = (ushort)(bits & 0x7FFF);
                        ushort lower = castWentUp ? (ushort)(magnitude - 1) : magnitude;
                        ushort chosen = order > 0 ? (ushort)(lower + 1) : lower;
                        value = BitConverter.UInt16BitsToHalf((ushort)(chosen | (bits & 0x8000)));
                    }
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

        return Half.IsInfinity(value) && !double.IsInfinity(v) ? NumberStatus.OutOfRange : NumberStatus.Ok;
    }

    // Whether a positive finite double is exactly halfway between two adjacent Half values (65520 is halfway between the
    // largest Half and the next step up).
    private static bool IsHalfMidpoint(double magnitude)
    {
        if (magnitude >= 65520.0)
        {
            return magnitude == 65520.0;
        }

        // Half values are multiples of 2^-24; a midpoint is an odd multiple of 2^-25 at the scale of its binade.
        double scaled = magnitude * 33554432.0; // 2^25, exact
        if (scaled != Math.Floor(scaled))
        {
            return false;
        }

        long k = (long)scaled;
        // Spacing between Half values in this binade is 2^(e-10) with e the exponent of the value (at least -14).
        int exponent = Math.Max(-14, Math.ILogB(magnitude));
        int spacingShift = exponent - 10 + 24; // spacing in units of 2^-24
        long unit = 2L << spacingShift;        // spacing in units of 2^-25
        return k % unit == unit / 2;
    }

    // Compares the decimal float text with a midpoint exactly: -1 below, 0 equal, 1 above. A midpoint has a short, exact
    // decimal expansion, so both sides are reduced to their significant digits and a decimal exponent.
    private static int CompareDecimalToMidpoint(ReadOnlySpan<byte> text, double midpoint)
    {
        // The midpoint as digits: midpoint = k / 2^25 = k * 5^25 / 10^25.
        var scaled = new System.Numerics.BigInteger(midpoint * 33554432.0) * System.Numerics.BigInteger.Pow(5, 25);
        string wanted = scaled.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int wantedPoint = wanted.Length - 25;
        wanted = wanted.TrimEnd('0');

        // The text: sign, digits with an optional point, optional exponent.
        int i = 0;
        if (i < text.Length && (text[i] == (byte)'+' || text[i] == (byte)'-'))
        {
            i++;
        }

        var digits = new System.Text.StringBuilder(text.Length);
        int intDigits = 0;
        bool seenPoint = false;
        for (; i < text.Length; i++)
        {
            byte c = text[i];
            if (c == (byte)'.')
            {
                seenPoint = true;
            }
            else if (c >= (byte)'0' && c <= (byte)'9')
            {
                digits.Append((char)c);
                if (!seenPoint)
                {
                    intDigits++;
                }
            }
            else
            {
                break;
            }
        }

        long exponent = 0;
        if (i < text.Length)
        {
            i++; // the e or E
            bool negative = false;
            if (i < text.Length && (text[i] == (byte)'+' || text[i] == (byte)'-'))
            {
                negative = text[i] == (byte)'-';
                i++;
            }

            for (; i < text.Length; i++)
            {
                // A double that sits on a midpoint has a small exponent; the cap only keeps the sum from overflowing.
                exponent = Math.Min(exponent * 10 + (text[i] - (byte)'0'), 1_000_000);
            }

            if (negative)
            {
                exponent = -exponent;
            }
        }

        string all = digits.ToString();
        string significant = all.TrimStart('0');
        long point = intDigits + exponent - (all.Length - significant.Length);
        significant = significant.TrimEnd('0');
        if (point != wantedPoint)
        {
            return point > wantedPoint ? 1 : -1;
        }

        // Same magnitude: the digit strings order like the numbers (a shorter one is a prefix padded with zeros).
        return Math.Sign(string.CompareOrdinal(significant, wanted));
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
        if (TagKind != TagClass.None)
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
    // With numberText, a number comes back as the text of a JSON number (a JsonNumber) instead of a long, a BigInteger or a
    // double, so that 1.0, 1e3, -0 and a long decimal keep the digits they were written with.
    internal object? ReadGenericScalar(bool numberText = false)
    {
        if (IsPlainlyText())
        {
            return GetSharedString();
        }

        if (!numberText && HasPlainSourceText)
        {
            ReadOnlySpan<byte> raw = _src.Slice(ContentStart, ContentEnd - ContentStart);
            if (CoreSchema.TryParseSimpleInt64(raw, out long whole))
            {
                return whole;
            }

            if (CoreSchema.TryParseSimpleDouble(raw, out double fast, out bool isInteger) && !isInteger)
            {
                return fast;
            }
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
                    if (numberText)
                    {
                        return new JsonNumber(JsonIntegerText(t));
                    }

                    if (CoreSchema.TryParseInt64(t, out long l))
                    {
                        return l;
                    }

                    CheckIntegerLength(t);
                    return CoreSchema.TryParseBigInteger(t, out BigInteger big) ? big : throw CreateException("The integer is not valid.");
                case ScalarKind.Float:
                    if (numberText)
                    {
                        return new JsonNumber(JsonFloatText(t));
                    }

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

    // The JSON number text of a YAML integer: the digits as written, without a plus sign or leading zeros; 0x and 0o forms in decimal.
    private readonly string JsonIntegerText(ReadOnlySpan<byte> t)
    {
        CheckIntegerLength(t);
        int i = 0;
        bool negative = false;
        if (t.Length > 0 && t[0] is (byte)'-' or (byte)'+')
        {
            negative = t[0] == (byte)'-';
            i = 1;
        }

        if (t.Length - i > 1 && t[i] == (byte)'0' && t[i + 1] is (byte)'x' or (byte)'o')
        {
            return CoreSchema.TryParseBigInteger(t, out BigInteger big) ? big.ToString(CultureInfo.InvariantCulture) : throw CreateException("The integer is not valid.");
        }

        while (i < t.Length - 1 && t[i] == (byte)'0')
        {
            i++;
        }

        string digits = Encoding.ASCII.GetString(t[i..]);
        return negative ? "-" + digits : digits;
    }

    // The JSON number text of a YAML float, which may be written as .5, 1., +1.5 or with an exponent: the digits are kept
    // as written and only the parts JSON does not allow are repaired. .inf and .nan have no JSON form.
    private readonly string JsonFloatText(ReadOnlySpan<byte> t)
    {
        int i = 0;
        bool negative = false;
        if (t.Length > 0 && t[0] is (byte)'-' or (byte)'+')
        {
            negative = t[0] == (byte)'-';
            i = 1;
        }

        if (t.Length - i < 1 || (t[i] != (byte)'.' && !char.IsAsciiDigit((char)t[i])) || (t.Length - i > 1 && t[i] == (byte)'.' && !char.IsAsciiDigit((char)t[i + 1])))
        {
            throw CreateException("A JSON number cannot be infinite or not a number.");
        }

        var sb = new StringBuilder(t.Length + 3);
        if (negative)
        {
            sb.Append('-');
        }

        int intStart = i;
        while (i < t.Length && char.IsAsciiDigit((char)t[i]))
        {
            i++;
        }

        ReadOnlySpan<byte> integer = t[intStart..i];
        int skip = 0;
        while (skip < integer.Length - 1 && integer[skip] == (byte)'0')
        {
            skip++;
        }

        sb.Append(integer.IsEmpty ? "0" : Encoding.ASCII.GetString(integer[skip..]));
        if (i < t.Length && t[i] == (byte)'.')
        {
            int fracStart = ++i;
            while (i < t.Length && char.IsAsciiDigit((char)t[i]))
            {
                i++;
            }

            sb.Append('.');
            sb.Append(i == fracStart ? "0" : Encoding.ASCII.GetString(t[fracStart..i]));
        }

        if (i < t.Length)
        {
            sb.Append(Encoding.ASCII.GetString(t[i..]));
        }

        return sb.ToString();
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
    private static readonly string?[] s_singleCharacters = new string?[128];

    private static string SingleCharacter(byte b) => s_singleCharacters[b] ??= ((char)b).ToString();

    internal string GetSharedString()
    {
        int length = ContentEnd - ContentStart;
        if (length == 1 && !Escaped && _src[ContentStart] < 0x80)
        {
            // One-letter keys are common, and the strings are shared by every reader, so nothing is made per key.
            return SingleCharacter(_src[ContentStart]);
        }

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
