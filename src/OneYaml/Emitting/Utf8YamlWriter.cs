using System;
using System.Buffers;
using System.Text;
using OneYaml.Schema;

namespace OneYaml.Emitting;

/// <summary>
/// Writes YAML scalars as UTF-8 into an <see cref="IBufferWriter{T}"/>. Every scalar writer
/// either writes text that reads back as exactly the value given, or writes nothing and says
/// so. Text is never wrapped. Strings are passed as UTF-8 bytes and must be valid UTF-8;
/// malformed bytes are written as U+FFFD in double quotes. The string, bool, null, integer and
/// float writers allocate nothing after warm-up on net8 and net10 (measured);
/// <see cref="WriteBigInteger"/> allocates inside the runtime's formatter on net8 (about 1 KB for
/// a 400-digit value).
/// </summary>
internal readonly partial struct Utf8YamlWriter
{
    /// <summary>Longest implicit key YAML allows, in characters; longer keys need the explicit <c>? </c> form.</summary>
    public const int MaxImplicitKeyLength = 1024;

    // Bytes the double-quoted writer must look at one by one: controls, the quote, the backslash,
    // DEL and everything that starts a multi-byte character.
    private static readonly SearchValues<byte> DoubleQuotedAttention = SearchValues.Create(
        [.. ScalarText.MakeRange(0x00, 0x1F), (byte)'"', (byte)'\\', .. ScalarText.MakeRange(0x7F, 0xFF)]);

    private static readonly SearchValues<byte> BlockPlainSpecials = SearchValues.Create(":#"u8);

    // Flow collections avoid more: readers differ on "a:b" and on a question mark inside a plain scalar.
    private static readonly SearchValues<byte> FlowPlainSpecials = SearchValues.Create(",[]{}:#?"u8);

    private readonly IBufferWriter<byte> _output;

    /// <summary>Creates a writer over a buffer writer.</summary>
    /// <param name="output">Where the bytes go.</param>
    public Utf8YamlWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>Writes bytes as they are.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        bytes.CopyTo(_output.GetSpan(bytes.Length));
        _output.Advance(bytes.Length);
    }

    /// <summary>Writes one byte.</summary>
    /// <param name="value">The byte.</param>
    public void WriteByte(byte value)
    {
        _output.GetSpan(1)[0] = value;
        _output.Advance(1);
    }

    /// <summary>Writes spaces.</summary>
    /// <param name="count">How many.</param>
    public void WriteSpaces(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _output.GetSpan(count).Slice(0, count).Fill((byte)' ');
        _output.Advance(count);
    }

    /// <summary>
    /// Finds the style closest to the one asked for that reads back as exactly this string.
    /// The preferred style is kept when it can hold the text. Otherwise a plain scalar falls to
    /// single quotes, and text that quotes cannot carry falls to a literal block when dumping
    /// in a block value (or to double quotes when editing, which keeps the value on its line).
    /// </summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="preferred">The style the value has now or the caller would like.</param>
    /// <param name="context">Where the scalar sits.</param>
    /// <param name="mode">Whose reading a plain scalar must survive.</param>
    /// <param name="parentIndent">Column of the owning key or dash, for block scalars.</param>
    /// <param name="contentIndent">Column of the block scalar's lines.</param>
    /// <returns>
    /// The style to write with, or <see cref="ScalarWriteStyle.ExplicitKeyRequired"/> when the
    /// scalar is a key that is too long to be an implicit one in any style.
    /// </returns>
    public static ScalarWriteStyle ChooseStyle(
        ReadOnlySpan<byte> text,
        ScalarWriteStyle preferred,
        EmitContext context,
        StyleMode mode,
        int parentIndent = 0,
        int contentIndent = 2)
    {
        ScalarWriteStyle style = ChooseStyleCore(text, preferred, context, mode, parentIndent, contentIndent);
        if (context is EmitContext.BlockKey or EmitContext.FlowKey && IsTooLongForImplicitKey(text, style))
        {
            return ScalarWriteStyle.ExplicitKeyRequired;
        }

        return style;
    }

    private static ScalarWriteStyle ChooseStyleCore(
        ReadOnlySpan<byte> text,
        ScalarWriteStyle preferred,
        EmitContext context,
        StyleMode mode,
        int parentIndent,
        int contentIndent)
    {
        TextTraits traits = ScalarText.Analyze(text);
        switch (preferred)
        {
            case ScalarWriteStyle.DoubleQuoted:
                return ScalarWriteStyle.DoubleQuoted;
            case ScalarWriteStyle.Literal:
            case ScalarWriteStyle.Folded:
                return context == EmitContext.BlockValue && CanWriteBlock(text, parentIndent, contentIndent)
                    ? preferred
                    : ScalarWriteStyle.DoubleQuoted;
            case ScalarWriteStyle.Plain:
                if (CanBePlain(text, traits, context, mode))
                {
                    return ScalarWriteStyle.Plain;
                }

                break;
        }

        if ((traits & (TextTraits.LineBreak | TextTraits.NeedsEscape)) == 0)
        {
            return ScalarWriteStyle.SingleQuoted;
        }

        if (mode == StyleMode.Dump && context == EmitContext.BlockValue
            && CanWriteBlock(text, parentIndent, contentIndent))
        {
            return ScalarWriteStyle.Literal;
        }

        return ScalarWriteStyle.DoubleQuoted;
    }

    /// <summary>Chooses a style with <see cref="ChooseStyle"/> and writes the string with it.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="preferred">The preferred style.</param>
    /// <param name="context">Where the scalar sits.</param>
    /// <param name="mode">Whose reading a plain scalar must survive.</param>
    /// <param name="parentIndent">Column of the owning key or dash, for block scalars.</param>
    /// <param name="contentIndent">Column of the block scalar's lines.</param>
    /// <param name="existingHeader">The header the scalar has now, kept for a block style while it still reads back exactly.</param>
    /// <returns>
    /// The style used. A block style means the text ended with a line break.
    /// <see cref="ScalarWriteStyle.ExplicitKeyRequired"/> means a key too long to be implicit:
    /// nothing was written and the caller writes <c>? </c> and the key itself.
    /// </returns>
    public ScalarWriteStyle WriteString(
        ReadOnlySpan<byte> text,
        ScalarWriteStyle preferred,
        EmitContext context,
        StyleMode mode,
        int parentIndent = 0,
        int contentIndent = 2,
        BlockHeader existingHeader = default)
    {
        ScalarWriteStyle style = ChooseStyle(text, preferred, context, mode, parentIndent, contentIndent);
        switch (style)
        {
            case ScalarWriteStyle.ExplicitKeyRequired:
                break;
            case ScalarWriteStyle.Plain:
                WriteRaw(text);
                break;
            case ScalarWriteStyle.SingleQuoted:
                WriteSingleQuotedCore(text);
                break;
            case ScalarWriteStyle.DoubleQuoted:
                WriteDoubleQuoted(text);
                break;
            default:
                WriteBlock(text, style == ScalarWriteStyle.Folded, parentIndent, contentIndent, existingHeader);
                break;
        }

        return style;
    }

    /// <summary>Tells whether a string can be written plain and read back as the same string.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="context">Where the scalar sits.</param>
    /// <param name="mode">Whose reading it must survive.</param>
    /// <returns><see langword="true"/> when plain is safe.</returns>
    public static bool CanBePlain(ReadOnlySpan<byte> text, EmitContext context, StyleMode mode) =>
        CanBePlain(text, ScalarText.Analyze(text), context, mode);

    private static bool CanBePlain(ReadOnlySpan<byte> text, TextTraits traits, EmitContext context, StyleMode mode)
    {
        if (text.IsEmpty || (traits & (TextTraits.LineBreak | TextTraits.NeedsEscape | TextTraits.Tab)) != 0)
        {
            return false;
        }

        if (context is EmitContext.BlockKey or EmitContext.FlowKey && IsTooLongForImplicitKey(text, ScalarWriteStyle.Plain))
        {
            return false;
        }

        bool flow = context is EmitContext.FlowValue or EmitContext.FlowKey;
        byte first = text[0];
        byte last = text[^1];
        if (first == (byte)' ' || last == (byte)' ' || last == (byte)':')
        {
            return false;
        }

        switch (first)
        {
            case (byte)',':
            case (byte)'[':
            case (byte)']':
            case (byte)'{':
            case (byte)'}':
            case (byte)'#':
            case (byte)'&':
            case (byte)'*':
            case (byte)'!':
            case (byte)'|':
            case (byte)'>':
            case (byte)'\'':
            case (byte)'"':
            case (byte)'%':
            case (byte)'@':
            case (byte)'`':
            case (byte)':':
                return false;
            case (byte)'-':
            case (byte)'?':
                // Some readers take "?x" inside flow collections as the start of an explicit key.
                if (text.Length == 1 || text[1] == (byte)' ' || (flow && (first == (byte)'?' || IsFlowIndicator(text[1]))))
                {
                    return false;
                }

                break;
        }

        // A document marker at the start of a line would end or start a document.
        if (text.Length >= 3 && (text.StartsWith("---"u8) || text.StartsWith("..."u8))
            && (text.Length == 3 || text[3] == (byte)' '))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = text;
        int offset = 0;
        SearchValues<byte> specials = flow ? FlowPlainSpecials : BlockPlainSpecials;
        while (true)
        {
            int at = rest.IndexOfAny(specials);
            if (at < 0)
            {
                break;
            }

            int pos = offset + at;
            byte c = text[pos];
            if (c == (byte)'#')
            {
                if (pos > 0 && text[pos - 1] == (byte)' ')
                {
                    return false;
                }
            }
            else if (c == (byte)':')
            {
                // Flow context: any colon is avoided, readers differ on "a:b". Block: only ": " matters.
                if (flow || pos + 1 == text.Length || text[pos + 1] == (byte)' ')
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            rest = rest.Slice(at + 1);
            offset = pos + 1;
        }

        // Some readers scan an implicit key as the start of an anchor, alias or tag after a space.
        if (context is EmitContext.BlockKey or EmitContext.FlowKey && HasSpaceBeforeProperty(text))
        {
            return false;
        }

        if (CoreSchema.ResolvePlain(text) != ScalarKind.String)
        {
            return false;
        }

        return mode == StyleMode.Edit || !Yaml11.LooksNonString(text);
    }

    // Every escape is at most six characters ("\u0001"), so short keys need no exact count.
    private const int AlwaysShortKeyBytes = (MaxImplicitKeyLength - 2) / 6;

    /// <summary>
    /// Tells whether a key written in this style would be longer than an implicit key may be: 1,024
    /// characters including quotes and escapes.
    /// </summary>
    private static bool IsTooLongForImplicitKey(ReadOnlySpan<byte> text, ScalarWriteStyle style)
    {
        if (text.Length <= AlwaysShortKeyBytes)
        {
            return false;
        }

        return WrittenKeyLength(text, style) > MaxImplicitKeyLength;
    }

    private static long WrittenKeyLength(ReadOnlySpan<byte> text, ScalarWriteStyle style)
    {
        switch (style)
        {
            case ScalarWriteStyle.Plain:
                return CountCharacters(text);
            case ScalarWriteStyle.SingleQuoted:
                int quotes = 0;
                ReadOnlySpan<byte> rest = text;
                int at;
                while ((at = rest.IndexOf((byte)'\'')) >= 0)
                {
                    quotes++;
                    rest = rest.Slice(at + 1);
                }

                return CountCharacters(text) + quotes + 2;
            default:
                return DoubleQuotedLength(text);
        }
    }

    // Characters of valid UTF-8: every byte that does not continue a character.
    private static int CountCharacters(ReadOnlySpan<byte> text)
    {
        int count = 0;
        foreach (byte b in text)
        {
            if ((b & 0xC0) != 0x80)
            {
                count++;
            }
        }

        return count;
    }

    // Mirrors WriteDoubleQuoted: what each character costs once escaped.
    private static long DoubleQuotedLength(ReadOnlySpan<byte> text)
    {
        long length = 2;
        ReadOnlySpan<byte> rest = text;
        while (!rest.IsEmpty)
        {
            byte b = rest[0];
            if (b < 0x80)
            {
                length += b switch
                {
                    (byte)'"' or (byte)'\\' or 0 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 27 => 2,
                    < 0x20 or 0x7F => 4,
                    _ => 1,
                };
                rest = rest.Slice(1);
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf8(rest, out Rune rune, out int used);
            if (status != OperationStatus.Done)
            {
                length += 6;
                rest = rest.Slice(Math.Max(used, 1));
                continue;
            }

            int cp = rune.Value;
            length += !ScalarText.MustEscape(cp) ? 1 : cp is 0x85 or 0x2028 or 0x2029 ? 2 : cp <= 0xFF ? 4 : 6;
            rest = rest.Slice(used);
        }

        return length;
    }

    private static bool HasSpaceBeforeProperty(ReadOnlySpan<byte> text)
    {
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == (byte)' ' && text[i + 1] is (byte)'*' or (byte)'&' or (byte)'!')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFlowIndicator(byte c) => c is (byte)',' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}';

    /// <summary>Writes a string plain when it can be, checked for the context and mode.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <param name="context">Where the scalar sits.</param>
    /// <param name="mode">Whose reading it must survive.</param>
    /// <returns><see langword="false"/> and nothing written when plain would not read back as the string.</returns>
    public bool WritePlain(ReadOnlySpan<byte> text, EmitContext context, StyleMode mode)
    {
        if (!CanBePlain(text, context, mode))
        {
            return false;
        }

        WriteRaw(text);
        return true;
    }

    /// <summary>Writes a string in single quotes.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    /// <returns><see langword="false"/> and nothing written when the text has line breaks or characters that need escapes.</returns>
    public bool WriteSingleQuoted(ReadOnlySpan<byte> text)
    {
        if ((ScalarText.Analyze(text) & (TextTraits.LineBreak | TextTraits.NeedsEscape)) != 0)
        {
            return false;
        }

        WriteSingleQuotedCore(text);
        return true;
    }

    private void WriteSingleQuotedCore(ReadOnlySpan<byte> text)
    {
        WriteByte((byte)'\'');
        ReadOnlySpan<byte> rest = text;
        while (true)
        {
            int at = rest.IndexOf((byte)'\'');
            if (at < 0)
            {
                WriteRaw(rest);
                break;
            }

            // Include the quote in the copy and write a second one: '' is an escaped quote.
            WriteRaw(rest.Slice(0, at + 1));
            WriteByte((byte)'\'');
            rest = rest.Slice(at + 1);
        }

        WriteByte((byte)'\'');
    }

    /// <summary>Writes a string in double quotes, escaping what must be escaped. Holds any text.</summary>
    /// <param name="text">The string's UTF-8 bytes.</param>
    public void WriteDoubleQuoted(ReadOnlySpan<byte> text)
    {
        WriteByte((byte)'"');
        ReadOnlySpan<byte> rest = text;
        while (!rest.IsEmpty)
        {
            int run = rest.IndexOfAny(DoubleQuotedAttention);
            if (run < 0)
            {
                WriteRaw(rest);
                break;
            }

            if (run > 0)
            {
                WriteRaw(rest.Slice(0, run));
                rest = rest.Slice(run);
            }

            if (rest[0] < 0x80)
            {
                WriteAsciiEscape(rest[0]);
                rest = rest.Slice(1);
                continue;
            }

            // Copy a run of printable non-ASCII characters in one piece.
            int printable = 0;
            while (printable < rest.Length && rest[printable] >= 0x80)
            {
                OperationStatus s = Rune.DecodeFromUtf8(rest.Slice(printable), out Rune r, out int used);
                if (s != OperationStatus.Done || ScalarText.MustEscape(r.Value))
                {
                    break;
                }

                printable += used;
            }

            if (printable > 0)
            {
                WriteRaw(rest.Slice(0, printable));
                rest = rest.Slice(printable);
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf8(rest, out Rune rune, out int consumed);
            WriteCodePointEscape(status == OperationStatus.Done ? rune.Value : 0xFFFD);
            rest = rest.Slice(Math.Max(consumed, 1));
        }

        WriteByte((byte)'"');
    }

    private void WriteAsciiEscape(byte b)
    {
        byte named = b switch
        {
            (byte)'"' => (byte)'"',
            (byte)'\\' => (byte)'\\',
            0 => (byte)'0',
            7 => (byte)'a',
            8 => (byte)'b',
            9 => (byte)'t',
            10 => (byte)'n',
            11 => (byte)'v',
            12 => (byte)'f',
            13 => (byte)'r',
            27 => (byte)'e',
            _ => 0,
        };

        if (named != 0)
        {
            Span<byte> two = _output.GetSpan(2);
            two[0] = (byte)'\\';
            two[1] = named;
            _output.Advance(2);
            return;
        }

        WriteCodePointEscape(b);
    }

    private void WriteCodePointEscape(int codePoint)
    {
        Span<byte> dst = _output.GetSpan(10);
        dst[0] = (byte)'\\';
        int digits;
        switch (codePoint)
        {
            case 0x85:
                dst[1] = (byte)'N';
                _output.Advance(2);
                return;
            case 0x2028:
                dst[1] = (byte)'L';
                _output.Advance(2);
                return;
            case 0x2029:
                dst[1] = (byte)'P';
                _output.Advance(2);
                return;
            case <= 0xFF:
                dst[1] = (byte)'x';
                digits = 2;
                break;
            case <= 0xFFFF:
                dst[1] = (byte)'u';
                digits = 4;
                break;
            default:
                dst[1] = (byte)'U';
                digits = 8;
                break;
        }

        for (int i = 0; i < digits; i++)
        {
            int nibble = (codePoint >> (4 * (digits - 1 - i))) & 0xF;
            dst[2 + i] = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
        }

        _output.Advance(2 + digits);
    }
}
