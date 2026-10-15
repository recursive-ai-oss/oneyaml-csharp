using System;
using System.IO;
using System.Text;

namespace OneYaml.Compat.SharpYaml;

// Reads the text of a stream the way the engine reads bytes: a byte order mark or the null bytes of an ASCII first
// character pick UTF-8, UTF-16 or UTF-32, and bytes that are not valid in that encoding are an error. A decoder that
// substitutes U+FFFD would turn damaged input into different, valid-looking text.
internal static class StreamText
{
    public static string Read(Stream stream, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes;
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }

        (Encoding encoding, int mark) = Detect(bytes);
        try
        {
            return encoding.GetString(bytes, mark, bytes.Length - mark);
        }
        catch (DecoderFallbackException error)
        {
            // Name the first bad position: the text before it decodes, so its lines and characters can be counted.
            int good = Math.Clamp(error.Index, 0, bytes.Length - mark);
            string head = new UTF8Encoding(false, false).GetString(bytes, mark, good);
            Mark at = MarkAfter(head);
            throw new SyntaxErrorException(sourceName, at, at, "The input is not valid in its character encoding.");
        }
    }

    private static (Encoding Encoding, int Mark) Detect(byte[] b)
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
