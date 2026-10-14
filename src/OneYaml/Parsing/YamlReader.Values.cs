using System;
using System.Buffers;
using System.Text;

namespace OneYaml.Parsing;

internal ref partial struct YamlReader
{
    /// <summary>An upper bound on the decoded byte length of the current scalar.</summary>
    public readonly int MaxDecodedLength => ScalarDecoder.MaxDecodedLength(_contentEnd - _contentStart);

    /// <summary>Decodes the current scalar's value as UTF-8 into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="MaxDecodedLength"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public readonly int DecodeScalar(Span<byte> destination)
    {
        if (!_escapedOrFolded)
        {
            RawScalar.CopyTo(destination);
            return _contentEnd - _contentStart;
        }

        return ScalarDecoder.Decode(RawScalar, ScalarStyle, _blockIndent, _chomping, destination);
    }

    /// <summary>Writes the current scalar's value as UTF-8 to a buffer writer.</summary>
    /// <param name="writer">The destination.</param>
    public readonly void CopyScalar(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!_escapedOrFolded)
        {
            writer.Write(RawScalar);
            return;
        }

        Span<byte> span = writer.GetSpan(MaxDecodedLength);
        writer.Advance(DecodeScalar(span));
    }

    /// <summary>Returns the current scalar's value as a string.</summary>
    /// <returns>The value.</returns>
    public readonly string GetString()
    {
        if (!_escapedOrFolded)
        {
            return _contentEnd == _contentStart ? string.Empty : Encoding.UTF8.GetString(RawScalar);
        }

        int max = MaxDecodedLength;
        if (max <= 512)
        {
            Span<byte> small = stackalloc byte[512];
            return Encoding.UTF8.GetString(small[..DecodeScalar(small)]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = DecodeScalar(rented);
            return Encoding.UTF8.GetString(rented, 0, n);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
