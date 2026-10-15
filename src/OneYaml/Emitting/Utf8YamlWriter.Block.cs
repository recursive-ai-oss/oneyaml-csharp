using System;
namespace OneYaml.Emitting;

internal readonly partial struct Utf8YamlWriter
{
    /// <summary>
    /// Writes a string as a literal block scalar (<c>|</c>). The header is chosen from the value:
    /// an indentation indicator when the first line starts with a space, and the chomping
    /// indicator from the trailing line breaks. An existing header is kept as far as the value
    /// allows (see <see cref="ResolveHeader"/>). The text starts after the caller's <c>key: </c>
    /// and ends with a line break, so the caller continues on a new line.
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash (0 or more), or -1 for the document root.</param>
    /// <param name="contentIndent">Column of the content lines; 1 to 9 more than <paramref name="parentIndent"/>.</param>
    /// <param name="existingHeader">The header the scalar has now; <c>default</c> for none.</param>
    /// <returns>
    /// <see langword="false"/> and nothing written when a block scalar cannot hold the text: it is
    /// empty or only line breaks, has characters that need escapes, has a line of only blanks or
    /// one that starts with a tab, or the indent does not fit.
    /// </returns>
    public bool WriteLiteral(ReadOnlySpan<byte> text, int parentIndent, int contentIndent, BlockHeader existingHeader = default) =>
        CanWriteBlock(text, parentIndent, contentIndent) && WriteBlock(text, folded: false, parentIndent, contentIndent, existingHeader);

    /// <summary>
    /// Writes a string as a folded block scalar (<c>&gt;</c>), with the blank lines that make the
    /// folded reading reproduce the exact line structure. Same contract as <see cref="WriteLiteral"/>.
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash (0 or more), or -1 for the document root.</param>
    /// <param name="contentIndent">Column of the content lines; 1 to 9 more than <paramref name="parentIndent"/>.</param>
    /// <param name="existingHeader">The header the scalar has now; <c>default</c> for none.</param>
    /// <returns><see langword="false"/> and nothing written when a block scalar cannot hold the text.</returns>
    public bool WriteFolded(ReadOnlySpan<byte> text, int parentIndent, int contentIndent, BlockHeader existingHeader = default) =>
        CanWriteBlock(text, parentIndent, contentIndent) && WriteBlock(text, folded: true, parentIndent, contentIndent, existingHeader);

    /// <summary>Tells whether a block scalar (literal or folded) can hold the text at these indents.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="parentIndent">Column of the owning key or dash, or -1 for the document root.</param>
    /// <param name="contentIndent">Column of the content lines.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool CanWriteBlock(ReadOnlySpan<byte> text, int parentIndent, int contentIndent)
    {
        int delta = contentIndent - parentIndent;
        if (parentIndent < -1 || delta < 1 || delta > 9)
        {
            return false;
        }

        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        if (body.IsEmpty || (ScalarText.Analyze(text) & TextTraits.NeedsEscape) != 0)
        {
            return false;
        }

        // At the document root the lines start at least one column in, so a line cannot be taken
        // for a document marker, and no indentation indicator is written because readers differ on
        // what it counts from; text that needs one stays in quotes.
        if (parentIndent < 0 && (contentIndent < 1 || NeedsIndentIndicator(body)))
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

    // The first non-empty line starting with a space hides the indent from the reader.
    private static bool NeedsIndentIndicator(ReadOnlySpan<byte> body) => body.TrimStart((byte)'\n')[0] == (byte)' ';

    /// <summary>
    /// Picks the header of a block scalar. The existing header is kept while the value still reads
    /// back exactly and changes only as far as the value needs: a keep indicator stays when the
    /// value ends with exactly one line break (the caller must then leave no blank line after the
    /// scalar, because keep would take it as part of the value), is added when the value ends with
    /// more, and a strip indicator is replaced when the value ends with a line break; an
    /// indentation indicator stays when it counts the right number of columns, and is added when
    /// the first line starts with a space.
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes; the caller has checked <see cref="CanWriteBlock"/>.</param>
    /// <param name="parentIndent">Column of the owning key or dash, or -1 for the document root.</param>
    /// <param name="contentIndent">Column of the content lines.</param>
    /// <param name="existing">The header the scalar has now; <c>default</c> for none.</param>
    /// <returns>The header to write.</returns>
    public static BlockHeader ResolveHeader(ReadOnlySpan<byte> text, int parentIndent, int contentIndent, BlockHeader existing = default)
    {
        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        int trailing = text.Length - body.Length;
        BlockChomping chomping = trailing == 0
            ? BlockChomping.Strip
            : trailing >= 2 || existing.Chomping == BlockChomping.Keep ? BlockChomping.Keep : BlockChomping.Clip;

        int delta = contentIndent - parentIndent;
        int indicator = parentIndent >= 0 && (NeedsIndentIndicator(body) || existing.IndentIndicator == delta) ? delta : 0;
        return new BlockHeader(chomping, indicator);
    }

    // The caller has checked CanWriteBlock.
    private bool WriteBlock(ReadOnlySpan<byte> text, bool folded, int parentIndent, int contentIndent, BlockHeader existingHeader = default)
    {
        ReadOnlySpan<byte> body = text.TrimEnd((byte)'\n');
        int trailing = text.Length - body.Length;
        BlockHeader chosen = ResolveHeader(text, parentIndent, contentIndent, existingHeader);

        Span<byte> header = stackalloc byte[4];
        int h = 0;
        header[h++] = folded ? (byte)'>' : (byte)'|';
        if (chosen.IndentIndicator != 0)
        {
            header[h++] = (byte)('0' + chosen.IndentIndicator);
        }

        if (chosen.Chomping == BlockChomping.Strip)
        {
            header[h++] = (byte)'-';
        }
        else if (chosen.Chomping == BlockChomping.Keep)
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
