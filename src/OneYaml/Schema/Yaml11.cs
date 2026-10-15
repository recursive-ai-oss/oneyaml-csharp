using System;
using System.Buffers;
using System.Text;

namespace OneYaml.Schema;

/// <summary>
/// Answers whether a YAML 1.1 reader (or a reader with the common loose number and boolean
/// rules) would take plain text as something other than a string. The default reader never
/// does; the writer uses it so text it writes plain reads back as the same string everywhere.
/// </summary>
internal static class Yaml11
{
    /// <summary>
    /// Tells whether plain text could be read as a non-string: booleans (<c>yes</c>, <c>On</c>, <c>y</c>
    /// in any case), null spellings, the merge and value keys, integers with a leading zero, binary,
    /// octal and hexadecimal, underscores, sexagesimal numbers, floats, infinities, and timestamps.
    /// Text that the Core schema already reads as a non-string is included.
    /// </summary>
    /// <param name="text">The scalar's text, UTF-8.</param>
    /// <returns><see langword="true"/> when some reader may not take the text as a string.</returns>
    public static bool LooksNonString(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return true;
        }

        // Words and symbols start with a few letters; ordinary text exits on the first byte.
        switch (text[0])
        {
            case (byte)'y':
            case (byte)'Y':
            case (byte)'n':
            case (byte)'N':
            case (byte)'t':
            case (byte)'T':
            case (byte)'f':
            case (byte)'F':
            case (byte)'o':
            case (byte)'O':
                return IsWord(text);
            case (byte)'~':
                return text.Length == 1;
            case (byte)'=':
                return text.Length == 1;
            case (byte)'<':
                return text.Length == 2 && text[1] == (byte)'<';
            case (byte)'_':
                // Readers that drop every underscore before parsing a number read "_1" as 1. Only text that becomes a
                // number is affected, so a name such as "_id" stays plain.
                return StartsLikeNumberAfterUnderscores(text) && IsLooseNumber(text);
            case (byte)'+':
            case (byte)'-':
            case (byte)'.':
            case >= (byte)'0' and <= (byte)'9':
                return IsNumberOrTimestamp(text) || IsLooseNumber(text);
            default:
                return false;
        }
    }

    private static bool StartsLikeNumberAfterUnderscores(ReadOnlySpan<byte> t)
    {
        int i = 0;
        while (i < t.Length && t[i] == (byte)'_')
        {
            i++;
        }

        return i < t.Length && (IsDigit(t[i]) || t[i] is (byte)'+' or (byte)'-' or (byte)'.');
    }

    // Spellings that strict readers reject but loose ones accept: Go's readers drop every underscore
    // before parsing a number, and some of them skip a second sign or a sign after 0x and 0o.
    private static bool IsLooseNumber(ReadOnlySpan<byte> t)
    {
        if (IsLooseCore(t))
        {
            return true;
        }

        if (t.IndexOf((byte)'_') < 0)
        {
            return false;
        }

        byte[]? rented = null;
        Span<byte> stripped = t.Length <= 128 ? stackalloc byte[128] : (rented = ArrayPool<byte>.Shared.Rent(t.Length));
        try
        {
            int n = 0;
            foreach (byte b in t)
            {
                if (b != (byte)'_')
                {
                    stripped[n++] = b;
                }
            }

            ReadOnlySpan<byte> s = stripped.Slice(0, n);
            return !s.IsEmpty && (CoreSchema.ResolvePlain(s) != ScalarKind.String || IsNumberOrTimestamp(s) || IsLooseCore(s));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static bool IsLooseCore(ReadOnlySpan<byte> t)
    {
        int signs = 0;
        while (signs < t.Length && t[signs] is (byte)'+' or (byte)'-')
        {
            signs++;
        }

        // "+-1" and "++.4": a run of signs in front of a number.
        if (signs >= 2 && signs < t.Length && (IsDigit(t[signs]) || t[signs] == (byte)'.'))
        {
            ReadOnlySpan<byte> rest = t.Slice(signs);
            return CoreSchema.ResolvePlain(rest) != ScalarKind.String || IsNumberOrTimestamp(rest);
        }

        // "0o+7" and "0x-1": a sign after the base prefix.
        int i = signs == 1 ? 1 : 0;
        return signs <= 1 && i + 3 < t.Length && t[i] == (byte)'0' && (t[i + 1] | 0x20) is 'x' or 'o' or 'b'
            && t[i + 2] is (byte)'+' or (byte)'-';
    }

    private static bool IsWord(ReadOnlySpan<byte> t)
    {
        if (t.Length > 5)
        {
            return false;
        }

        return Ascii.EqualsIgnoreCase(t, "y"u8) || Ascii.EqualsIgnoreCase(t, "n"u8)
            || Ascii.EqualsIgnoreCase(t, "yes"u8) || Ascii.EqualsIgnoreCase(t, "no"u8)
            || Ascii.EqualsIgnoreCase(t, "on"u8) || Ascii.EqualsIgnoreCase(t, "off"u8)
            || Ascii.EqualsIgnoreCase(t, "true"u8) || Ascii.EqualsIgnoreCase(t, "false"u8)
            || Ascii.EqualsIgnoreCase(t, "null"u8);
    }

    private static bool IsDigit(byte c) => (uint)(c - '0') <= 9;

    private static int SkipDigitsAndUnderscores(ReadOnlySpan<byte> t, int i)
    {
        while (i < t.Length && (IsDigit(t[i]) || t[i] == (byte)'_'))
        {
            i++;
        }

        return i;
    }

    private static bool IsNumberOrTimestamp(ReadOnlySpan<byte> t)
    {
        int len = t.Length;
        int i = 0;
        if (t[0] is (byte)'+' or (byte)'-')
        {
            i = 1;
            if (len == 1)
            {
                return false;
            }
        }

        if (t[i] == (byte)'.')
        {
            ReadOnlySpan<byte> rest = t.Slice(i + 1);
            if (Ascii.EqualsIgnoreCase(rest, "inf"u8) || Ascii.EqualsIgnoreCase(rest, "nan"u8))
            {
                return true;
            }

            // The literal YAML 1.1 float pattern allows no digits at all, so ".", "-." and ".e-12" match it.
            int f = SkipDigitsAndUnderscores(t, i + 1);
            return IsExponentTail(t, f);
        }

        if (!IsDigit(t[i]))
        {
            return false;
        }

        // 0b101, 0x1F, 0o17 and the underscore forms of them.
        if (t[i] == (byte)'0' && i + 1 < len && (t[i + 1] | 0x20) is 'b' or 'x' or 'o')
        {
            for (int k = i + 2; k < len; k++)
            {
                if (!(IsDigit(t[k]) || t[k] == (byte)'_' || (uint)((t[k] | 0x20) - 'a') <= 5))
                {
                    return false;
                }
            }

            return len > i + 2;
        }

        int j = SkipDigitsAndUnderscores(t, i);
        if (j == len)
        {
            return true;
        }

        if (t[j] == (byte)'-' && j - i == 4 && i == 0)
        {
            return IsTimestampTail(t, j);
        }

        if (t[j] == (byte)':')
        {
            // Sexagesimal: groups of one or two digits after each colon.
            while (j < len && t[j] == (byte)':')
            {
                j++;
                int k = j;
                while (j < len && IsDigit(t[j]))
                {
                    j++;
                }

                if (j - k is 0 or > 2)
                {
                    return false;
                }
            }

            if (j == len)
            {
                return true;
            }

            if (t[j] == (byte)'.')
            {
                return SkipDigitsAndUnderscores(t, j + 1) == len;
            }

            return false;
        }

        if (t[j] == (byte)'.')
        {
            j = SkipDigitsAndUnderscores(t, j + 1);
            if (j == len)
            {
                return true;
            }
        }

        return IsExponentTail(t, j);
    }

    private static bool IsExponentTail(ReadOnlySpan<byte> t, int j)
    {
        if (j == t.Length)
        {
            return true;
        }

        if ((t[j] | 0x20) != 'e')
        {
            return false;
        }

        j++;
        if (j < t.Length && t[j] is (byte)'+' or (byte)'-')
        {
            j++;
        }

        int k = j;
        while (j < t.Length && IsDigit(t[j]))
        {
            j++;
        }

        return j > k && j == t.Length;
    }

    // yyyy-m-d with one or two digits for month and day, then the end, or a time after T, t or a space.
    private static bool IsTimestampTail(ReadOnlySpan<byte> t, int dash)
    {
        int j = dash + 1;
        int k = j;
        while (j < t.Length && IsDigit(t[j]))
        {
            j++;
        }

        if (j - k is 0 or > 2 || j >= t.Length || t[j] != (byte)'-')
        {
            return false;
        }

        j++;
        k = j;
        while (j < t.Length && IsDigit(t[j]))
        {
            j++;
        }

        if (j - k is 0 or > 2)
        {
            return false;
        }

        return j == t.Length || t[j] is (byte)'T' or (byte)'t' or (byte)' ';
    }
}
