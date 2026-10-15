using System.Text;

namespace OneYaml.Tests.Fuzzing;

/// <summary>
/// Makes variations of YAML inputs by small random edits that tend to hit parser corners:
/// structural characters, odd whitespace, broken UTF-8, truncation and splicing.
/// </summary>
internal sealed class Mutator
{
    private static readonly byte[][] s_snippets =
    [
        "---\n"u8.ToArray(), "...\n"u8.ToArray(), "- "u8.ToArray(), "? "u8.ToArray(), ": "u8.ToArray(), ":"u8.ToArray(),
        "{"u8.ToArray(), "}"u8.ToArray(), "["u8.ToArray(), "]"u8.ToArray(), ","u8.ToArray(), "&a "u8.ToArray(), "*a"u8.ToArray(),
        "!!str "u8.ToArray(), "!<tag:x> "u8.ToArray(), "!e!x "u8.ToArray(), "'"u8.ToArray(), "\""u8.ToArray(), "|"u8.ToArray(),
        ">"u8.ToArray(), "|2-\n"u8.ToArray(), ">+\n"u8.ToArray(), "#"u8.ToArray(), " #c\n"u8.ToArray(), "\t"u8.ToArray(),
        "\r\n"u8.ToArray(), "\r"u8.ToArray(), "\n"u8.ToArray(), "  "u8.ToArray(), "%YAML 1.2\n"u8.ToArray(),
        "%TAG !e! tag:x,1:\n"u8.ToArray(), "\\x41"u8.ToArray(), "\\u00e9"u8.ToArray(), "\\U0001F600"u8.ToArray(), "\\\n"u8.ToArray(),
        "\\q"u8.ToArray(), [0xEF, 0xBB, 0xBF], [0xC2, 0x85], [0xC2, 0x80], [0x7F], [0xEF, 0xBF, 0xBE], [0xE2, 0x80, 0xA8],
        [0xF0, 0x9F, 0x98, 0x80], [0xFF], [0x80], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0x00], [0x07], [0x7F],
        "<<: *a\n"u8.ToArray(), "key: value\n"u8.ToArray(), "- - - - "u8.ToArray(), "[[[[ "u8.ToArray(), "{{{{ "u8.ToArray(),
    ];

    private readonly Random _random;

    public Mutator(int seed)
    {
        _random = new Random(seed);
    }

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public byte[] Mutate(byte[] source, IReadOnlyList<byte[]> pool, int maxLength)
    {
        var data = new List<byte>(source);
        int rounds = 1 + _random.Next(4);
        for (int r = 0; r < rounds; r++)
        {
            ApplyOne(data, pool);
        }

        if (data.Count > maxLength)
        {
            data.RemoveRange(maxLength, data.Count - maxLength);
        }

        return data.ToArray();
    }

    private void ApplyOne(List<byte> data, IReadOnlyList<byte[]> pool)
    {
        int at = data.Count == 0 ? 0 : _random.Next(data.Count + 1);
        switch (_random.Next(10))
        {
            case 0:
                if (data.Count > 0)
                {
                    data[Math.Min(at, data.Count - 1)] ^= (byte)(1 << _random.Next(8));
                }

                break;
            case 1:
                if (data.Count > 0)
                {
                    data[Math.Min(at, data.Count - 1)] = (byte)_random.Next(256);
                }

                break;
            case 2:
            case 3:
            case 4:
                data.InsertRange(at, s_snippets[_random.Next(s_snippets.Length)]);
                break;
            case 5:
                if (data.Count > 0)
                {
                    int length = Math.Min(1 + _random.Next(24), data.Count - Math.Min(at, data.Count - 1));
                    data.RemoveRange(Math.Min(at, data.Count - 1), length);
                }

                break;
            case 6:
                if (data.Count > 0)
                {
                    int from = _random.Next(data.Count);
                    int length = Math.Min(1 + _random.Next(64), data.Count - from);
                    data.InsertRange(at, data.GetRange(from, length));
                }

                break;
            case 7:
                if (data.Count > 0)
                {
                    data.RemoveRange(at == data.Count ? data.Count - 1 : at, data.Count - (at == data.Count ? data.Count - 1 : at));
                }

                break;
            case 8:
                if (pool.Count > 0)
                {
                    byte[] other = pool[_random.Next(pool.Count)];
                    if (other.Length > 0)
                    {
                        int from = _random.Next(other.Length);
                        int length = Math.Min(1 + _random.Next(256), other.Length - from);
                        data.InsertRange(at, other.AsSpan(from, length).ToArray());
                    }
                }

                break;
            default:
                // Indentation changes: add or drop spaces at a line start.
                int line = FindLineStart(data, at);
                if (_random.Next(2) == 0)
                {
                    data.InsertRange(line, new byte[1 + _random.Next(3)].Select(_ => (byte)' '));
                }
                else if (line < data.Count && data[line] == (byte)' ')
                {
                    data.RemoveAt(line);
                }

                break;
        }
    }

    private static int FindLineStart(List<byte> data, int at)
    {
        int i = Math.Min(at, data.Count);
        while (i > 0 && data[i - 1] != (byte)'\n')
        {
            i--;
        }

        return i;
    }

    /// <summary>Re-encodes text in one of the encodings a stream may arrive in.</summary>
    public static byte[]? Transcode(byte[] utf8, int variant)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        Encoding encoding = variant switch
        {
            0 or 1 => new UnicodeEncoding(false, variant == 0),
            2 or 3 => new UnicodeEncoding(true, variant == 2),
            4 or 5 => new UTF32Encoding(false, variant == 4),
            _ => new UTF32Encoding(true, variant == 6),
        };
        byte[] body = encoding.GetBytes(text);
        byte[] mark = encoding.GetPreamble();
        var result = new byte[mark.Length + body.Length];
        mark.CopyTo(result, 0);
        body.CopyTo(result, mark.Length);
        return result;
    }
}
