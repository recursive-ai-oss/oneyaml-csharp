using System;
using System.Text;

namespace OneYaml;

/// <summary>
/// The error every YAML operation reports: malformed text, malformed encoding, an exceeded
/// limit, or a failed binding or patch.
/// </summary>
public class YamlException : Exception
{
    /// <summary>Creates an exception with no message and no position.</summary>
    public YamlException()
        : this("A YAML error occurred.", YamlErrorCategory.Syntax, 0, 0, -1)
    {
    }

    /// <summary>Creates an exception with a message and no position.</summary>
    /// <param name="message">The message.</param>
    public YamlException(string message)
        : this(message, YamlErrorCategory.Syntax, 0, 0, -1)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public YamlException(string message, Exception? innerException)
        : this(message, YamlErrorCategory.Syntax, 0, 0, -1, null, innerException)
    {
    }

    /// <summary>Creates an exception with a category and a position.</summary>
    /// <param name="message">The message, meant for people; its text is not a stable contract.</param>
    /// <param name="category">The kind of error.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column in characters, or 0 when unknown.</param>
    /// <param name="offset">The byte offset in the UTF-8 text, or -1 when unknown.</param>
    /// <param name="operationIndex">For a patch, the index of the failing operation.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public YamlException(
        string message,
        YamlErrorCategory category,
        int line,
        int column,
        long offset,
        int? operationIndex = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        Line = line;
        Column = column;
        Offset = offset;
        OperationIndex = operationIndex;
    }

    /// <summary>The kind of error.</summary>
    public YamlErrorCategory Category { get; }

    /// <summary>
    /// The 1-based line of the error, counting LF, CRLF and a lone CR as one break each, or 0 when
    /// the error has no position.
    /// </summary>
    public int Line { get; }

    /// <summary>The 1-based column of the error in decoded characters (a byte order mark is not counted), or 0.</summary>
    public int Column { get; }

    /// <summary>The byte offset of the error in the UTF-8 text the parser read, or -1 when unknown.</summary>
    public long Offset { get; }

    /// <summary>For a patch, the zero-based index of the operation that failed; otherwise <see langword="null"/>.</summary>
    public int? OperationIndex { get; }

    // The position is worked out here, when the error is built, so reading never tracks columns.
    internal static YamlException At(YamlErrorCategory category, string message, ReadOnlySpan<byte> utf8, int offset)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        if (offset > utf8.Length)
        {
            offset = utf8.Length;
        }

        ReadOnlySpan<byte> before = utf8[..offset];
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < before.Length; i++)
        {
            byte b = before[i];
            if (b == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
            else if (b == (byte)'\r')
            {
                bool crlf = i + 1 < utf8.Length && utf8[i + 1] == (byte)'\n';
                if (!crlf)
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        // A byte order mark at the start of a line is not counted.
        if (lineStart + 2 < utf8.Length && utf8[lineStart] == 0xEF && utf8[lineStart + 1] == 0xBB && utf8[lineStart + 2] == 0xBF)
        {
            lineStart += 3;
        }

        int column = 1;
        for (int i = lineStart; i < before.Length; i++)
        {
            // Count characters, not bytes: skip UTF-8 continuation bytes.
            if ((before[i] & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new YamlException(message, category, line, column, offset);
    }

    internal static YamlException At(YamlErrorCategory category, string message, ReadOnlySpan<char> text, int charIndex)
    {
        if (charIndex < 0)
        {
            charIndex = 0;
        }

        if (charIndex > text.Length)
        {
            charIndex = text.Length;
        }

        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < charIndex; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = 1;
        for (int i = lineStart; i < charIndex; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
            {
                column++;
            }
        }

        return new YamlException(message, category, line, column, Encoding.UTF8.GetByteCount(text[..charIndex]));
    }
}
