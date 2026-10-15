using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace OneYaml.Emitting;

/// <summary>What a scalar's text contains that limits how it can be written.</summary>
[Flags]
internal enum TextTraits : byte
{
    /// <summary>Printable text only.</summary>
    None = 0,

    /// <summary>Contains a line feed.</summary>
    LineBreak = 1,

    /// <summary>
    /// Contains something that cannot appear raw in any scalar: a control character, a carriage
    /// return, a Unicode line separator, a byte-order mark, U+FFFE, U+FFFF or malformed UTF-8.
    /// </summary>
    NeedsEscape = 2,

    /// <summary>Contains a tab.</summary>
    Tab = 4,
}

/// <summary>Reads a string's text once to find what limits its styles.</summary>
internal static class ScalarText
{
    // Printable ASCII: the one-pass vector scan skips straight over it.
    private static readonly SearchValues<byte> PrintableAscii = SearchValues.Create(MakeRange(0x20, 0x7E));

    // In valid UTF-8 only three lead bytes can start a character that must be escaped: C2 (U+0080 to U+009F),
    // E2 (U+2028, U+2029) and EF (U+FEFF, U+FFFE, U+FFFF). Controls and the other ASCII bytes that are not
    // printable are listed too, so one vector scan stops at everything that matters and skips the rest of the
    // text, however many multi-byte characters it holds.
    private static readonly SearchValues<byte> Attention = SearchValues.Create(
        [.. MakeRange(0x00, 0x1F), 0x7F, 0xC2, 0xE2, 0xEF]);

    /// <summary>Finds the traits of UTF-8 text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="validUtf8">
    /// The caller has already checked that the bytes are well-formed UTF-8, which saves a second pass over text with
    /// non-ASCII characters. Malformed text given with this flag is still handled without reading past the end, but may
    /// not be reported as needing an escape.
    /// </param>
    /// <returns>The traits.</returns>
    public static TextTraits Analyze(ReadOnlySpan<byte> text, bool validUtf8 = false)
    {
        ReadOnlySpan<byte> rest = text;
        if (!validUtf8)
        {
            // Text that is not plain printable ASCII is checked once for well-formed UTF-8; then no character needs decoding.
            int first = text.IndexOfAnyExcept(PrintableAscii);
            if (first < 0)
            {
                return TextTraits.None;
            }

            if (!Utf8.IsValid(text.Slice(first)))
            {
                return AnalyzeByRune(text);
            }

            rest = text.Slice(first);
        }

        TextTraits traits = TextTraits.None;
        while (true)
        {
            int at = rest.IndexOfAny(Attention);
            if (at < 0)
            {
                return traits;
            }

            rest = rest.Slice(at);
            byte b = rest[0];
            if (b < 0x80)
            {
                traits |= b == (byte)'\n' ? TextTraits.LineBreak : b == (byte)'\t' ? TextTraits.Tab : TextTraits.NeedsEscape;
                rest = rest.Slice(1);
                continue;
            }

            // A lead byte without its continuation bytes is malformed text.
            int length = b == 0xC2 ? 2 : 3;
            if (rest.Length < length)
            {
                return traits | TextTraits.NeedsEscape;
            }

            bool escape = b switch
            {
                0xC2 => rest[1] <= 0x9F,
                0xE2 => rest[1] == 0x80 && rest[2] is 0xA8 or 0xA9,
                _ => (rest[1] == 0xBB && rest[2] == 0xBF) || (rest[1] == 0xBF && rest[2] is 0xBE or 0xBF),
            };
            if (escape)
            {
                traits |= TextTraits.NeedsEscape;
            }

            rest = rest.Slice(length);
        }
    }

    // Text that may be malformed: every non-ASCII character is decoded, and malformed bytes need an escape.
    private static TextTraits AnalyzeByRune(ReadOnlySpan<byte> text)
    {
        TextTraits traits = TextTraits.None;
        ReadOnlySpan<byte> rest = text;
        while (true)
        {
            int at = rest.IndexOfAnyExcept(PrintableAscii);
            if (at < 0)
            {
                return traits;
            }

            rest = rest.Slice(at);
            byte b = rest[0];
            if (b < 0x80)
            {
                if (b == (byte)'\n')
                {
                    traits |= TextTraits.LineBreak;
                }
                else if (b == (byte)'\t')
                {
                    traits |= TextTraits.Tab;
                }
                else
                {
                    // Other controls, carriage return and DEL.
                    traits |= TextTraits.NeedsEscape;
                }

                rest = rest.Slice(1);
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf8(rest, out Rune rune, out int used);
            if (status != OperationStatus.Done || MustEscape(rune.Value))
            {
                traits |= TextTraits.NeedsEscape;
                rest = rest.Slice(Math.Max(used, 1));
            }
            else
            {
                rest = rest.Slice(used);
            }
        }
    }

    /// <summary>Tells whether a code point has to be escaped (written in double quotes only).</summary>
    /// <param name="codePoint">A Unicode scalar value.</param>
    /// <returns><see langword="true"/> for controls, next-line and Unicode separators, the byte-order mark, U+FFFE and U+FFFF.</returns>
    public static bool MustEscape(int codePoint) =>
        codePoint < 0x20 || (codePoint >= 0x7F && codePoint <= 0x9F)
        || codePoint == 0x2028 || codePoint == 0x2029 || codePoint == 0xFEFF
        || codePoint == 0xFFFE || codePoint == 0xFFFF;

    /// <summary>Builds a byte array holding each value from <paramref name="first"/> to <paramref name="last"/>.</summary>
    /// <param name="first">The first value.</param>
    /// <param name="last">The last value.</param>
    /// <returns>The array.</returns>
    public static byte[] MakeRange(int first, int last)
    {
        byte[] values = new byte[last - first + 1];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (byte)(first + i);
        }

        return values;
    }
}
