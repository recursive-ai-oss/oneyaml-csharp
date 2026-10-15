using System;
using System.Globalization;
using System.Numerics;
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

        if (double.IsInfinity(d) ? !IsInfinityLiteral(text) : d == 0 && HasNonZeroDigit(text))
        {
            return Decimal(text);
        }

        return "f" + (d == 0 ? 0.0 : d).ToString("R", CultureInfo.InvariantCulture);
    }

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

        BigInteger exponent = point - leadingZeros;
        if (e >= 0)
        {
            ReadOnlySpan<byte> x = t.Slice(e + 1);
            bool expNegative = x[0] == (byte)'-';
            if (x[0] is (byte)'+' or (byte)'-')
            {
                x = x.Slice(1);
            }

            x = x.TrimStart((byte)'0');
            if (!x.IsEmpty)
            {
                BigInteger magnitude = BigInteger.Parse(Encoding.ASCII.GetString(x), CultureInfo.InvariantCulture);
                exponent += expNegative ? -magnitude : magnitude;
            }
        }

        return string.Concat(negative ? "x-" : "x", digits.ToString(), "e", exponent.ToString(CultureInfo.InvariantCulture));
    }
}
