using System;
using System.Globalization;
using System.Text;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>
/// One text per float value for comparing mapping keys. A literal that a double cannot hold (1e400 or 1e-400)
/// is still its own value, so it is compared by its decimal form: sign, significant digits and exponent.
/// The document and the generic loader both ask here, so they agree on every pair.
/// </summary>
internal static class FloatKeyIdentity
{
    /// <summary>The identity text of a Core float spelling, or <see langword="null"/> when it is not a float.</summary>
    /// <param name="text">The scalar text.</param>
    /// <returns>Equal for two spellings exactly when the values are equal; zero and negative zero are one value, and so are all NaN.</returns>
    public static string? Of(ReadOnlySpan<byte> text)
    {
        if (!CoreSchema.TryParseDouble(text, out double d))
        {
            return null;
        }

        if (NeedsDecimal(d, text))
        {
            return Decimal(text);
        }

        return "f" + (d == 0 ? 0.0 : d).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a float key must be compared by its decimal form: a literal beyond the double range, like 1e400 or 1e-400.</summary>
    /// <param name="d">The parsed double of the text.</param>
    /// <param name="text">The scalar text.</param>
    /// <returns><see langword="true"/> when the double lost the value. Two literals that round to the same subnormal (3e-324 and 4e-324) are one value, so they stay one key.</returns>
    public static bool NeedsDecimal(double d, ReadOnlySpan<byte> text)
        => double.IsInfinity(d) ? !IsInfinityLiteral(text) : d == 0 && HasNonZeroDigit(text);

    private static bool IsInfinityLiteral(ReadOnlySpan<byte> t) => t.Length > 0 && t[^1] is (byte)'f' or (byte)'F';

    private static bool HasNonZeroDigit(ReadOnlySpan<byte> t)
    {
        foreach (byte c in t)
        {
            if (c is (byte)'e' or (byte)'E')
            {
                return false;
            }

            if (c is >= (byte)'1' and <= (byte)'9')
            {
                return true;
            }
        }

        return false;
    }

    // The text matched the Core float grammar: [+-] digits [. digits] [eE [+-] digits].
    // Value = 0.DIGITS x 10^(pointPosition + exponent - leadingZeros), the same for every spelling.
    private static string Decimal(ReadOnlySpan<byte> t)
    {
        bool negative = false;
        if (t[0] is (byte)'+' or (byte)'-')
        {
            negative = t[0] == (byte)'-';
            t = t.Slice(1);
        }

        int e = t.IndexOfAny((byte)'e', (byte)'E');
        ReadOnlySpan<byte> mantissa = e < 0 ? t : t.Slice(0, e);
        int dot = mantissa.IndexOf((byte)'.');
        int point = dot < 0 ? mantissa.Length : dot;
        var digits = new StringBuilder(mantissa.Length);
        int leadingZeros = 0;
        foreach (byte c in mantissa)
        {
            if (c == (byte)'.')
            {
                continue;
            }

            if (digits.Length == 0 && c == (byte)'0')
            {
                leadingZeros++;
                continue;
            }

            digits.Append((char)c);
        }

        while (digits.Length > 0 && digits[^1] == '0')
        {
            digits.Length--;
        }

        long shift = point - leadingZeros;
        ReadOnlySpan<byte> x = default;
        bool expNegative = false;
        if (e >= 0)
        {
            x = t.Slice(e + 1);
            expNegative = x[0] == (byte)'-';
            if (x[0] is (byte)'+' or (byte)'-')
            {
                x = x.Slice(1);
            }

            x = x.TrimStart((byte)'0');
        }

        // The exponent is added as digit text: a literal may carry an exponent of any length, and parsing it
        // into a big integer costs more than linear time.
        string exponent = x.Length <= 17 ? AddSmall(x, expNegative, shift) : AddLong(x, expNegative, shift);
        return string.Concat(negative ? "x-" : "x", digits.ToString(), "e", exponent);
    }

    private static string AddSmall(ReadOnlySpan<byte> x, bool negative, long shift)
    {
        long magnitude = 0;
        foreach (byte c in x)
        {
            magnitude = (magnitude * 10) + (c - '0');
        }

        return ((negative ? -magnitude : magnitude) + shift).ToString(CultureInfo.InvariantCulture);
    }

    // x has more than 17 digits and no leading zero, so it is far larger than any shift and the sum keeps its sign.
    private static string AddLong(ReadOnlySpan<byte> x, bool negative, long shift)
    {
        // A shift that works against the exponent's sign is subtracted from the magnitude.
        bool subtract = shift != 0 && (shift < 0) != negative;
        long delta = Math.Abs(shift);
        char[] d = new char[x.Length + 1];
        d[0] = '0';
        for (int i = 0; i < x.Length; i++)
        {
            d[i + 1] = (char)x[i];
        }

        int at = d.Length - 1;
        long carry = 0;
        while (at >= 0 && (delta != 0 || carry != 0))
        {
            int digit = d[at] - '0';
            long step = delta % 10;
            delta /= 10;
            long v = subtract ? digit - step - carry : digit + step + carry;
            if (subtract)
            {
                carry = v < 0 ? 1 : 0;
                v += v < 0 ? 10 : 0;
            }
            else
            {
                carry = v >= 10 ? 1 : 0;
                v -= v >= 10 ? 10 : 0;
            }

            d[at--] = (char)('0' + v);
        }

        int first = 0;
        while (first < d.Length - 1 && d[first] == '0')
        {
            first++;
        }

        var span = new ReadOnlySpan<char>(d, first, d.Length - first);
        return negative ? string.Concat("-", span) : new string(span);
    }
}
