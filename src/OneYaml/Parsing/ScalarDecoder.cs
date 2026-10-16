using System;
using System.Text;

namespace OneYaml.Parsing;

/// <summary>
/// Turns a scalar's source text into its value: unescaping, line folding and block scalar
/// indentation and chomping. The scanner has already checked the text, so this trusts it.
/// </summary>
internal static class ScalarDecoder
{
    /// <summary>The most bytes <see cref="Decode"/> can write for a raw scalar of the given length.</summary>
    /// <param name="raw">The raw scalar text.</param>
    /// <param name="style">The scalar style.</param>
    /// <returns>An upper bound on the decoded length.</returns>
    public static int MaxDecodedLength(ReadOnlySpan<byte> raw, ScalarStyle style)
    {
        // Decoding only drops characters (quotes, indentation, escape backslashes, folded breaks), bar two
        // things: a block scalar may gain its final line break, and the escapes \L and \P turn two
        // bytes into the three of U+2028 and U+2029, one byte more per backslash at most. Only double-quoted
        // text has escapes. A tight bound matters because the buffer comes from a pool that rounds up to a
        // power of two.
        long max = raw.Length + 4L;
        if (style == ScalarStyle.DoubleQuoted)
        {
            max += raw.Count((byte)'\\');
        }

        return max > int.MaxValue ? int.MaxValue : (int)max;
    }

    /// <summary>Decodes a scalar into <paramref name="dest"/>.</summary>
    /// <param name="raw">The raw scalar text.</param>
    /// <param name="style">The scalar style.</param>
    /// <param name="blockIndent">For block scalars, the indentation of the content lines.</param>
    /// <param name="chomping">For block scalars, how trailing line breaks are treated.</param>
    /// <param name="dest">The destination, at least <see cref="MaxDecodedLength"/> bytes long.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Decode(ReadOnlySpan<byte> raw, ScalarStyle style, int blockIndent, Chomping chomping, Span<byte> dest)
    {
        return style switch
        {
            ScalarStyle.Plain => DecodeFlow(raw, dest, doubleQuoted: false, singleQuoted: false),
            ScalarStyle.SingleQuoted => DecodeFlow(raw, dest, doubleQuoted: false, singleQuoted: true),
            ScalarStyle.DoubleQuoted => DecodeFlow(raw, dest, doubleQuoted: true, singleQuoted: false),
            ScalarStyle.Literal => DecodeBlock(raw, blockIndent, chomping, folded: false, dest),
            _ => DecodeBlock(raw, blockIndent, chomping, folded: true, dest),
        };
    }

    private static bool IsBlank(byte b) => b == (byte)' ' || b == (byte)'\t';

    private static int DecodeFlow(ReadOnlySpan<byte> raw, Span<byte> dest, bool doubleQuoted, bool singleQuoted)
    {
        int o = 0;
        int i = 0;
        int n = raw.Length;

        // Output up to here is final: it holds no trailing whitespace that line folding would remove.
        int keep = 0;
        while (i < n)
        {
            byte b = raw[i];
            if (b == (byte)'\n' || b == (byte)'\r')
            {
                o = Math.Max(keep, TrimBlanks(dest, o, keep));
                int breaks = 0;
                while (i < n && (raw[i] == (byte)'\n' || raw[i] == (byte)'\r'))
                {
                    i += raw[i] == (byte)'\r' && i + 1 < n && raw[i + 1] == (byte)'\n' ? 2 : 1;
                    breaks++;
                    while (i < n && IsBlank(raw[i]))
                    {
                        i++;
                    }
                }

                if (breaks == 1)
                {
                    dest[o++] = (byte)' ';
                }
                else
                {
                    for (int k = 1; k < breaks; k++)
                    {
                        dest[o++] = (byte)'\n';
                    }
                }

                keep = o;
                continue;
            }

            if (doubleQuoted && b == (byte)'\\')
            {
                byte e = raw[i + 1];
                if (e == (byte)'\n' || e == (byte)'\r')
                {
                    // An escaped line break joins the lines without a space; blank lines still count.
                    keep = o;
                    i += 1 + (e == (byte)'\r' && i + 2 < n && raw[i + 2] == (byte)'\n' ? 2 : 1);
                    while (i < n && IsBlank(raw[i]))
                    {
                        i++;
                    }

                    while (i < n && (raw[i] == (byte)'\n' || raw[i] == (byte)'\r'))
                    {
                        dest[o++] = (byte)'\n';
                        i += raw[i] == (byte)'\r' && i + 1 < n && raw[i + 1] == (byte)'\n' ? 2 : 1;
                        while (i < n && IsBlank(raw[i]))
                        {
                            i++;
                        }
                    }

                    keep = o;
                    continue;
                }

                i = WriteEscape(raw, i, dest, ref o);
                keep = o;
                continue;
            }

            if (singleQuoted && b == (byte)'\'')
            {
                // A doubled quote is one quote.
                dest[o++] = (byte)'\'';
                i += 2;
                keep = o;
                continue;
            }

            dest[o++] = b;
            i++;
            if (!IsBlank(b))
            {
                keep = o;
            }
        }

        return o;
    }

    private static int TrimBlanks(Span<byte> dest, int o, int keep)
    {
        while (o > keep && IsBlank(dest[o - 1]))
        {
            o--;
        }

        return o;
    }

    // raw[i] is the backslash. Returns the index after the escape.
    private static int WriteEscape(ReadOnlySpan<byte> raw, int i, Span<byte> dest, ref int o)
    {
        byte e = raw[i + 1];
        switch (e)
        {
            case (byte)'0':
                dest[o++] = 0;
                return i + 2;
            case (byte)'a':
                dest[o++] = 7;
                return i + 2;
            case (byte)'b':
                dest[o++] = 8;
                return i + 2;
            case (byte)'t':
            case (byte)'\t':
                dest[o++] = 9;
                return i + 2;
            case (byte)'n':
                dest[o++] = 10;
                return i + 2;
            case (byte)'v':
                dest[o++] = 11;
                return i + 2;
            case (byte)'f':
                dest[o++] = 12;
                return i + 2;
            case (byte)'r':
                dest[o++] = 13;
                return i + 2;
            case (byte)'e':
                dest[o++] = 27;
                return i + 2;
            case (byte)' ':
                dest[o++] = (byte)' ';
                return i + 2;
            case (byte)'"':
                dest[o++] = (byte)'"';
                return i + 2;
            case (byte)'/':
                dest[o++] = (byte)'/';
                return i + 2;
            case (byte)'\\':
                dest[o++] = (byte)'\\';
                return i + 2;
            case (byte)'N':
                o += EncodeUtf8(0x85, dest[o..]);
                return i + 2;
            case (byte)'_':
                o += EncodeUtf8(0xA0, dest[o..]);
                return i + 2;
            case (byte)'L':
                o += EncodeUtf8(0x2028, dest[o..]);
                return i + 2;
            case (byte)'P':
                o += EncodeUtf8(0x2029, dest[o..]);
                return i + 2;
            default:
                {
                    int digits = e == (byte)'x' ? 2 : e == (byte)'u' ? 4 : 8;
                    uint value = 0;
                    for (int k = 0; k < digits; k++)
                    {
                        byte h = raw[i + 2 + k];
                        value = (value << 4) | (uint)(h <= '9' ? h - '0' : (h | 0x20) - 'a' + 10);
                    }

                    if (digits == 4 && value >= 0xD800 && value <= 0xDBFF)
                    {
                        // The scanner has checked that a low surrogate escape follows; the pair is one character.
                        uint low = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            byte h = raw[i + 8 + k];
                            low = (low << 4) | (uint)(h <= '9' ? h - '0' : (h | 0x20) - 'a' + 10);
                        }

                        o += EncodeUtf8(0x10000 + ((value - 0xD800) << 10) + (low - 0xDC00), dest[o..]);
                        return i + 12;
                    }

                    o += EncodeUtf8(value, dest[o..]);
                    return i + 2 + digits;
                }
        }
    }

    private static int EncodeUtf8(uint cp, Span<byte> dest)
    {
        if (cp < 0x80)
        {
            dest[0] = (byte)cp;
            return 1;
        }

        if (cp < 0x800)
        {
            dest[0] = (byte)(0xC0 | (cp >> 6));
            dest[1] = (byte)(0x80 | (cp & 0x3F));
            return 2;
        }

        if (cp < 0x10000)
        {
            dest[0] = (byte)(0xE0 | (cp >> 12));
            dest[1] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            dest[2] = (byte)(0x80 | (cp & 0x3F));
            return 3;
        }

        dest[0] = (byte)(0xF0 | (cp >> 18));
        dest[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
        dest[2] = (byte)(0x80 | ((cp >> 6) & 0x3F));
        dest[3] = (byte)(0x80 | (cp & 0x3F));
        return 4;
    }

    private static bool AllSpaces(ReadOnlySpan<byte> text)
    {
        foreach (byte b in text)
        {
            if (b != (byte)' ')
            {
                return false;
            }
        }

        return true;
    }

    private static int DecodeBlock(ReadOnlySpan<byte> raw, int indent, Chomping chomping, bool folded, Span<byte> dest)
    {
        int o = 0;
        int i = 0;
        int n = raw.Length;
        bool haveText = false;
        bool previousMoreIndented = false;

        // Line breaks seen since the last line of text, the one ending that line included.
        int pending = 0;
        while (i < n)
        {
            int spaces = 0;
            while (spaces < indent && i + spaces < n && raw[i + spaces] == (byte)' ')
            {
                spaces++;
            }

            int textStart = i + spaces;
            int rel = raw[textStart..].IndexOfAny((byte)'\n', (byte)'\r');
            int eol = rel < 0 ? n : textStart + rel;
            int breakLength = eol < n ? (raw[eol] == (byte)'\r' && eol + 1 < n && raw[eol + 1] == (byte)'\n' ? 2 : 1) : 0;
            // A last line of spaces without a line break counts as ended by one, as the
            // conformance suite expects.
            bool ended = breakLength > 0 || (eol == n && AllSpaces(raw[textStart..eol]));
            if (eol == textStart)
            {
                if (ended)
                {
                    pending++;
                }

                i = eol + breakLength;
                continue;
            }

            bool moreIndented = IsBlank(raw[textStart]);
            if (!haveText || !folded || previousMoreIndented || moreIndented)
            {
                for (int k = 0; k < pending; k++)
                {
                    dest[o++] = (byte)'\n';
                }
            }
            else if (pending == 1)
            {
                dest[o++] = (byte)' ';
            }
            else
            {
                for (int k = 1; k < pending; k++)
                {
                    dest[o++] = (byte)'\n';
                }
            }

            raw[textStart..eol].CopyTo(dest[o..]);
            o += eol - textStart;
            haveText = true;
            previousMoreIndented = moreIndented;
            pending = ended ? 1 : 0;
            i = eol + breakLength;
        }

        int trailing = chomping switch
        {
            Chomping.Strip => 0,
            Chomping.Clip => haveText && pending > 0 ? 1 : 0,
            _ => pending,
        };
        for (int k = 0; k < trailing; k++)
        {
            dest[o++] = (byte)'\n';
        }

        return o;
    }
}
