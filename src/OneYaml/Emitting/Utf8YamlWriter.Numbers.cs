using System;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OneYaml.Emitting;

internal readonly partial struct Utf8YamlWriter
{
    /// <summary>Writes <c>null</c>.</summary>
    public void WriteNull() => WriteRaw("null"u8);

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteBool(bool value) => WriteRaw(value ? "true"u8 : "false"u8);

    /// <summary>Writes an integer in decimal.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value)
    {
        Span<byte> dst = _output.GetSpan(20);
        value.TryFormat(dst, out int written, default, CultureInfo.InvariantCulture);
        _output.Advance(written);
    }

    /// <summary>Writes an unsigned integer in decimal.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value)
    {
        Span<byte> dst = _output.GetSpan(20);
        value.TryFormat(dst, out int written, default, CultureInfo.InvariantCulture);
        _output.Advance(written);
    }

    /// <summary>Writes an integer of any size in decimal, exactly.</summary>
    /// <param name="value">The value.</param>
    public void WriteBigInteger(BigInteger value)
    {
        // A decimal digit needs at most 0.302 bits-worth of the bit length, plus the sign.
        long digits = (long)(value.GetBitLength() * 0.30103) + 3;
        char[]? rented = null;
        Span<char> chars = digits <= 64
            ? stackalloc char[64]
            : (rented = ArrayPool<char>.Shared.Rent((int)Math.Min(digits, int.MaxValue)));
        try
        {
            value.TryFormat(chars, out int written, default, CultureInfo.InvariantCulture);
            Span<byte> dst = _output.GetSpan(written);
            Ascii.FromUtf16(chars.Slice(0, written), dst, out _);
            _output.Advance(written);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Writes a double in its shortest form that reads back as the same number, always with a
    /// <c>.</c> or an exponent so it is never an integer: <c>1.0</c>, <c>1E+16</c>, <c>.inf</c>,
    /// <c>-.inf</c>, <c>.nan</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value)
    {
        if (double.IsNaN(value))
        {
            WriteRaw(".nan"u8);
        }
        else if (double.IsInfinity(value))
        {
            WriteRaw(value > 0 ? ".inf"u8 : "-.inf"u8);
        }
        else
        {
            Span<byte> dst = _output.GetSpan(40);
            value.TryFormat(dst, out int written, default, CultureInfo.InvariantCulture);
            _output.Advance(AddPointIfNeeded(dst, written));
        }
    }

    /// <summary>Writes a single-precision float the way <see cref="WriteDouble"/> writes a double.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value)
    {
        if (float.IsNaN(value))
        {
            WriteRaw(".nan"u8);
        }
        else if (float.IsInfinity(value))
        {
            WriteRaw(value > 0 ? ".inf"u8 : "-.inf"u8);
        }
        else
        {
            Span<byte> dst = _output.GetSpan(40);
            value.TryFormat(dst, out int written, default, CultureInfo.InvariantCulture);
            _output.Advance(AddPointIfNeeded(dst, written));
        }
    }

    private static int AddPointIfNeeded(Span<byte> dst, int written)
    {
        // "1", "-0" and "100" would read back as integers; a float always shows a point or an exponent.
        if (dst.Slice(0, written).IndexOfAny((byte)'.', (byte)'E') >= 0)
        {
            return written;
        }

        dst[written] = (byte)'.';
        dst[written + 1] = (byte)'0';
        return written + 2;
    }
}
