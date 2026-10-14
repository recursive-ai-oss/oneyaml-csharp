using System;
namespace OneYaml.Emitting;

internal readonly partial struct Utf8YamlWriter
{
    /// <summary>
    /// Writes a string as a literal block scalar (<c>|</c>). The header is chosen from the value:
    /// an indentation indicator when the first line starts with a space, and the chomping
    /// indicator from the trailing line breaks. The text starts after the caller's <c>key: </c>
    /// and ends with a line break, so the caller continues on a new line.
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash (0 or more).</param>
    /// <param name="contentIndent">Column of the content lines; 1 to 9 more than <paramref name="parentIndent"/>.</param>
    /// <returns>
    /// <see langword="false"/> and nothing written when a block scalar cannot hold the text: it is
    /// empty or only line breaks, has characters that need escapes, has a line of only blanks or
    /// one that starts with a tab, or the indent does not fit.
    /// </returns>
    public bool WriteLiteral(ReadOnlySpan<byte> text, int parentIndent, int contentIndent) =>
        CanWriteBlock(text, parentIndent, contentIndent) && WriteBlock(text, folded: false, parentIndent, contentIndent);

    /// <summary>
    /// Writes a string as a folded block scalar (<c>&gt;</c>), with the blank lines that make the
    /// folded reading reproduce the exact line structure. Same contract as <see cref="WriteLiteral"/>.
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash (0 or more).</param>
    /// <param name="contentIndent">Column of the content lines; 1 to 9 more than <paramref name="parentIndent"/>.</param>
    /// <returns><see langword="false"/> and nothing written when a block scalar cannot hold the text.</returns>
    public bool WriteFolded(ReadOnlySpan<byte> text, int parentIndent, int contentIndent) =>
        CanWriteBlock(text, parentIndent, contentIndent) && WriteBlock(text, folded: true, parentIndent, contentIndent);

    /// <summary>Tells whether a block scalar (literal or folded) can hold the text at these indents.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash.</param>
    /// <param name="contentIndent">Column of the content lines.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool CanWriteBlock(ReadOnlySpan<byte> text, int parentIndent, int contentIndent)
    {
        int delta = contentIndent - parentIndent;
        if (parentIndent < 0 || delta < 1 || delta > 9)
        {
            return false;
        }

        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        if (body.IsEmpty || (ScalarText.Analyze(text) & TextTraits.NeedsEscape) != 0)
        {
            return false;
        }

        // A line of only blanks is ambiguous with an empty line shorter than the indent, and
        // readers disagree about a tab where the indentation ends, so both go to double quotes.
        ReadOnlySpan<byte> rest = body;
        while (true)
        {
            int nl = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl < 0 ? rest : rest.Slice(0, nl);
            if (!line.IsEmpty && (line.IndexOfAnyExcept(" \t"u8) < 0 || line[0] == (byte)'\t'))
            {
                return false;
            }

            if (nl < 0)
            {
                return true;
            }

            rest = rest.Slice(nl + 1);
        }
    }

    // The caller has checked CanWriteBlock.
    private bool WriteBlock(ReadOnlySpan<byte> text, bool folded, int parentIndent, int contentIndent)
    {
        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        int trailing = text.Length - body.Length;

        Span<byte> header = stackalloc byte[4];
        int h = 0;
        header[h++] = folded ? (byte)'>' : (byte)'|';

        // The first non-empty line starting with a space hides the indent from the reader.
        if (body.TrimStart((byte)'\n')[0] == (byte)' ')
        {
            header[h++] = (byte)('0' + (contentIndent - parentIndent));
        }

        if (trailing == 0)
        {
            header[h++] = (byte)'-';
        }
        else if (trailing >= 2)
        {
            header[h++] = (byte)'+';
        }

        header[h++] = (byte)'\n';
        WriteRaw(header.Slice(0, h));

        ReadOnlySpan<byte> rest = body;
        int pendingEmpty = 0;
        bool havePrevious = false;
        bool previousSpaced = false;
        while (true)
        {
            int nl = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl < 0 ? rest : rest.Slice(0, nl);
            if (line.IsEmpty)
            {
                pendingEmpty++;
            }
            else
            {
                bool spaced = line[0] is (byte)' ' or (byte)'\t';

                // Folding turns a lone break between two ordinary lines into a space, so one more
                // break is written there; a lone break next to an indented line is kept as it is.
                if (folded && havePrevious && !previousSpaced && !spaced)
                {
                    pendingEmpty++;
                }

                WriteEmptyLines(pendingEmpty);
                WriteBlockLine(line, contentIndent);
                pendingEmpty = 0;
                havePrevious = true;
                previousSpaced = spaced;
            }

            if (nl < 0)
            {
                break;
            }

            rest = rest.Slice(nl + 1);
        }

        // Keep chomping: the blank lines after the last line are part of the value.
        WriteEmptyLines(trailing >= 2 ? trailing - 1 : 0);
        return true;
    }

    private void WriteBlockLine(ReadOnlySpan<byte> line, int indent)
    {
        int total = indent + line.Length + 1;
        Span<byte> dst = _output.GetSpan(total);
        dst.Slice(0, indent).Fill((byte)' ');
        line.CopyTo(dst.Slice(indent));
        dst[total - 1] = (byte)'\n';
        _output.Advance(total);
    }

    private void WriteEmptyLines(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _output.GetSpan(count).Slice(0, count).Fill((byte)'\n');
        _output.Advance(count);
    }
}
