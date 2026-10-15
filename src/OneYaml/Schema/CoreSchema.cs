using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OneYaml.Schema;

/// <summary>What a scalar's text means under a schema.</summary>
internal enum ScalarKind : byte
{
    /// <summary>The null value.</summary>
    Null,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>An integer of any size.</summary>
    Int,

    /// <summary>A floating-point number, including infinities and not-a-number.</summary>
    Float,

    /// <summary>Plain text.</summary>
    String,
}

/// <summary>The scalar tags of the Core schema.</summary>
internal enum CoreTag : byte
{
    /// <summary>Not a Core scalar tag; the scalar is kept as text.</summary>
    Unknown,

    /// <summary>Text. Also what the non-specific tag <c>!</c> means.</summary>
    Str,

    /// <summary>Integer.</summary>
    Int,

    /// <summary>Floating point.</summary>
    Float,

    /// <summary>Boolean.</summary>
    Bool,

    /// <summary>Null.</summary>
    Null,
}

/// <summary>
/// The YAML 1.2 Core schema (section 10.3.2) over UTF-8 text: which plain scalars are null,
/// booleans, integers or floats, and exact parsers for the values. Everything is a pure
/// function of its input. Resolving and number parsing allocate nothing after warm-up on net8
/// and net10 (measured); <see cref="TryParseBigInteger"/> returns an allocated value.
/// </summary>
internal static class CoreSchema
{
    private const string TagPrefix = "tag:yaml.org,2002:";

    /// <summary>Says what a plain scalar's text resolves to.</summary>
    /// <param name="text">The scalar's text as read, UTF-8.</param>
    /// <returns>The kind; <see cref="ScalarKind.String"/> when nothing else matches.</returns>
    public static ScalarKind ResolvePlain(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return ScalarKind.Null;
        }

        // Most plain scalars are ordinary words: one look at the first byte settles them.
        switch (text[0])
        {
            case (byte)'~':
                return text.Length == 1 ? ScalarKind.Null : ScalarKind.String;
            case (byte)'n':
            case (byte)'N':
                return IsNullWord(text) ? ScalarKind.Null : ScalarKind.String;
            case (byte)'t':
            case (byte)'T':
                return IsTrueWord(text) ? ScalarKind.Bool : ScalarKind.String;
            case (byte)'f':
            case (byte)'F':
                return IsFalseWord(text) ? ScalarKind.Bool : ScalarKind.String;
            case (byte)'+':
            case (byte)'-':
            case (byte)'.':
            case >= (byte)'0' and <= (byte)'9':
                return ClassifyNumber(text);
            default:
                return ScalarKind.String;
        }
    }

    /// <summary>Tells whether the text is a Core null: empty, <c>~</c>, <c>null</c>, <c>Null</c> or <c>NULL</c>.</summary>
    /// <param name="text">The scalar's text.</param>
    /// <returns><see langword="true"/> for a null spelling.</returns>
    public static bool IsNull(ReadOnlySpan<byte> text) =>
        text.IsEmpty || (text.Length == 1 && text[0] == (byte)'~') || IsNullWord(text);

    /// <summary>Parses a Core boolean: <c>true</c>, <c>True</c>, <c>TRUE</c> and the same for false.</summary>
    /// <param name="text">The scalar's text.</param>
    /// <param name="value">The value when the text is a boolean.</param>
    /// <returns><see langword="true"/> when the text is a boolean spelling.</returns>
    public static bool TryParseBool(ReadOnlySpan<byte> text, out bool value)
    {
        if (IsTrueWord(text))
        {
            value = true;
            return true;
        }

        value = false;
        return IsFalseWord(text);
    }

    /// <summary>
    /// Parses a Core integer (decimal with an optional sign, <c>0o</c> octal or <c>0x</c>
    /// hexadecimal without a sign) that fits in 64 bits.
    /// </summary>
    /// <param name="text">The scalar's text.</param>
    /// <param name="value">The value on success.</param>
    /// <returns><see langword="false"/> when the text is not a Core integer or does not fit; use <see cref="TryParseBigInteger"/> for the latter.</returns>
    public static bool TryParseInt64(ReadOnlySpan<byte> text, out long value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        ulong acc = 0;
        bool negative = false;
        int i = 0;
        if (text.Length > 2 && text[0] == (byte)'0' && (text[1] == (byte)'x' || text[1] == (byte)'o'))
        {
            bool hex = text[1] == (byte)'x';
            for (i = 2; i < text.Length; i++)
            {
                int d = hex ? HexValue(text[i]) : OctalValue(text[i]);
                if (d < 0 || acc > (ulong.MaxValue >> (hex ? 4 : 3)))
                {
                    return false;
                }

                acc = (acc << (hex ? 4 : 3)) | (uint)d;
            }

            if (acc > long.MaxValue)
            {
                return false;
            }

            value = (long)acc;
            return true;
        }

        if (text[0] is (byte)'+' or (byte)'-')
        {
            negative = text[0] == (byte)'-';
            i = 1;
        }

        if (i == text.Length)
        {
            return false;
        }

        // Up to 18 digits cannot overflow 64 bits, so the common case skips the overflow test.
        if (text.Length - i <= 18)
        {
            for (; i < text.Length; i++)
            {
                uint d = (uint)(text[i] - '0');
                if (d > 9)
                {
                    return false;
                }

                acc = (acc * 10) + d;
            }
        }
        else
        {
            for (; i < text.Length; i++)
            {
                uint d = (uint)(text[i] - '0');
                if (d > 9 || acc > (ulong.MaxValue - d) / 10)
                {
                    return false;
                }

                acc = (acc * 10) + d;
            }
        }

        if (negative)
        {
            if (acc > (ulong)long.MaxValue + 1)
            {
                return false;
            }

            value = unchecked((long)(0 - acc));
            return true;
        }

        if (acc > long.MaxValue)
        {
            return false;
        }

        value = (long)acc;
        return true;
    }

    /// <summary>
    /// Tells whether an integer literal is short enough to parse: decimal digits are counted as
    /// they are, and hexadecimal and octal digits as the same number of decimal digits would
    /// need. Readers call this before <see cref="TryParseBigInteger"/>, whose cost grows faster
    /// than the length.
    /// </summary>
    /// <param name="text">The scalar's text (an integer literal; anything else passes).</param>
    /// <param name="maxDigits">The most decimal digits allowed, <see cref="YamlLimits.MaxIntegerDigits"/>.</param>
    /// <returns><see langword="false"/> when the literal has more digits than allowed.</returns>
    public static bool IsWithinIntegerDigits(ReadOnlySpan<byte> text, int maxDigits)
    {
        if (text.Length > 2 && text[0] == (byte)'0' && (text[1] == (byte)'x' || text[1] == (byte)'o'))
        {
            // A hexadecimal digit carries 4 bits and an octal digit 3; a decimal digit about 3.32.
            long bitsAllowed = maxDigits * 332L / 100;
            long digits = text.Length - 2;
            return digits * (text[1] == (byte)'x' ? 4 : 3) <= bitsAllowed;
        }

        int start = text.Length > 0 && text[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        return text.Length - start <= maxDigits;
    }

    /// <summary>Parses a Core integer of any size exactly; nothing is rounded.</summary>
    /// <param name="text">The scalar's text.</param>
    /// <param name="value">The value on success.</param>
    /// <returns><see langword="false"/> when the text is not a Core integer.</returns>
    public static bool TryParseBigInteger(ReadOnlySpan<byte> text, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (text.IsEmpty)
        {
            return false;
        }

        if (text.Length > 2 && text[0] == (byte)'0' && (text[1] == (byte)'x' || text[1] == (byte)'o'))
        {
            return TryParsePowerOfTwoRadix(text.Slice(2), text[1] == (byte)'x', out value);
        }

        int start = text[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        if (start == text.Length)
        {
            return false;
        }

        for (int i = start; i < text.Length; i++)
        {
            if ((uint)(text[i] - '0') > 9)
            {
                return false;
            }
        }

        char[]? rented = null;
        Span<char> chars = text.Length <= 128
            ? stackalloc char[128]
            : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        try
        {
            // Digits are plain ASCII, so widening is exact.
            Ascii.ToUtf16(text, chars, out int written);
            return BigInteger.TryParse(chars.Slice(0, written), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Parses a Core float into the nearest <see cref="double"/>: the Core float forms,
    /// <c>.inf</c> and <c>.nan</c> spellings, and decimal integers (as a float tag allows).
    /// </summary>
    /// <param name="text">The scalar's text.</param>
    /// <param name="value">The value on success.</param>
    /// <returns><see langword="false"/> when the text is not a Core float spelling.</returns>
    public static bool TryParseDouble(ReadOnlySpan<byte> text, out double value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        if (TrySpecialFloat(text, out value))
        {
            return true;
        }

        if (ClassifyNumber(text) == ScalarKind.String || IsBasePrefixed(text))
        {
            return false;
        }

        // The text matched the Core pattern, so only ASCII digits, signs, '.' and 'e' reach the BCL
        // parsers. Both round correctly; the plain UTF-8 parser is the faster one and the culture-aware
        // one catches what it declines (for example a result beyond the double range).
        if (Utf8Parser.TryParse(text, out value, out int consumed) && consumed == text.Length)
        {
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Finds the Core scalar tag for a tag as written, where <c>!!</c> is taken to be the default
    /// handle. A document can redefine <c>!!</c> with <c>%TAG</c>, so a caller that has the resolved
    /// tag must use <see cref="ParseResolvedTag"/> instead.
    /// </summary>
    /// <param name="tag">The tag as <c>tag:yaml.org,2002:int</c>, <c>!!int</c> or the non-specific <c>!</c>.</param>
    /// <returns>The tag, or <see cref="CoreTag.Unknown"/>.</returns>
    public static CoreTag ParseTag(ReadOnlySpan<byte> tag) =>
        tag.StartsWith("!!"u8) ? ParseName(tag.Slice(2)) : ParseResolvedTag(tag);

    /// <summary>
    /// Finds the Core scalar tag for a tag whose handle is already resolved. The shorthand
    /// <c>!!int</c> is not accepted, because it may name a different tag in a document that
    /// redefines the handle.
    /// </summary>
    /// <param name="tag">The resolved tag, such as <c>tag:yaml.org,2002:int</c>, or the non-specific <c>!</c>.</param>
    /// <returns>The tag, or <see cref="CoreTag.Unknown"/>.</returns>
    public static CoreTag ParseResolvedTag(ReadOnlySpan<byte> tag)
    {
        if (tag.Length == 1 && tag[0] == (byte)'!')
        {
            return CoreTag.Str;
        }

        return tag.StartsWith("tag:yaml.org,2002:"u8) ? ParseName(tag.Slice(TagPrefix.Length)) : CoreTag.Unknown;
    }

    private static CoreTag ParseName(ReadOnlySpan<byte> name)
    {
        if (name.SequenceEqual("str"u8))
        {
            return CoreTag.Str;
        }

        if (name.SequenceEqual("int"u8))
        {
            return CoreTag.Int;
        }

        if (name.SequenceEqual("float"u8))
        {
            return CoreTag.Float;
        }

        if (name.SequenceEqual("bool"u8))
        {
            return CoreTag.Bool;
        }

        return name.SequenceEqual("null"u8) ? CoreTag.Null : CoreTag.Unknown;
    }

    /// <summary>
    /// Applies an explicit Core tag to a scalar's text, quoted or not: the tag decides the type.
    /// </summary>
    /// <param name="tag">The scalar's tag.</param>
    /// <param name="text">The scalar's text (already unquoted).</param>
    /// <param name="kind">The kind the tag forces.</param>
    /// <returns>
    /// <see langword="false"/> when the text cannot be a value of the tag's type, or the tag is
    /// not a Core tag; the caller reports it or keeps the text.
    /// </returns>
    public static bool TryResolveTagged(CoreTag tag, ReadOnlySpan<byte> text, out ScalarKind kind)
    {
        switch (tag)
        {
            case CoreTag.Str:
                kind = ScalarKind.String;
                return true;
            case CoreTag.Null:
                kind = ScalarKind.Null;
                return IsNull(text);
            case CoreTag.Bool:
                kind = ScalarKind.Bool;
                return TryParseBool(text, out _);
            case CoreTag.Int:
                kind = ScalarKind.Int;
                return ClassifyNumber(text) == ScalarKind.Int;
            case CoreTag.Float:
                kind = ScalarKind.Float;
                ScalarKind k = ClassifyNumber(text);
                return k == ScalarKind.Float || (k == ScalarKind.Int && !IsBasePrefixed(text));
            default:
                kind = ScalarKind.String;
                return false;
        }
    }

    private static bool IsNullWord(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("null"u8) || t.SequenceEqual("Null"u8) || t.SequenceEqual("NULL"u8);

    private static bool IsTrueWord(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("true"u8) || t.SequenceEqual("True"u8) || t.SequenceEqual("TRUE"u8);

    private static bool IsFalseWord(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("false"u8) || t.SequenceEqual("False"u8) || t.SequenceEqual("FALSE"u8);

    private static bool IsBasePrefixed(ReadOnlySpan<byte> t) =>
        t.Length > 2 && t[0] == (byte)'0' && (t[1] == (byte)'x' || t[1] == (byte)'o');

    private static int HexValue(byte c)
    {
        uint d = (uint)(c - '0');
        if (d <= 9)
        {
            return (int)d;
        }

        uint l = (uint)((c | 0x20) - 'a');
        return l <= 5 ? (int)(l + 10) : -1;
    }

    private static int OctalValue(byte c)
    {
        uint d = (uint)(c - '0');
        return d <= 7 ? (int)d : -1;
    }

    private static bool TrySpecialFloat(ReadOnlySpan<byte> t, out double value)
    {
        value = 0;
        bool signed = t[0] is (byte)'+' or (byte)'-';
        ReadOnlySpan<byte> w = signed ? t.Slice(1) : t;
        if (w.Length != 4 || w[0] != (byte)'.')
        {
            return false;
        }

        if (w.SequenceEqual(".inf"u8) || w.SequenceEqual(".Inf"u8) || w.SequenceEqual(".INF"u8))
        {
            value = t[0] == (byte)'-' ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }

        // Not-a-number takes no sign.
        if (!signed && (w.SequenceEqual(".nan"u8) || w.SequenceEqual(".NaN"u8) || w.SequenceEqual(".NAN"u8)))
        {
            value = double.NaN;
            return true;
        }

        return false;
    }

    private static bool TryParsePowerOfTwoRadix(ReadOnlySpan<byte> digits, bool hex, out BigInteger value)
    {
        value = BigInteger.Zero;
        int bits = hex ? 4 : 3;
        long totalBits = (long)digits.Length * bits;
        byte[] bytes = new byte[(totalBits + 7) / 8];
        int bitPos = 0;

        // Little-endian bit packing from the least significant digit.
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = hex ? HexValue(digits[i]) : OctalValue(digits[i]);
            if (d < 0)
            {
                return false;
            }

            int byteIndex = bitPos >> 3;
            int shift = bitPos & 7;
            bytes[byteIndex] |= (byte)(d << shift);
            if (shift + bits > 8)
            {
                bytes[byteIndex + 1] |= (byte)(d >> (8 - shift));
            }

            bitPos += bits;
        }

        value = new BigInteger(bytes, isUnsigned: true);
        return true;
    }

    // Classifies text that starts with a sign, '.' or a digit as Int, Float or String, in one pass.
    private static ScalarKind ClassifyNumber(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len == 0)
        {
            return ScalarKind.String;
        }

        int i = 0;
        bool signed = false;
        if (s[0] is (byte)'+' or (byte)'-')
        {
            signed = true;
            i = 1;
            if (len == 1)
            {
                return ScalarKind.String;
            }
        }

        if (s[i] == (byte)'.' && len - i == 4)
        {
            ReadOnlySpan<byte> w = s.Slice(i);
            if (w.SequenceEqual(".inf"u8) || w.SequenceEqual(".Inf"u8) || w.SequenceEqual(".INF"u8))
            {
                return ScalarKind.Float;
            }

            if (!signed && (w.SequenceEqual(".nan"u8) || w.SequenceEqual(".NaN"u8) || w.SequenceEqual(".NAN"u8)))
            {
                return ScalarKind.Float;
            }
        }

        if (!signed && s[0] == (byte)'0' && len >= 2 && (s[1] == (byte)'x' || s[1] == (byte)'o'))
        {
            if (len == 2)
            {
                return ScalarKind.String;
            }

            bool hex = s[1] == (byte)'x';
            for (int k = 2; k < len; k++)
            {
                if ((hex ? HexValue(s[k]) : OctalValue(s[k])) < 0)
                {
                    return ScalarKind.String;
                }
            }

            return ScalarKind.Int;
        }

        int intStart = i;
        while (i < len && (uint)(s[i] - '0') <= 9)
        {
            i++;
        }

        int intDigits = i - intStart;
        bool isFloat = false;
        int fracDigits = 0;
        if (i < len && s[i] == (byte)'.')
        {
            isFloat = true;
            i++;
            int fracStart = i;
            while (i < len && (uint)(s[i] - '0') <= 9)
            {
                i++;
            }

            fracDigits = i - fracStart;
        }

        if (intDigits == 0 && fracDigits == 0)
        {
            return ScalarKind.String;
        }

        if (i < len && (s[i] | 0x20) == 'e')
        {
            isFloat = true;
            i++;
            if (i < len && s[i] is (byte)'+' or (byte)'-')
            {
                i++;
            }

            int expStart = i;
            while (i < len && (uint)(s[i] - '0') <= 9)
            {
                i++;
            }

            if (i == expStart)
            {
                return ScalarKind.String;
            }
        }

        if (i != len)
        {
            return ScalarKind.String;
        }

        return isFloat ? ScalarKind.Float : ScalarKind.Int;
    }
}
