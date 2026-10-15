using System;
using System.Buffers;
using System.Text;

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

    /// <summary>Finds the traits of UTF-8 text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The traits.</returns>
    public static TextTraits Analyze(ReadOnlySpan<byte> text)
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
