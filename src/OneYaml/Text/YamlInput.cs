using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace OneYaml.Text;

/// <summary>The character encoding an input was stored in.</summary>
internal enum YamlEncoding : byte
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,
}

/// <summary>
/// An input stream normalised to validated UTF-8, with the facts needed to write it back in
/// its original encoding. Text that is already UTF-8 is used in place; everything else is
/// transcoded once into a pooled buffer.
/// </summary>
internal ref struct YamlInput
{
    // Control characters other than tab, line feed and carriage return, DEL, and the lead
    // bytes of the few code points YAML does not allow (C1 controls, U+FFFE and U+FFFF).
    private static readonly SearchValues<byte> s_suspect = SearchValues.Create(
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0B, 0x0C, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x7F, 0xC2, 0xEF,
        ]);

    private byte[]? _rented;
    private ReadOnlySpan<byte> _utf8;

    private YamlInput(ReadOnlySpan<byte> utf8, byte[]? rented, YamlEncoding encoding, bool hasBom)
    {
        _utf8 = utf8;
        _rented = rented;
        Encoding = encoding;
        HasByteOrderMark = hasBom;
    }

    /// <summary>The validated UTF-8 text, without a leading byte order mark.</summary>
    public readonly ReadOnlySpan<byte> Utf8Text => _utf8;

    /// <summary>The encoding the input arrived in.</summary>
    public YamlEncoding Encoding { get; }

    /// <summary>Whether the input began with a byte order mark.</summary>
    public bool HasByteOrderMark { get; }

    /// <summary>Detects the encoding by byte order mark or by the null-byte pattern of the first characters.</summary>
    /// <param name="bytes">The raw input.</param>
    /// <param name="markLength">The length of the byte order mark, 0 when there is none.</param>
    /// <returns>The encoding.</returns>
    public static YamlEncoding Detect(ReadOnlySpan<byte> bytes, out int markLength)
    {
        markLength = 0;
        int n = bytes.Length;
        if (n >= 2)
        {
            byte b0 = bytes[0];
            byte b1 = bytes[1];
            if (n >= 4 && b0 == 0 && b1 == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                markLength = 4;
                return YamlEncoding.Utf32BigEndian;
            }

            if (n >= 4 && b0 == 0xFF && b1 == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            {
                markLength = 4;
                return YamlEncoding.Utf32LittleEndian;
            }

            if (b0 == 0xFE && b1 == 0xFF)
            {
                markLength = 2;
                return YamlEncoding.Utf16BigEndian;
            }

            if (b0 == 0xFF && b1 == 0xFE)
            {
                markLength = 2;
                return YamlEncoding.Utf16LittleEndian;
            }

            if (n >= 3 && b0 == 0xEF && b1 == 0xBB && bytes[2] == 0xBF)
            {
                markLength = 3;
                return YamlEncoding.Utf8;
            }

            // No mark: the first character is ASCII, so the null bytes show the width.
            if (n >= 4 && b0 == 0 && b1 == 0 && bytes[2] == 0 && bytes[3] != 0)
            {
                return YamlEncoding.Utf32BigEndian;
            }

            if (n >= 4 && b0 != 0 && b1 == 0 && bytes[2] == 0 && bytes[3] == 0)
            {
                return YamlEncoding.Utf32LittleEndian;
            }

            if (b0 == 0 && b1 != 0)
            {
                return YamlEncoding.Utf16BigEndian;
            }

            if (b0 != 0 && b1 == 0)
            {
                return YamlEncoding.Utf16LittleEndian;
            }
        }

        return YamlEncoding.Utf8;
    }

    /// <summary>Normalises raw bytes (UTF-8, UTF-16 or UTF-32, detected) to validated UTF-8.</summary>
    /// <param name="bytes">The raw input.</param>
    /// <param name="limits">The limits to enforce on the input size.</param>
    /// <returns>The normalised input. Dispose it when the text is no longer used.</returns>
    /// <exception cref="YamlException">The input is malformed or too large.</exception>
    public static YamlInput FromBytes(ReadOnlySpan<byte> bytes, YamlLimits limits)
    {
        if (bytes.Length > limits.MaxInputBytes)
        {
            throw new YamlException("The input is larger than the configured limit.", YamlErrorCategory.Limit, 0, 0, 0);
        }

        YamlEncoding encoding = Detect(bytes, out int mark);
        if (encoding == YamlEncoding.Utf8)
        {
            ReadOnlySpan<byte> text = bytes[mark..];
            Validate(text);
            return new YamlInput(text, null, YamlEncoding.Utf8, mark != 0);
        }

        return Transcode(bytes, encoding, mark, limits);
    }

    /// <summary>Converts a string to validated UTF-8 in a pooled buffer.</summary>
    /// <param name="text">The text.</param>
    /// <param name="limits">The limits to enforce on the input size.</param>
    /// <returns>The normalised input. Dispose it when the text is no longer used.</returns>
    /// <exception cref="YamlException">The text has an unpaired surrogate or is too large.</exception>
    public static YamlInput FromChars(ReadOnlySpan<char> text, YamlLimits limits)
    {
        int max = checked((text.Length * 3) + 3);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            OperationStatus status = Utf8.FromUtf16(text, buffer, out int read, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                throw YamlException.At(YamlErrorCategory.Encoding, "The text contains an unpaired surrogate.", text, read);
            }

            if (written > limits.MaxInputBytes)
            {
                throw new YamlException("The input is larger than the configured limit.", YamlErrorCategory.Limit, 0, 0, 0);
            }

            ReadOnlySpan<byte> utf8 = buffer.AsSpan(0, written);
            bool bom = false;
            if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
            {
                // A string that starts with U+FEFF carries a byte order mark; keep it out of the text.
                utf8 = utf8[3..];
                bom = true;
            }

            CheckPrintable(utf8);
            return new YamlInput(utf8, buffer, YamlEncoding.Utf8, bom);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Returns the pooled buffer, if any.</summary>
    public void Dispose()
    {
        byte[]? rented = _rented;
        _rented = null;
        _utf8 = default;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static YamlInput Transcode(ReadOnlySpan<byte> bytes, YamlEncoding encoding, int mark, YamlLimits limits)
    {
        ReadOnlySpan<byte> body = bytes[mark..];
        bool wide = encoding is YamlEncoding.Utf32BigEndian or YamlEncoding.Utf32LittleEndian;
        bool bigEndian = encoding is YamlEncoding.Utf16BigEndian or YamlEncoding.Utf32BigEndian;
        Encoding decoder = wide
            ? new UTF32Encoding(bigEndian, false, true)
            : new UnicodeEncoding(bigEndian, false, true);

        char[]? chars = null;
        byte[]? utf8 = null;
        try
        {
            int charCount;
            try
            {
                charCount = decoder.GetCharCount(body);
                chars = ArrayPool<char>.Shared.Rent(Math.Max(charCount, 1));
                decoder.GetChars(body, chars);
            }
            catch (DecoderFallbackException ex)
            {
                throw EncodingError(body, mark, bigEndian, wide, ex.Index);
            }

            utf8 = ArrayPool<byte>.Shared.Rent(checked((charCount * 3) + 3));
            OperationStatus status = Utf8.FromUtf16(chars.AsSpan(0, charCount), utf8, out int read, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                throw EncodingError(body, mark, bigEndian, wide, read * (wide ? 4 : 2));
            }

            if (written > limits.MaxInputBytes)
            {
                throw new YamlException("The input is larger than the configured limit.", YamlErrorCategory.Limit, 0, 0, 0);
            }

            CheckPrintable(utf8.AsSpan(0, written));
            byte[] result = utf8;
            utf8 = null;
            return new YamlInput(result.AsSpan(0, written), result, encoding, mark != 0);
        }
        finally
        {
            if (chars is not null)
            {
                ArrayPool<char>.Shared.Return(chars);
            }

            if (utf8 is not null)
            {
                ArrayPool<byte>.Shared.Return(utf8);
            }
        }
    }

    private static YamlException EncodingError(ReadOnlySpan<byte> body, int mark, bool bigEndian, bool wide, int index)
    {
        // Work out the position from the characters that did decode.
        int unit = wide ? 4 : 2;
        int good = Math.Min(index, body.Length) / unit * unit;
        Encoding lenient = wide ? new UTF32Encoding(bigEndian, false, false) : new UnicodeEncoding(bigEndian, false, false);
        string prefix = lenient.GetString(body[..good]);
        YamlException at = YamlException.At(YamlErrorCategory.Encoding, wide ? "Malformed UTF-32 input." : "Malformed UTF-16 input.", prefix, prefix.Length);
        return new YamlException(at.Message, YamlErrorCategory.Encoding, at.Line, at.Column, mark + index);
    }

    private static void Validate(ReadOnlySpan<byte> text)
    {
        if (!Utf8.IsValid(text))
        {
            int bad = FindInvalidUtf8(text);
            throw YamlException.At(YamlErrorCategory.Encoding, "The input is not valid UTF-8.", text, bad);
        }

        CheckPrintable(text);
    }

    private static int FindInvalidUtf8(ReadOnlySpan<byte> text)
    {
        int i = 0;
        while (i < text.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(text[i..], out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                return i;
            }

            i += consumed;
        }

        return text.Length;
    }

    // YAML allows tab, line feed, carriage return, and the printable characters only.
    private static void CheckPrintable(ReadOnlySpan<byte> text)
    {
        int from = 0;
        while (true)
        {
            int rel = text[from..].IndexOfAny(s_suspect);
            if (rel < 0)
            {
                return;
            }

            int i = from + rel;
            byte b = text[i];
            bool bad;
            if (b == 0xC2)
            {
                // U+0080..U+009F except U+0085.
                bad = i + 1 < text.Length && text[i + 1] is >= 0x80 and <= 0x9F and not 0x85;
            }
            else if (b == 0xEF)
            {
                // U+FFFE and U+FFFF.
                bad = i + 2 < text.Length && text[i + 1] == 0xBF && text[i + 2] is 0xBE or 0xBF;
            }
            else
            {
                bad = true;
            }

            if (bad)
            {
                throw YamlException.At(YamlErrorCategory.Syntax, "The input contains a character YAML does not allow.", text, i);
            }

            from = i + 1;
        }
    }
}
