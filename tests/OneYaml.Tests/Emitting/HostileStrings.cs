using System.Text;

namespace OneYaml.Tests.Emitting;

/// <summary>Deterministic generator of awkward strings: every character class a scalar writer must survive.</summary>
internal sealed class HostileStrings
{
    private static readonly string[] Fragments =
    [
        "yes", "No", "ON", "off", "y", "N", "true", "False", "null", "Null", "~", "<<", "=", "0755", "0o17", "0x1F", "0b101", "1_000",
        "1:30", "190:20:30", "12", "-7", "+3", "1.5", ".5", "1e3", ".inf", "-.INF", ".NaN", "2001-12-14", "2001-12-14 21:59:43.10 -5",
        "---", "...", "- ", "-", "? ", "?", ": ", ":", "k: v", "a:b", "x #y", " #", "#", "&a", "*a", "!t", "!!str", "|", ">", "'", "\"",
        "''", "\\", "\\n", "%", "@", "`", "[", "]", "{", "}", ",", "[a, b]", "{a: b}", "http://example.com/a?b=c#d", "key:", "- item",
        "  lead", "trail  ", "\ttab", "tab\t", "a\tb", "  ", "\n", "\n\n", "\r", "\r\n", "\u0085", "\u2028", "\u2029", "\uFEFF", "\uFFFE",
        "\uFFFF", "\u00A0", "\u007F", "\u0000", "\u0007", "\u001B", "\u009F", "é", "日本語", "\U0001F600", "\U0010FFFF", "\uE000",
        "line one\nline two", "line one\n\nline two", "  indented\nsecond", "first\n  indented", "x\n", "x\n\n", "x\n\n\n", "\nx", "\n\nx",
        "a\n \nb", "a\n  \nb", "a \nb", "a\n\tb", "# not a comment", "text # comment", "- - -", "? - ?",
    ];

    private readonly Random _random;

    public HostileStrings(int seed) => _random = new Random(seed);

    public string Next(bool allowLong)
    {
        var sb = new StringBuilder();
        int mode = _random.Next(20);
        if (allowLong && mode == 0)
        {
            int target = _random.Next(1500, 6000);
            while (sb.Length < target)
            {
                AppendPiece(sb);
            }

            return sb.ToString();
        }

        int count = mode switch
        {
            < 3 => 1,
            < 12 => _random.Next(1, 4),
            _ => _random.Next(1, 9),
        };
        for (int i = 0; i < count; i++)
        {
            AppendPiece(sb);
        }

        return sb.ToString();
    }

    private void AppendPiece(StringBuilder sb)
    {
        switch (_random.Next(10))
        {
            case < 4:
                sb.Append(Fragments[_random.Next(Fragments.Length)]);
                break;
            case 4:
                sb.Append((char)_random.Next(0x20, 0x7F));
                break;
            case 5:
                sb.Append((char)_random.Next(0x00, 0x20));
                break;
            case 6:
                sb.Append((char)_random.Next(0x7F, 0xA1));
                break;
            case 7:
                sb.Append(char.ConvertFromUtf32(RandomScalar()));
                break;
            default:
                // Ordinary words keep a good share of values plain-friendly.
                int len = _random.Next(1, 9);
                for (int i = 0; i < len; i++)
                {
                    sb.Append((char)_random.Next('a', 'z' + 1));
                }

                if (_random.Next(3) == 0)
                {
                    sb.Append(' ');
                }

                break;
        }
    }

    private int RandomScalar()
    {
        int cp = _random.Next(5) switch
        {
            0 => _random.Next(0xA0, 0x800),
            1 => _random.Next(0x800, 0xD800),
            2 => _random.Next(0xE000, 0x10000),
            3 => _random.Next(0x10000, 0x110000),
            _ => _random.Next(0x2000, 0x2100),
        };
        return cp;
    }
}
