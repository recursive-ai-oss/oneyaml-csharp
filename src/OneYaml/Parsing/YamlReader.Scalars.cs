using System;
using System.Buffers;

namespace OneYaml.Parsing;

internal ref partial struct YamlReader
{
    // Plain scalars run on through spaces; they stop at a line break, a ':' that ends the
    // text, or a '#' that follows whitespace. In flow context the flow indicators stop them too.
    private static readonly SearchValues<byte> s_blockPlainStop = SearchValues.Create(":\n\r#"u8);
    private static readonly SearchValues<byte> s_flowPlainStop = SearchValues.Create(":\n\r#,[]{}"u8);
    private static readonly SearchValues<byte> s_singleQuoteStop = SearchValues.Create("'\n\r"u8);
    private static readonly SearchValues<byte> s_doubleQuoteStop = SearchValues.Create("\"\\\n\r"u8);

    private void CheckScalarSize(int length, int offset)
    {
        if (length > _limits.MaxScalarBytes)
        {
            throw LimitError($"A scalar is larger than the limit MaxScalarBytes ({_limits.MaxScalarBytes}).", offset);
        }
    }

    private void FetchPlainScalar()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;
        ReadOnlySpan<byte> s = _src;
        int len = s.Length;
        int start = _pos;
        bool flow = _flowLevel > 0;
        SearchValues<byte> stop = flow ? s_flowPlainStop : s_blockPlainStop;
        int indentRequired = _indent + 1;
        int p = start;
        int end;
        int lineStart = _lineStart;
        bool multiLine = false;
        while (true)
        {
            // One run of text on one line.
            while (true)
            {
                int rel = s[p..].IndexOfAny(stop);
                if (rel < 0)
                {
                    p = len;
                    break;
                }

                p += rel;
                byte b = s[p];
                if (b == (byte)':')
                {
                    byte n = At(p + 1);
                    if (IsBlankZ(n) || (flow && IsFlowIndicator(n)))
                    {
                        break;
                    }

                    p++;
                    continue;
                }

                if (b == (byte)'#')
                {
                    if (p > start && IsBlank(s[p - 1]))
                    {
                        break;
                    }

                    p++;
                    continue;
                }

                break;
            }

            end = p;
            while (end > start && IsBlank(s[end - 1]))
            {
                end--;
            }

            if (p >= len || (s[p] != (byte)'\n' && s[p] != (byte)'\r'))
            {
                break;
            }

            // Look ahead over blank lines to see whether the scalar goes on.
            int q = p;
            int newLine = lineStart;
            bool more = true;
            while (true)
            {
                while (q < len && IsBlank(s[q]))
                {
                    q++;
                }

                if (q >= len)
                {
                    more = false;
                    break;
                }

                byte d = s[q];
                if (d == (byte)'\n' || d == (byte)'\r')
                {
                    q += d == (byte)'\r' && At(q + 1) == (byte)'\n' ? 2 : 1;
                    newLine = q;
                    if (IsMarkerAtLineStart(q))
                    {
                        more = false;
                        break;
                    }

                    continue;
                }

                break;
            }

            if (!more)
            {
                break;
            }

            byte c = s[q];
            int spaces = 0;
            while (At(newLine + spaces) == (byte)' ')
            {
                spaces++;
            }

            // A line that starts with a byte order mark is not text of the scalar. In a flow
            // collection a continuation line needs the same indentation as any other line there;
            // a tab does not count towards it.
            if (c == (byte)'#' || (!flow && spaces < indentRequired) || (flow && spaces <= _indent) || (q == newLine && IsBomAt(q)))
            {
                break;
            }

            if (c == (byte)':')
            {
                byte n = At(q + 1);
                if (IsBlankZ(n) || (flow && IsFlowIndicator(n)))
                {
                    break;
                }
            }

            if (flow && IsFlowIndicator(c))
            {
                break;
            }

            multiLine = true;
            p = q;
            lineStart = newLine;
        }

        CheckScalarSize(end - start, start);
        CheckChars(start, end);
        Token t = default;
        t.Kind = TokenKind.Scalar;
        t.Style = (byte)ScalarStyle.Plain;
        t.Start = t.ContentStart = start;
        t.End = t.ContentEnd = end;
        t.Flags = multiLine ? (byte)1 : (byte)0;
        Enqueue(in t);
        _pos = end;
        _lineStart = lineStart;
    }

    private bool IsBomAt(int i) => At(i) == 0xEF && At(i + 1) == 0xBB && At(i + 2) == 0xBF;

    // Whether only blanks stand between i and the end of the line or the input.
    private bool BlankToEol(int i)
    {
        while (IsBlank(At(i)))
        {
            i++;
        }

        byte c = At(i);
        return i >= _src.Length || c == (byte)'\n' || c == (byte)'\r';
    }

    // A quoted scalar's continuation line must not start a document and, when it has text,
    // must be indented more than the enclosing block.
    private void CheckQuotedContinuation(int lineStart)
    {
        if (IsMarkerAtLineStart(lineStart))
        {
            throw Error("A document marker cannot appear inside a quoted scalar.", lineStart);
        }

        int spaces = 0;
        while (At(lineStart + spaces) == (byte)' ')
        {
            spaces++;
        }

        int r = lineStart + spaces;
        while (IsBlank(At(r)))
        {
            r++;
        }

        byte c = At(r);
        if (c == (byte)'\n' || c == (byte)'\r' || r >= _src.Length)
        {
            return;
        }

        if (spaces <= _indent)
        {
            throw Error("A line of a quoted scalar must be indented more than its parent.", lineStart + spaces);
        }
    }

    private void FetchSingleQuoted()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;
        ReadOnlySpan<byte> s = _src;
        int start = _pos;
        int p = start + 1;
        bool processed = false;
        int lineStart = _lineStart;
        while (true)
        {
            int rel = s[p..].IndexOfAny(s_singleQuoteStop);
            if (rel < 0)
            {
                throw Error("The single-quoted string is not closed.", s.Length);
            }

            p += rel;
            byte b = s[p];
            if (b == (byte)'\'')
            {
                if (At(p + 1) == (byte)'\'')
                {
                    p += 2;
                    processed = true;
                    continue;
                }

                break;
            }

            p += b == (byte)'\r' && At(p + 1) == (byte)'\n' ? 2 : 1;
            processed = true;
            lineStart = p;
            CheckQuotedContinuation(p);
        }

        CheckScalarSize(p - start - 1, start);
        Token t = default;
        t.Kind = TokenKind.Scalar;
        t.Style = (byte)ScalarStyle.SingleQuoted;
        t.Start = start;
        t.End = p + 1;
        t.ContentStart = start + 1;
        t.ContentEnd = p;
        t.Flags = processed ? (byte)1 : (byte)0;
        Enqueue(in t);
        _pos = p + 1;
        _lineStart = lineStart;
        _prevJson = true;
    }

    private void FetchDoubleQuoted()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;
        ReadOnlySpan<byte> s = _src;
        int start = _pos;
        int p = start + 1;
        bool processed = false;
        int lineStart = _lineStart;
        while (true)
        {
            int rel = s[p..].IndexOfAny(s_doubleQuoteStop);
            if (rel < 0)
            {
                throw Error("The double-quoted string is not closed.", s.Length);
            }

            p += rel;
            byte b = s[p];
            if (b == (byte)'"')
            {
                break;
            }

            processed = true;
            if (b == (byte)'\\')
            {
                byte e = At(p + 1);
                switch (e)
                {
                    case (byte)'0':
                    case (byte)'a':
                    case (byte)'b':
                    case (byte)'t':
                    case (byte)'\t':
                    case (byte)'n':
                    case (byte)'v':
                    case (byte)'f':
                    case (byte)'r':
                    case (byte)'e':
                    case (byte)' ':
                    case (byte)'"':
                    case (byte)'/':
                    case (byte)'\\':
                    case (byte)'N':
                    case (byte)'_':
                    case (byte)'L':
                    case (byte)'P':
                        p += 2;
                        break;
                    case (byte)'x':
                        p = CheckHexEscape(p, 2);
                        break;
                    case (byte)'u':
                        p = CheckHexEscape(p, 4);
                        break;
                    case (byte)'U':
                        p = CheckHexEscape(p, 8);
                        break;
                    case (byte)'\n':
                        p += 2;
                        lineStart = p;
                        CheckQuotedContinuation(p);
                        break;
                    case (byte)'\r':
                        p += At(p + 2) == (byte)'\n' ? 3 : 2;
                        lineStart = p;
                        CheckQuotedContinuation(p);
                        break;
                    default:
                        throw Error("This escape sequence is not valid in a double-quoted string.", p + 1);
                }

                continue;
            }

            p += b == (byte)'\r' && At(p + 1) == (byte)'\n' ? 2 : 1;
            lineStart = p;
            CheckQuotedContinuation(p);
        }

        CheckScalarSize(p - start - 1, start);
        Token t = default;
        t.Kind = TokenKind.Scalar;
        t.Style = (byte)ScalarStyle.DoubleQuoted;
        t.Start = start;
        t.End = p + 1;
        t.ContentStart = start + 1;
        t.ContentEnd = p;
        t.Flags = processed ? (byte)1 : (byte)0;
        Enqueue(in t);
        _pos = p + 1;
        _lineStart = lineStart;
        _prevJson = true;
    }

    // p is at the backslash; returns the offset after the escape.
    private int CheckHexEscape(int p, int digits)
    {
        uint value = 0;
        for (int i = 0; i < digits; i++)
        {
            byte h = At(p + 2 + i);
            if (!IsHex(h))
            {
                throw Error("An escape sequence needs hexadecimal digits.", p + 2 + i);
            }

            value = (value << 4) | (uint)HexValue(h);
        }

        if (digits == 4 && value >= 0xD800 && value <= 0xDBFF)
        {
            // A high surrogate is only half a character: JSON writes characters outside the BMP as a pair of
            // \u escapes, so the next escape must be the low half. Anything else is refused.
            if (At(p + 6) != (byte)'\\' || At(p + 7) != (byte)'u')
            {
                throw Error("A high surrogate escape must be followed by a low surrogate escape.", p);
            }

            uint low = 0;
            for (int i = 0; i < 4; i++)
            {
                byte h = At(p + 8 + i);
                if (!IsHex(h))
                {
                    throw Error("An escape sequence needs hexadecimal digits.", p + 8 + i);
                }

                low = (low << 4) | (uint)HexValue(h);
            }

            if (low < 0xDC00 || low > 0xDFFF)
            {
                throw Error("A high surrogate escape must be followed by a low surrogate escape.", p);
            }

            return p + 12;
        }

        if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
        {
            throw Error("An escape sequence does not name a valid character.", p);
        }

        return p + 2 + digits;
    }

    private void FetchBlockScalar(bool literal)
    {
        RemoveSimpleKey();
        _simpleKeyAllowed = true;
        ReadOnlySpan<byte> s = _src;
        int len = s.Length;
        int start = _pos;
        int p = start + 1;
        Chomping chomp = Chomping.Clip;
        int explicitIndent = 0;
        for (int i = 0; i < 2; i++)
        {
            byte c = At(p);
            if (c == (byte)'+' || c == (byte)'-')
            {
                if (chomp != Chomping.Clip)
                {
                    throw Error("A block scalar header has two chomping indicators.", p);
                }

                chomp = c == (byte)'+' ? Chomping.Keep : Chomping.Strip;
                p++;
            }
            else if (c >= (byte)'1' && c <= (byte)'9')
            {
                if (explicitIndent != 0)
                {
                    throw Error("A block scalar header has two indentation indicators.", p);
                }

                explicitIndent = c - '0';
                p++;
            }
            else if (c == (byte)'0')
            {
                throw Error("The indentation indicator of a block scalar cannot be 0.", p);
            }
            else
            {
                break;
            }
        }

        int afterIndicators = p;
        while (IsBlank(At(p)))
        {
            p++;
        }

        if (At(p) == (byte)'#')
        {
            if (p == afterIndicators)
            {
                throw Error("A comment must be separated from the text before it by whitespace.", p);
            }

            int rel = s[p..].IndexOfAny(s_lineBreaks);
            int end = rel < 0 ? len : p + rel;
            CheckChars(p, end);
            RecordComment(p, end);
            p = end;
        }

        byte after = At(p);
        if (p < len && after != (byte)'\n' && after != (byte)'\r')
        {
            throw Error("Unexpected text after the block scalar header.", p);
        }

        if (p < len)
        {
            p += after == (byte)'\r' && At(p + 1) == (byte)'\n' ? 2 : 1;
        }

        int bodyStart = p;
        int parent = _indent;
        int minIndent = parent < 0 ? 0 : parent + 1;
        int contentIndent;
        if (explicitIndent != 0)
        {
            contentIndent = parent + explicitIndent;
        }
        else
        {
            // The first line with text sets the indentation; blank lines before it may not be longer.
            int q = bodyStart;
            int longestBlank = 0;
            int firstIndent = -1;
            while (q < len)
            {
                int spaces = 0;
                while (At(q + spaces) == (byte)' ')
                {
                    spaces++;
                }

                byte d = At(q + spaces);
                if (d == (byte)'\n' || d == (byte)'\r')
                {
                    longestBlank = Math.Max(longestBlank, spaces);
                    q += spaces + (d == (byte)'\r' && At(q + spaces + 1) == (byte)'\n' ? 2 : 1);
                    continue;
                }

                if (q + spaces >= len)
                {
                    longestBlank = Math.Max(longestBlank, spaces);
                    break;
                }

                firstIndent = spaces;
                break;
            }

            if (firstIndent < 0 || firstIndent < minIndent)
            {
                // No line of text belongs to the scalar, so the longest blank line sets the indentation
                // whether the stream ends or the next token follows (a value must not depend on what comes after).
                contentIndent = Math.Max(minIndent, longestBlank);
            }
            else
            {
                if (firstIndent >= minIndent && longestBlank > firstIndent)
                {
                    throw Error("A blank line before the first line of text has more spaces than that line.", bodyStart);
                }

                contentIndent = Math.Max(firstIndent, minIndent);
            }
        }

        // Consume lines that belong to the scalar: indented lines and blank lines.
        bool hadText = false;
        while (p < len)
        {
            int spaces = 0;
            while (spaces < contentIndent && At(p + spaces) == (byte)' ')
            {
                spaces++;
            }

            byte d = At(p + spaces);
            if (spaces < contentIndent)
            {
                if (d == (byte)'\n' || d == (byte)'\r')
                {
                    p += spaces + (d == (byte)'\r' && At(p + spaces + 1) == (byte)'\n' ? 2 : 1);
                    continue;
                }

                if (p + spaces >= len)
                {
                    p = len;
                }
                else if (d == (byte)'\t' && !(hadText && BlankToEol(p + spaces)))
                {
                    // Once the text has set the indentation, a line of only white space after it is
                    // a comment line, not content; anything else here is a tab used as indentation.
                    throw Error("A tab cannot be used as indentation after a block scalar.", p + spaces);
                }

                break;
            }

            if (spaces == 0 && IsMarkerAtLineStart(p))
            {
                break;
            }

            int rel = s[(p + spaces)..].IndexOfAny(s_lineBreaks);
            if (rel < 0)
            {
                p = len;
                break;
            }

            hadText = true;
            int eol = p + spaces + rel;
            p = eol + (s[eol] == (byte)'\r' && At(eol + 1) == (byte)'\n' ? 2 : 1);
        }

        CheckChars(bodyStart, p);

        CheckScalarSize(p - bodyStart, start);
        Token t = default;
        t.Kind = TokenKind.Scalar;
        t.Style = (byte)(literal ? ScalarStyle.Literal : ScalarStyle.Folded);
        t.Start = start;
        t.End = p;
        t.ContentStart = bodyStart;
        t.ContentEnd = p;
        t.X = contentIndent;
        t.Y = (int)chomp;
        t.Flags = 1;
        Enqueue(in t);
        _pos = p;
        _lineStart = p;
    }
}
