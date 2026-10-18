using System;
using System.IO;
using System.Text;
using Oy = global::OneYaml;

namespace OneYaml.Compat.SharpYaml;

// Reads the text of a stream the way the engine reads bytes: a byte order mark or the null bytes of an ASCII first
// character pick UTF-8, UTF-16 or UTF-32, and bytes that are not valid in that encoding are an error. A decoder that
// substitutes U+FFFD would turn damaged input into different, valid-looking text.
internal static class StreamText
{
    public static string Read(Stream stream, string? sourceName, long maxBytes = long.MaxValue) => Read(stream, sourceName, maxBytes, Array.MaxLength);

    // The ceiling is a parameter so a test can reach it without a two-gigabyte input.
    internal static string Read(Stream stream, string? sourceName, long maxBytes, int ceiling) => Read(stream, sourceName, maxBytes, ceiling, Oy.Text.YamlInput.StringMaxLength);

    private const int ReadChunk = 8192;

    // The string ceiling is a parameter too, so a test can reach it without a billion characters.
    internal static string Read(Stream stream, string? sourceName, long maxBytes, int ceiling, int stringCeiling)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // Grown here and not in a MemoryStream, which fails with an IOException at two gigabytes instead of naming the limit.
        byte[] bytes = new byte[8192];
        int count = 0;
        while (true)
        {
            if (count == bytes.Length)
            {
                if (bytes.Length >= ceiling)
                {
                    throw TooLarge(maxBytes, sourceName, "The input is larger than the largest buffer the runtime can hold");
                }

                Array.Resize(ref bytes, (int)Math.Min(bytes.Length * 2L, ceiling));
            }

            // Ask for one chunk, not for all the room left: an endless stream is then read to the limit plus a chunk, not to
            // the end of a doubled buffer.
            int n = stream.Read(bytes, count, Math.Min(bytes.Length - count, ReadChunk));
            if (n <= 0)
            {
                break;
            }

            count += n;

            // Stop a few bytes past the limit (room for a byte order mark, which is not part of the size): the stream may
            // be endless, and the check below reports the excess exactly.
            if (count - 4L > maxBytes)
            {
                throw TooLarge(maxBytes, sourceName);
            }
        }

        (Encoding encoding, int mark) = Detect(bytes.AsSpan(0, count));
        if (count - mark > maxBytes)
        {
            throw TooLarge(maxBytes, sourceName);
        }

        try
        {
            // A string holds fewer characters than an array holds bytes; count before decoding so a text that decodes
            // to more is a limit error, not an out-of-memory error from the decoder.
            if (count - mark > stringCeiling && encoding.GetCharCount(bytes, mark, count - mark) > stringCeiling)
            {
                throw TooLarge(maxBytes, sourceName, "The text is longer than a string can hold");
            }

            return encoding.GetString(bytes, mark, count - mark);
        }
        catch (DecoderFallbackException error)
        {
            // Name the first bad position: the text before it decodes, so its lines and characters can be counted.
            int good = Math.Clamp(error.Index, 0, count - mark);
            // Decode it in the encoding that was detected: counting the raw bytes of UTF-16 or UTF-32 would put NULs in the
            // columns. Some decoders report the end of a bad unit rather than its start (a lone UTF-16 high surrogate is
            // only known to be bad when the next unit arrives), so step back until the prefix decodes cleanly.
            string head = string.Empty;
            for (int length = good, tries = 0; length >= 0 && tries < 8; length--, tries++)
            {
                try
                {
                    head = encoding.GetString(bytes, mark, length);
                    break;
                }
                catch (DecoderFallbackException)
                {
                }
            }

            Mark at = MarkAfter(head);
            throw new SyntaxErrorException(sourceName, at, at, "The input is not valid in its character encoding.");
        }
    }

    private static YamlException TooLarge(long maxBytes, string? sourceName, string? what = null) =>
        ErrorMap.Translate(new Oy.YamlException(what is null ? $"The input is larger than the limit MaxInputBytes ({maxBytes})." : $"{what} (limit MaxInputBytes is {maxBytes}).") { Category = Oy.YamlErrorCategory.Limit }, default, sourceName);

    private static (Encoding Encoding, int Mark) Detect(ReadOnlySpan<byte> b)
    {
        var utf8 = new UTF8Encoding(false, true);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        {
            return (new UTF32Encoding(true, false, true), 4);
        }

        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        {
            return (new UTF32Encoding(false, false, true), 4);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false, true), 2);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false, true), 2);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return (utf8, 3);
        }

        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] != 0)
        {
            return (new UTF32Encoding(true, false, true), 0);
        }

        if (b.Length >= 4 && b[0] != 0 && b[1] == 0 && b[2] == 0 && b[3] == 0)
        {
            return (new UTF32Encoding(false, false, true), 0);
        }

        if (b.Length >= 2 && b[0] == 0 && b[1] != 0)
        {
            return (new UnicodeEncoding(true, false, true), 0);
        }

        if (b.Length >= 2 && b[0] != 0 && b[1] == 0)
        {
            return (new UnicodeEncoding(false, false, true), 0);
        }

        return (utf8, 0);
    }

    private static Mark MarkAfter(string head)
    {
        int line = 0;
        int lineStart = 0;
        for (int i = 0; i < head.Length; i++)
        {
            if (head[i] == '\n' || (head[i] == '\r' && !(i + 1 < head.Length && head[i + 1] == '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Mark(head.Length, line, head.Length - lineStart);
    }
}
