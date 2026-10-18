using System;
using System.Buffers;
using System.IO;
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
    // The C0 control characters other than tab, line feed and carriage return are not allowed
    // anywhere, quoted scalars included. DEL, the C1 controls, U+FFFE, U+FFFF and the byte order
    // mark depend on where they stand (quoted scalars may hold them), so the reader checks those.
    private static readonly SearchValues<byte> s_control = SearchValues.Create(
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0B, 0x0C, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
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
            throw new YamlException($"The input is larger than the limit MaxInputBytes ({limits.MaxInputBytes}).", YamlErrorCategory.Limit, 0, 0, 0);
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
        // A string that starts with U+FEFF carries a byte order mark; keep it out of the text so
        // that positions match the ones reported for the same text as bytes.
        bool bom = false;
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
            bom = true;
        }

        // Size the buffer from the text itself and compare with the limit before renting it, so a
        // huge string cannot make the request overflow or allocate more than the limit allows.
        long size = CountUtf8(text);
        if (size > limits.MaxInputBytes)
        {
            throw new YamlException($"The input is larger than the limit MaxInputBytes ({limits.MaxInputBytes}).", YamlErrorCategory.Limit, 0, 0, 0);
        }

        // No limit setting can make an array larger than the runtime allows.
        if (size > Array.MaxLength)
        {
            throw TooLarge(limits);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(size, 1));
        try
        {
            OperationStatus status = Utf8.FromUtf16(text, buffer, out int read, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                throw YamlException.Located(YamlErrorCategory.Encoding, "The text contains an unpaired surrogate.", text, read);
            }

            ReadOnlySpan<byte> utf8 = buffer.AsSpan(0, written);
            CheckControls(utf8);
            return new YamlInput(utf8, buffer, YamlEncoding.Utf8, bom);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    // How many characters are asked of a reader at a time.
    private const int ReaderChunk = 4096;

    /// <summary>
    /// Reads a text reader to its end as validated UTF-8. When the limits set an input size, the text is converted chunk
    /// by chunk and counted as it goes, so a reader that is far larger than the limit, or never ends, is refused after
    /// one chunk past the limit instead of being buffered first. With no input size limit the reader is read in one call.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="limits">The limits to enforce on the input size.</param>
    /// <returns>The normalised input. Dispose it when the text is no longer used.</returns>
    /// <exception cref="YamlException">The text has an unpaired surrogate or is too large.</exception>
    public static YamlInput FromReader(TextReader reader, YamlLimits limits)
    {
        long max = limits.MaxInputBytes;
        // A string reader already holds its whole text. Read from the start, it hands that string over without a copy;
        // read part way, it copies the rest, which is never larger than the string the caller holds. FromChars then
        // checks the size before it rents anything.
        if (max == long.MaxValue || reader.GetType() == typeof(StringReader))
        {
            return FromChars(reader.ReadToEnd(), limits);
        }

        long cap = GrowthCap(max);
        byte[]? bytes = Array.Empty<byte>();
        try
        {
            Span<char> chunk = stackalloc char[ReaderChunk + 1];
            int carry = 0;
            int written = 0;
            long counted = 0; // bytes seen after the text stopped being written
            bool counting = false;
            bool tooLarge = false;
            bool first = true;
            bool bom = false;
            YamlException? encodingError = null;

            while (true)
            {
                int n = reader.Read(chunk.Slice(carry, ReaderChunk));
                if (n <= 0)
                {
                    break;
                }

                Span<char> text = chunk[..(carry + n)];
                carry = 0;
                if (first)
                {
                    first = false;
                    if (text[0] == '\uFEFF')
                    {
                        // Keep the mark out of the text so positions match the ones reported for the same text as bytes.
                        bom = true;
                        text = text[1..];
                    }
                }

                if (!counting)
                {
                    // What this chunk needs, exactly (a lone surrogate counts as 3 here, which is at least what it would take).
                    long need = written + (long)System.Text.Encoding.UTF8.GetByteCount(text);
                    if (need > max)
                    {
                        // Over the limit: refuse before renting a buffer that will never be used.
                        throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
                    }

                    if (need > Array.MaxLength)
                    {
                        counting = true;
                        tooLarge = true;
                    }
                    else if (need > bytes.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(need, bytes.Length * 2L), Math.Max(cap, need)));
                        Buffer.BlockCopy(bytes, 0, bigger, 0, written);
                        if (bytes.Length > 0)
                        {
                            ArrayPool<byte>.Shared.Return(bytes);
                        }

                        bytes = bigger;
                    }
                }

                if (!counting)
                {
                    OperationStatus status = Utf8.FromUtf16(text, bytes.AsSpan(written), out int read, out int w, replaceInvalidSequences: false, isFinalBlock: false);
                    written += w;
                    if (status == OperationStatus.InvalidData)
                    {
                        encodingError = YamlException.LocatedAtEnd(YamlErrorCategory.Encoding, "The text contains an unpaired surrogate.", bytes.AsSpan(0, written));
                        counting = true;
                        counted += CountPiece(text[read..], chunk, ref carry);
                    }
                    else if (status == OperationStatus.NeedMoreData)
                    {
                        // A high surrogate at the end of the chunk waits for its partner.
                        chunk[0] = text[read];
                        carry = 1;
                    }
                }
                else
                {
                    counted += CountPiece(text, chunk, ref carry);
                }

                if (written + counted > max)
                {
                    throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
                }
            }

            if (carry > 0)
            {
                // The text ends in a high surrogate.
                if (!counting)
                {
                    encodingError = YamlException.LocatedAtEnd(YamlErrorCategory.Encoding, "The text contains an unpaired surrogate.", bytes.AsSpan(0, written));
                }

                counted += 3;
                if (written + counted > max)
                {
                    throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
                }
            }

            if (tooLarge || written + counted > Array.MaxLength)
            {
                throw TooLarge(limits);
            }

            if (encodingError is not null)
            {
                throw encodingError;
            }

            ReadOnlySpan<byte> utf8 = bytes.AsSpan(0, written);
            CheckControls(utf8);
            byte[]? owned = bytes.Length > 0 ? bytes : null;
            bytes = null;
            return new YamlInput(utf8, owned, YamlEncoding.Utf8, bom);
        }
        finally
        {
            if (bytes is { Length: > 0 })
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }

    // The buffer never needs to hold more than the limit plus what one chunk can add before the limit trips. The sum
    // saturates: a limit close to long.MaxValue must not wrap around to a small cap.
    internal static long GrowthCap(long max)
        => max >= Array.MaxLength - (3L * ReaderChunk) ? Array.MaxLength : max + (3L * ReaderChunk);

    // Counts a piece of text in UTF-8 bytes. A high surrogate at its end is left for the next piece, so a pair is one
    // character; the caller's chunk holds it in the first place.
    private static long CountPiece(ReadOnlySpan<char> text, Span<char> chunk, ref int carry)
    {
        if (!text.IsEmpty && char.IsHighSurrogate(text[^1]))
        {
            long size = System.Text.Encoding.UTF8.GetByteCount(text[..^1]);
            chunk[0] = text[^1];
            carry = 1;
            return size;
        }

        return System.Text.Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>
    /// Reads a text reader to its end as a string, exactly as given (a byte order mark, unpaired surrogates and control
    /// characters included), but stops with the limit error as soon as the text is over the input size limit. The size
    /// is counted in UTF-8 bytes without the leading byte order mark. Checking the text itself is left to the caller.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="limits">The limits to enforce on the input size.</param>
    /// <returns>The text.</returns>
    /// <exception cref="YamlException">The text is over the input size limit.</exception>
    public static string ReadToString(TextReader reader, YamlLimits limits)
    {
        long max = limits.MaxInputBytes;
        if (max == long.MaxValue)
        {
            return reader.ReadToEnd();
        }

        if (reader.GetType() == typeof(StringReader))
        {
            // A string reader hands over the string it already holds without a copy; it is counted like any other text.
            string whole = reader.ReadToEnd();
            ReadOnlySpan<char> counted = whole;
            if (counted.Length > 0 && counted[0] == '\uFEFF')
            {
                counted = counted[1..];
            }

            if (CountUtf8(counted) > max)
            {
                throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
            }

            return whole;
        }

        // A character is at least one byte, so the text never needs more characters than the limit plus one chunk.
        long cap = max >= Array.MaxLength - ReaderChunk ? Array.MaxLength : max + ReaderChunk;
        char[] buffer = ArrayPool<char>.Shared.Rent(ReaderChunk * 2);
        try
        {
            int length = 0;
            int counted = 0;
            long bytes = 0;
            while (true)
            {
                if (buffer.Length - length < ReaderChunk)
                {
                    long wanted = Math.Min(Math.Max(buffer.Length * 2L, length + (long)ReaderChunk), Math.Max(cap, length + (long)ReaderChunk));
                    if (wanted > Array.MaxLength)
                    {
                        throw TooLarge(limits);
                    }

                    char[] bigger = ArrayPool<char>.Shared.Rent((int)wanted);
                    Array.Copy(buffer, bigger, length);
                    ArrayPool<char>.Shared.Return(buffer);
                    buffer = bigger;
                }

                int n = reader.Read(buffer, length, ReaderChunk);
                if (n <= 0)
                {
                    break;
                }

                length += n;

                // A high surrogate at the end waits for its partner, so a pair is counted once, as 4 bytes.
                int end = char.IsHighSurrogate(buffer[length - 1]) ? length - 1 : length;
                if (end > counted)
                {
                    int from = counted;
                    if (from == 0 && buffer[0] == '\uFEFF')
                    {
                        from = 1; // a leading byte order mark is not part of the size
                    }

                    bytes += System.Text.Encoding.UTF8.GetByteCount(buffer.AsSpan(from, end - from));
                    counted = end;
                }

                if (bytes > max)
                {
                    throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
                }
            }

            if (counted < length && bytes + 3 > max)
            {
                throw new YamlException($"The input is larger than the limit MaxInputBytes ({max}).", YamlErrorCategory.Limit, 0, 0, 0);
            }

            return new string(buffer, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
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

    // The UTF-8 length of text, counting an unpaired surrogate as the three bytes of its
    // replacement character (an upper bound for it). The sum is a long and never overflows.
    internal static long CountUtf8(ReadOnlySpan<char> text)
    {
        const int Chunk = 1 << 24;
        if (text.Length <= Chunk)
        {
            return System.Text.Encoding.UTF8.GetByteCount(text);
        }

        long total = 0;
        while (!text.IsEmpty)
        {
            int n = Math.Min(text.Length, Chunk);
            if (n < text.Length && char.IsHighSurrogate(text[n - 1]))
            {
                // Keep a surrogate pair together.
                n--;
            }

            total += System.Text.Encoding.UTF8.GetByteCount(text[..n]);
            text = text[n..];
        }

        return total;
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

            if (mark == 0 && charCount > 0 && chars[0] >= 0x80)
            {
                // Without a mark the width is told from the null bytes of an ASCII first character, so a
                // stream may not begin with anything else (YAML 1.2.2, 5.2). Writing such a stream back would
                // also lose the way it was recognised.
                throw YamlException.Located(YamlErrorCategory.Encoding, "A UTF-16 or UTF-32 input without a byte order mark must begin with an ASCII character.", body, 0);
            }

            long size = CountUtf8(chars.AsSpan(0, charCount));
            if (size > limits.MaxInputBytes)
            {
                throw new YamlException($"The input is larger than the limit MaxInputBytes ({limits.MaxInputBytes}).", YamlErrorCategory.Limit, 0, 0, 0);
            }

            // No limit setting can make an array larger than the runtime allows.
            if (size > Array.MaxLength)
            {
                throw TooLarge(limits);
            }

            utf8 = ArrayPool<byte>.Shared.Rent((int)Math.Max(size, 1));
            OperationStatus status = Utf8.FromUtf16(chars.AsSpan(0, charCount), utf8, out int read, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                throw EncodingError(body, mark, bigEndian, wide, read * (wide ? 4 : 2));
            }

            CheckControls(utf8.AsSpan(0, written));
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
        // Find the first bad unit ourselves: the decoder's own index may point past it (it reports where
        // it gave up, which for a lone surrogate is the unit after), and the column must name the bad character.
        index = FirstBadUnit(body, bigEndian, wide);
        int unit = wide ? 4 : 2;
        int good = Math.Clamp(index, 0, body.Length) / unit * unit;
        Encoding lenient = wide ? new UTF32Encoding(bigEndian, false, false) : new UnicodeEncoding(bigEndian, false, false);
        string prefix = lenient.GetString(body[..good]);
        YamlException at = YamlException.Located(YamlErrorCategory.Encoding, wide ? "Malformed UTF-32 input." : "Malformed UTF-16 input.", prefix, prefix.Length);
        return new YamlException(at.Message, YamlErrorCategory.Encoding, at.Line, at.Column, mark + index);
    }

    // The byte index of the first unit that is not a valid character: a lone surrogate, a UTF-32 value out of
    // range, or a partial unit at the end. The body is at most the input size limit, so an int index is enough.
    private static int FirstBadUnit(ReadOnlySpan<byte> body, bool bigEndian, bool wide)
    {
        int unit = wide ? 4 : 2;
        int i = 0;
        while (i + unit <= body.Length)
        {
            if (wide)
            {
                uint v = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body[i..])
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body[i..]);
                if (v > 0x10FFFF || (v >= 0xD800 && v <= 0xDFFF))
                {
                    return i;
                }

                i += 4;
                continue;
            }

            ushort u = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body[i..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body[i..]);
            if (u < 0xD800 || u > 0xDFFF)
            {
                i += 2;
                continue;
            }

            if (u >= 0xDC00 || i + 4 > body.Length)
            {
                return i;
            }

            ushort low = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body[(i + 2)..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body[(i + 2)..]);
            if (low < 0xDC00 || low > 0xDFFF)
            {
                return i;
            }

            i += 4;
        }

        return i;
    }

    private static void Validate(ReadOnlySpan<byte> text)
    {
        if (!Utf8.IsValid(text))
        {
            int bad = FindInvalidUtf8(text);
            throw YamlException.Located(YamlErrorCategory.Encoding, "The input is not valid UTF-8.", text, bad);
        }

        CheckControls(text);
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

    private static void CheckControls(ReadOnlySpan<byte> text)
    {
        int i = text.IndexOfAny(s_control);
        if (i >= 0)
        {
            throw YamlException.Located(YamlErrorCategory.Syntax, "The input contains a control character YAML does not allow.", text, i);
        }
    }

    private static YamlException TooLarge(YamlLimits limits)
        => new($"The input is larger than the largest buffer the runtime can hold (limit MaxInputBytes is {limits.MaxInputBytes}).", YamlErrorCategory.Limit, 0, 0, 0);
}
