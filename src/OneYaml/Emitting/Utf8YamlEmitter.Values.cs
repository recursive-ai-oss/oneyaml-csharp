using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Unicode;
using OneYaml.Emitting;

namespace OneYaml;

public sealed partial class Utf8YamlEmitter
{
    // A key whose UTF-8 form is at most this long cannot pass 1,024 UTF-16 code units once quoted and escaped
    // (a control byte becomes four characters), so it needs no measuring.
    private const int ShortKeyBytes = 250;

    // Text up to a third of this many characters is turned into UTF-8 in a buffer the emitter keeps.
    private const int SmallBufferBytes = 2048;

    // ---- property names ----

    /// <summary>Writes the key of a mapping entry. The value follows as the next call.</summary>
    /// <param name="name">The key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="YamlException">No mapping is open, the previous key has no value yet, or the text has an unpaired surrogate.</exception>
    public void WritePropertyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        WritePropertyName(name.AsSpan());
    }

    /// <summary>Writes the key of a mapping entry. The value follows as the next call.</summary>
    /// <param name="name">The key.</param>
    /// <exception cref="YamlException">No mapping is open, the previous key has no value yet, or the text has an unpaired surrogate.</exception>
    public void WritePropertyName(ReadOnlySpan<char> name)
    {
        if (ScalarText.IsSimplePlain(name, out bool spaced) && !spaced)
        {
            WriteSimpleKey(name);
            return;
        }

        byte[]? rented = null;
        try
        {
            WriteKey(Transcode(name, ref rented));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Writes the key of a mapping entry from UTF-8 bytes. The value follows as the next call.</summary>
    /// <param name="utf8Name">The key, valid UTF-8.</param>
    /// <exception cref="YamlException">No mapping is open, the previous key has no value yet, or the bytes are not valid UTF-8.</exception>
    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        if (!Utf8.IsValid(utf8Name))
        {
            throw Misuse("A property name must be valid UTF-8.");
        }

        WriteKey(utf8Name);
    }

    // The bytes of a key as they are written, colon included, for keys that are written many times.
    // Null when the key is long enough to need the measured path.
    internal static byte[]? PrepareKey(ReadOnlySpan<byte> utf8Name)
    {
        if (utf8Name.Length > ShortKeyBytes)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(utf8Name.Length + 8);
        var writer = new Utf8YamlWriter(buffer);
        writer.WriteString(utf8Name, ScalarWriteStyle.Plain, EmitContext.BlockKey, StyleMode.Dump);
        writer.WriteByte((byte)':');
        return buffer.WrittenSpan.ToArray();
    }

    // The keys of a generic tree repeat: the same few names open thousands of mappings. Their written form (text and
    // colon) is kept in a small table keyed by the string, so a repeated key costs a lookup and a copy, with no
    // transcoding and no style analysis. The table is dropped when the call ends.
    private struct KeyEntry
    {
        public string? Key;
        public int Offset;
        public int Length;
    }

    private const int KeyCacheSize = 256;
    private const int KeyCacheMaxChars = 100;
    private const int KeyArenaLimit = 1 << 16;

    private KeyEntry[]? _keyCache;
    private byte[]? _keyArena;
    private int _keyArenaUsed;
    private bool _usedKeyCache;

    /// <summary>Writes a mapping key from a string; the same as <see cref="WritePropertyName(string)"/>, faster for names that come up again.</summary>
    internal void WriteStringKey(string key)
    {
        if (key.Length is 0 or > KeyCacheMaxChars)
        {
            WritePropertyName(key);
            return;
        }

        KeyEntry[] cache = _keyCache ??= new KeyEntry[KeyCacheSize];
        int slot = (key.Length * 31 + key[0] * 7 + key[^1] * 3 + key[key.Length >> 1]) & (KeyCacheSize - 1);
        ref KeyEntry entry = ref cache[slot];
        if (entry.Key is not { } cached || !(ReferenceEquals(cached, key) || cached == key))
        {
            // First sight: note the key and write it the general way. A map of all different keys then pays for the note only.
            entry.Key = key;
            entry.Length = 0;
            _usedKeyCache = true;
            WritePropertyName(key);
            return;
        }

        if (entry.Length == 0 && !TryRememberKey(key, ref entry))
        {
            // A key that needs quotes or is too long to be implicit stays on the general path.
            entry.Length = -1;
        }

        if (entry.Length < 0)
        {
            WritePropertyName(key);
            return;
        }

        ref Frame f = ref KeyFrame();
        Lead lead = StartEntryLead(ref f);
        ReadOnlySpan<byte> bytes = _keyArena.AsSpan(entry.Offset, entry.Length);
        Span<byte> dst = OpenLead(lead, bytes.Length);
        bytes.CopyTo(dst);
        Advance(lead.Length + bytes.Length);
        f.KeyPending = true;
    }

    // Stores the plain form of a key (text and colon) in the arena; false when the key is not written plain.
    private bool TryRememberKey(string key, ref KeyEntry entry)
    {
        byte[]? rented = null;
        try
        {
            ReadOnlySpan<byte> utf8 = Transcode(key.AsSpan(), ref rented);
            if (Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockKey, StyleMode.Dump, validUtf8: true) != ScalarWriteStyle.Plain)
            {
                return false;
            }

            int need = utf8.Length + 1;
            byte[] arena = _keyArena ??= new byte[4096];
            if (_keyArenaUsed + need > arena.Length)
            {
                if (_keyArenaUsed + need > KeyArenaLimit)
                {
                    // Many different keys: start over, so the table stays small.
                    Array.Clear(_keyCache!);
                    _keyArenaUsed = 0;
                }

                if (_keyArenaUsed + need > arena.Length)
                {
                    Array.Resize(ref arena, Math.Max(arena.Length * 2, _keyArenaUsed + need));
                    _keyArena = arena;
                }
            }

            utf8.CopyTo(arena.AsSpan(_keyArenaUsed));
            arena[_keyArenaUsed + utf8.Length] = (byte)':';
            entry.Key = key;
            entry.Offset = _keyArenaUsed;
            entry.Length = need;
            _keyArenaUsed += need;
            _usedKeyCache = true;
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal void WritePreparedKey(byte[] prepared)
    {
        ref Frame f = ref KeyFrame();
        Lead lead = StartEntryLead(ref f);
        Span<byte> dst = OpenLead(lead, prepared.Length);
        prepared.CopyTo(dst);
        Advance(lead.Length + prepared.Length);
        f.KeyPending = true;
    }

    private ref Frame KeyFrame()
    {
        if (_depth == 0 || !_frames[_depth - 1].IsMapping || _frames[_depth - 1].KeyPending)
        {
            throw Misuse("A property name is only allowed inside a mapping, before the value of the previous one.");
        }

        return ref _frames[_depth - 1];
    }

    private void WriteKey(ReadOnlySpan<byte> utf8)
    {
        ref Frame f = ref KeyFrame();
        Lead lead = StartEntryLead(ref f);
        ScalarWriteStyle style = Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockKey, StyleMode.Dump, validUtf8: true);
        if (style == ScalarWriteStyle.Plain)
        {
            // The common key: the lead, the text and the colon go out in one buffer request.
            Span<byte> dst = OpenLead(lead, utf8.Length + 1);
            utf8.CopyTo(dst);
            dst[utf8.Length] = (byte)':';
            Advance(lead.Length + utf8.Length + 1);
            f.KeyPending = true;
            return;
        }

        WriteLead(lead);
        if (style == ScalarWriteStyle.ExplicitKeyRequired)
        {
            // An implicit key may take 1,024 UTF-16 code units at most; a longer one is written with "? ", and
            // the colon goes on a line of its own.
            _w.WriteRaw("? "u8);
            _w.WriteExplicitKeyText(utf8);
            _w.WriteByte((byte)'\n');
            _w.WriteSpaces(f.Indent);
        }
        else
        {
            _w.WriteChosen(utf8, style, 0, 2, default);
        }

        _w.WriteByte((byte)':');
        f.KeyPending = true;
    }

    private ArrayBufferWriter<byte> Scratch()
    {
        ArrayBufferWriter<byte> s = _scratch ??= new ArrayBufferWriter<byte>(64);
        s.ResetWrittenCount();
        return s;
    }

    // ---- strings ----

    /// <summary>Writes a string value, or the key when a mapping expects one. <see langword="null"/> is written as <c>null</c>.</summary>
    /// <param name="value">The string.</param>
    /// <exception cref="YamlException">A value is not allowed here, or the string has an unpaired surrogate.</exception>
    public void WriteStringValue(string? value)
    {
        if (value is null)
        {
            WriteNullValue();
            return;
        }

        WriteStringValue(value.AsSpan());
    }

    /// <summary>Writes a string value, or the key when a mapping expects one.</summary>
    /// <param name="value">The characters.</param>
    /// <exception cref="YamlException">A value is not allowed here, or the text has an unpaired surrogate.</exception>
    public void WriteStringValue(ReadOnlySpan<char> value)
    {
        if (ScalarText.IsSimplePlain(value, out bool spaced) && TryWriteSimple(value, spaced))
        {
            return;
        }

        byte[]? rented = null;
        try
        {
            WriteText(Transcode(value, ref rented));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Writes a string value from UTF-8 bytes, or the key when a mapping expects one. Bytes that are not valid UTF-8
    /// cannot be text, so they are written as <c>!!binary</c> data (not allowed as a key).
    /// </summary>
    /// <param name="utf8Value">The bytes.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteStringValue(ReadOnlySpan<byte> utf8Value)
    {
        if (Utf8.IsValid(utf8Value))
        {
            WriteText(utf8Value);
        }
        else
        {
            WriteBinaryValue(utf8Value);
        }
    }

    // Turns characters into UTF-8 in a buffer the emitter keeps for short text and rents for long text.
    private ReadOnlySpan<byte> Transcode(ReadOnlySpan<char> chars, ref byte[]? rented)
    {
        int max = checked(chars.Length * 3);
        Span<byte> buffer;
        if (max <= SmallBufferBytes)
        {
            buffer = _smallBuffer ??= new byte[SmallBufferBytes];
        }
        else
        {
            rented = ArrayPool<byte>.Shared.Rent(max);
            buffer = rented;
        }

        OperationStatus status = Utf8.FromUtf16(chars, buffer, out _, out int written, replaceInvalidSequences: false, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            throw Misuse("The string has an unpaired surrogate, which has no UTF-8 form.");
        }

        return buffer.Slice(0, written);
    }

    private void WriteText(ReadOnlySpan<byte> utf8)
    {
        Place place = BeginScalarLead(out int parentIndent, out Lead lead);
        if (place == Place.MapKey)
        {
            WriteKey(utf8);
            return;
        }

        ScalarWriteStyle style;
        if (place == Place.Root && _embedding == Embedding.None && NeedsIndentationIndicator(utf8))
        {
            // A block scalar at the root is indented from column -1, which the scalar writer's indicator does not count,
            // so text that needs the indicator is quoted instead.
            WriteLead(lead);
            _w.WriteDoubleQuoted(utf8);
            style = ScalarWriteStyle.DoubleQuoted;
        }
        else
        {
            style = Utf8YamlWriter.ChooseStyle(utf8, ScalarWriteStyle.Plain, EmitContext.BlockValue, StyleMode.Dump, parentIndent, parentIndent + _indentSize, validUtf8: true);
            if (style == ScalarWriteStyle.Plain)
            {
                // The common value: the lead, the text and the line break go out in one buffer request.
                Span<byte> dst = OpenLead(lead, utf8.Length + 1);
                utf8.CopyTo(dst);
                dst[utf8.Length] = (byte)'\n';
                Advance(lead.Length + utf8.Length + 1);
                EndScalar(place);
                return;
            }

            if (style == ScalarWriteStyle.SingleQuoted && utf8.IndexOf((byte)'\'') < 0)
            {
                // Quoted without a quote inside: still one buffer request.
                Span<byte> dst = OpenLead(lead, utf8.Length + 3);
                dst[0] = (byte)'\'';
                utf8.CopyTo(dst.Slice(1));
                dst[utf8.Length + 1] = (byte)'\'';
                dst[utf8.Length + 2] = (byte)'\n';
                Advance(lead.Length + utf8.Length + 3);
                EndScalar(place);
                return;
            }

            WriteLead(lead);
            _w.WriteChosen(utf8, style, parentIndent, parentIndent + _indentSize, default);
        }

        if (style < ScalarWriteStyle.Literal)
        {
            // The block styles end with their own line break.
            _w.WriteByte((byte)'\n');
        }

        EndScalar(place);
    }

    // Text that ScalarText.IsSimplePlain accepted is ASCII and plain in every place, so it is narrowed straight into the
    // output with its lead and line end: no transcoding, no analysis, one buffer request.
    private bool TryWriteSimple(ReadOnlySpan<char> value, bool spaced)
    {
        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            // Nothing is changed yet for a key, so text with a space can still take the general path.
            if (spaced)
            {
                return false;
            }

            WriteSimpleKey(value);
            return true;
        }

        Span<byte> dst = OpenLead(lead, value.Length + 1);
        Ascii.FromUtf16(value, dst, out _);
        dst[value.Length] = (byte)'\n';
        Advance(lead.Length + value.Length + 1);
        EndScalar(place);
        return true;
    }

    private void WriteSimpleKey(ReadOnlySpan<char> name)
    {
        ref Frame f = ref KeyFrame();
        Lead lead = StartEntryLead(ref f);
        Span<byte> dst = OpenLead(lead, name.Length + 1);
        Ascii.FromUtf16(name, dst, out _);
        dst[name.Length] = (byte)':';
        Advance(lead.Length + name.Length + 1);
        f.KeyPending = true;
    }

    // A multi-line text whose first line starts with a space: a block scalar would need an indentation indicator.
    private static bool NeedsIndentationIndicator(ReadOnlySpan<byte> utf8) =>
        utf8.Contains((byte)'\n') && utf8.TrimStart((byte)'\n') is { Length: > 0 } rest && rest[0] == (byte)' ';

    // ---- values that are never quoted ----

    /// <summary>Writes <c>null</c>.</summary>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNullValue() => WriteLiteral("null"u8);

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteBooleanValue(bool value) => WriteLiteral(value ? "true"u8 : "false"u8);

    // A value that is a fixed text, in one buffer request with its lead and line break.
    private void WriteLiteral(ReadOnlySpan<byte> text)
    {
        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            Utf8YamlWriter w = new(Scratch());
            w.WriteRaw(text);
            EndValue(place);
            return;
        }

        Span<byte> dst = OpenLead(lead, text.Length + 1);
        text.CopyTo(dst);
        dst[text.Length] = (byte)'\n';
        Advance(lead.Length + text.Length + 1);
        EndScalar(place);
    }

    /// <summary>Writes an integer.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(int value) => WriteNumberValue((long)value);

    /// <summary>Writes an integer.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(long value)
    {
        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            Utf8YamlWriter w = new(Scratch());
            w.WriteInt64(value);
            EndValue(place);
            return;
        }

        // Twenty digits and a sign at most, then the line break.
        Span<byte> dst = OpenLead(lead, 22);
        value.TryFormat(dst, out int written, default, CultureInfo.InvariantCulture);
        dst[written] = (byte)'\n';
        Advance(lead.Length + written + 1);
        EndScalar(place);
    }

    /// <summary>Writes an unsigned integer.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(ulong value)
    {
        Utf8YamlWriter w = ValueWriter(out Place place);
        w.WriteUInt64(value);
        EndValue(place);
    }

    /// <summary>Writes an integer of any size, exactly.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(BigInteger value)
    {
        Utf8YamlWriter w = ValueWriter(out Place place);
        w.WriteBigInteger(value);
        EndValue(place);
    }

    /// <summary>
    /// Writes a floating-point number in its shortest form that reads back as the same number, always with a point or an
    /// exponent so it is never an integer. Not-a-number and the infinities are written as <c>.nan</c>, <c>.inf</c> and <c>-.inf</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(double value)
    {
        Place place = BeginScalarLead(out _, out Lead lead);
        if (place == Place.MapKey)
        {
            Utf8YamlWriter w = new(Scratch());
            w.WriteDouble(value);
            EndValue(place);
            return;
        }

        Span<byte> dst = OpenLead(lead, 42);
        int written = Utf8YamlWriter.FormatDouble(value, dst);
        dst[written] = (byte)'\n';
        Advance(lead.Length + written + 1);
        EndScalar(place);
    }

    /// <summary>Writes a single-precision number the way <see cref="WriteNumberValue(double)"/> writes a double.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(float value)
    {
        Utf8YamlWriter w = ValueWriter(out Place place);
        w.WriteSingle(value);
        EndValue(place);
    }

    /// <summary>Writes a decimal number, exactly.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="YamlException">A value is not allowed here.</exception>
    public void WriteNumberValue(decimal value)
    {
        Span<byte> text = stackalloc byte[48];
        value.TryFormat(text, out int written, default, CultureInfo.InvariantCulture);
        Utf8YamlWriter w = ValueWriter(out Place place);
        w.WriteRaw(text.Slice(0, written));
        EndValue(place);
    }

    // Writes text that is already a valid plain number, such as the digits of an enum value outside the declared names.
    internal void WritePlainNumberText(ReadOnlySpan<char> ascii)
    {
        Span<byte> bytes = stackalloc byte[48];
        if (ascii.Length > bytes.Length || Encoding.UTF8.GetBytes(ascii, bytes) != ascii.Length)
        {
            throw Misuse("The number is too long to write.");
        }

        Utf8YamlWriter w = ValueWriter(out Place place);
        w.WriteRaw(bytes.Slice(0, ascii.Length));
        EndValue(place);
    }

    /// <summary>Writes binary data as Base64 with the <c>!!binary</c> tag. It cannot be a mapping key.</summary>
    /// <param name="value">The bytes.</param>
    /// <exception cref="YamlException">A value is not allowed here, or a mapping expects a key.</exception>
    public void WriteBinaryValue(ReadOnlySpan<byte> value)
    {
        Place place = BeginScalar(out _);
        if (place == Place.MapKey)
        {
            throw Misuse("Binary data cannot be a mapping key.");
        }

        _w.WriteRaw("!!binary "u8);
        if (value.IsEmpty)
        {
            // An empty plain scalar would be null, not empty data.
            _w.WriteRaw("''\n"u8);
            EndScalar(place);
            return;
        }

        int max = Base64.GetMaxEncodedToUtf8Length(value.Length);
        Span<byte> dst = GetSpan(max);
        Base64.EncodeToUtf8(value, dst, out _, out int written);
        Advance(written);
        _w.WriteByte((byte)'\n');
        EndScalar(place);
    }

    // The writer a plain scalar goes to: the output, or a scratch buffer when a mapping expects a key.
    private Utf8YamlWriter ValueWriter(out Place place)
    {
        place = BeginScalar(out _);
        return place == Place.MapKey ? new Utf8YamlWriter(Scratch()) : _w;
    }

    private void EndValue(Place place)
    {
        if (place == Place.MapKey)
        {
            ref Frame f = ref KeyFrame();
            Lead lead = StartEntryLead(ref f);
            ReadOnlySpan<byte> key = _scratch!.WrittenSpan;
            Span<byte> dst = OpenLead(lead, key.Length + 1);
            key.CopyTo(dst);
            dst[key.Length] = (byte)':';
            Advance(lead.Length + key.Length + 1);
            f.KeyPending = true;
            return;
        }

        _w.WriteByte((byte)'\n');
        EndScalar(place);
    }
}
