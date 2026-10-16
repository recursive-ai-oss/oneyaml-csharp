using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OneYaml.PublicBoundary;

/// <summary>
/// The terms the public-text check refuses, held as truncated SHA-256 hashes of their
/// normalised form so the repository carries no readable copy of the list. Each line of
/// the list file is a kind followed by one or two hashes; "#" starts a comment.
/// </summary>
/// <remarks>
/// Kinds:
/// <list type="bullet">
/// <item><c>s</c> (length, hash): a lower-case run of letters of that length, anywhere inside a word.</item>
/// <item><c>w</c>: a whole word, any letter case.</item>
/// <item><c>h</c>: as <c>w</c>, but not when a hyphen comes right before it.</item>
/// <item><c>n</c>: a word followed by digits, with an optional space or hyphen between.</item>
/// <item><c>p</c>: a word, one space, then a capital letter or digit.</item>
/// <item><c>f</c>: a word used as the stem of a Markdown file name.</item>
/// <item><c>x</c>: a whole token in exactly this letter case, in any text.</item>
/// <item><c>c</c>: as <c>x</c>, but only in comments, Markdown and commit messages.</item>
/// <item><c>d</c>: an exact-case word followed by a hyphen and digits.</item>
/// <item><c>g</c> (length, hash): a phrase of that length anywhere in the line, separators included.</item>
/// </list>
/// Words and phrases are hashed in lower case, except the exact-case kinds.
/// </remarks>
internal sealed class BannedTerms
{
    private const string Salt = "oneyaml-boundary:";
    private const int MaxToken = 64;

    private readonly record struct Key(ulong A, ulong B);

    private readonly HashSet<Key> _word = [];
    private readonly HashSet<Key> _wordNoHyphen = [];
    private readonly HashSet<Key> _digits = [];
    private readonly HashSet<Key> _phase = [];
    private readonly HashSet<Key> _stem = [];
    private readonly HashSet<Key> _exact = [];
    private readonly HashSet<Key> _prose = [];
    private readonly HashSet<Key> _dashed = [];
    private readonly Dictionary<int, HashSet<Key>> _inside = [];
    private readonly Dictionary<int, HashSet<Key>> _phrase = [];

    private static readonly Lazy<BannedTerms> DefaultTerms = new(LoadDefault);

    /// <summary>The list shipped with the tool.</summary>
    public static BannedTerms Default => DefaultTerms.Value;

    private static BannedTerms LoadDefault()
    {
        using Stream stream = typeof(BannedTerms).Assembly.GetManifestResourceStream("banned-terms.txt")
            ?? throw new InvalidOperationException("banned-terms.txt is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a list file of hashed terms.</summary>
    public static BannedTerms Parse(string text)
    {
        var terms = new BannedTerms();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (f[0])
            {
                case "s" when f.Length == 3:
                    terms.AddTo(terms._inside, int.Parse(f[1], CultureInfo.InvariantCulture), ParseKey(f[2]));
                    break;
                case "g" when f.Length == 3:
                    terms.AddTo(terms._phrase, int.Parse(f[1], CultureInfo.InvariantCulture), ParseKey(f[2]));
                    break;
                case "w" or "h" or "n" or "p" or "f" or "x" or "c" or "d" when f.Length == 2:
                    terms.Set(f[0]).Add(ParseKey(f[1]));
                    break;
                default:
                    throw new InvalidOperationException("bad line in the banned-term list: " + line);
            }
        }

        return terms;
    }

    /// <summary>Builds a list from plain words, for tests with neutral fixtures.</summary>
    public static BannedTerms FromPlain(params (string Kind, string Text)[] entries)
    {
        var terms = new BannedTerms();
        foreach ((string kind, string text) in entries)
        {
            string lower = text.ToLowerInvariant();
            switch (kind)
            {
                case "s":
                    terms.AddTo(terms._inside, text.Length, HashOf(lower));
                    break;
                case "g":
                    terms.AddTo(terms._phrase, text.Length, HashOf(lower));
                    break;
                case "x" or "c" or "d":
                    terms.Set(kind).Add(HashOf(text));
                    break;
                default:
                    terms.Set(kind).Add(HashOf(lower));
                    break;
            }
        }

        return terms;
    }

    /// <summary>The list-file hash of a normalised string.</summary>
    public static string HashText(string normalised)
    {
        Key k = HashOf(normalised);
        return k.A.ToString("x16", CultureInfo.InvariantCulture) + k.B.ToString("x16", CultureInfo.InvariantCulture);
    }

    private HashSet<Key> Set(string kind) => kind switch
    {
        "w" => _word,
        "h" => _wordNoHyphen,
        "n" => _digits,
        "p" => _phase,
        "f" => _stem,
        "x" => _exact,
        "c" => _prose,
        "d" => _dashed,
        _ => throw new InvalidOperationException("unknown kind " + kind),
    };

    private void AddTo(Dictionary<int, HashSet<Key>> sets, int length, Key key)
    {
        if (!sets.TryGetValue(length, out HashSet<Key>? set))
        {
            sets[length] = set = [];
        }

        set.Add(key);
    }

    private static Key ParseKey(string hex)
    {
        if (hex.Length != 32)
        {
            throw new InvalidOperationException("bad hash in the banned-term list: " + hex);
        }

        return new Key(
            ulong.Parse(hex.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            ulong.Parse(hex.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }

    private static Key HashOf(string s) => TryHash(s, out Key k) ? k : default;

    // Hashes the text as given; callers lower-case first where the kind needs it. A banned
    // term is never longer than MaxToken characters, so longer text is not worth hashing.
    private static bool TryHash(ReadOnlySpan<char> s, out Key key)
    {
        key = default;
        if (s.Length > MaxToken)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[Salt.Length + (MaxToken * 3)];
        int n = Encoding.UTF8.GetBytes(Salt, buffer);
        n += Encoding.UTF8.GetBytes(s, buffer[n..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer[..n], hash);
        key = new Key(
            BitConverter.ToUInt64(hash[..8]),
            BitConverter.ToUInt64(hash[8..16]));
        return true;
    }

    private static bool TryHashLower(ReadOnlySpan<char> s, out Key key)
    {
        if (s.Length > MaxToken)
        {
            key = default;
            return false;
        }

        Span<char> lower = stackalloc char[MaxToken];
        int n = s.ToLowerInvariant(lower);
        return TryHash(lower[..n], out key);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsUpperOrDigit(char c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Returns the first banned text found anywhere in a line, or null.</summary>
    public string? FindGeneral(string text)
    {
        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            if (!IsWord(text[i]))
            {
                i++;
                continue;
            }

            int s = i;
            while (i < n && IsWord(text[i]))
            {
                i++;
            }

            int e = i;
            if (TryHash(text.AsSpan(s, e - s), out Key whole) && _exact.Contains(whole))
            {
                return text[s..e];
            }

            int j = s;
            while (j < e)
            {
                if (!char.IsLetter(text[j]))
                {
                    j++;
                    continue;
                }

                int a = j;
                while (j < e && char.IsLetter(text[j]))
                {
                    j++;
                }

                string? hit = FindInLetters(text, s, e, a, j);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return FindPhrase(text);
    }

    /// <summary>Returns the first banned text of the prose-only kinds in a comment, or null.</summary>
    public string? FindProse(string text)
    {
        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            if (!IsWord(text[i]))
            {
                i++;
                continue;
            }

            int s = i;
            while (i < n && IsWord(text[i]))
            {
                i++;
            }

            if (TryHash(text.AsSpan(s, i - s), out Key whole) && _prose.Contains(whole))
            {
                return text[s..i];
            }
        }

        return null;
    }

    // One run of letters [a, b) inside the word [s, e) of the line.
    private string? FindInLetters(string text, int s, int e, int a, int b)
    {
        ReadOnlySpan<char> letters = text.AsSpan(a, b - a);
        bool wordBefore = a > s;
        bool wordAfter = b < e;

        foreach ((int length, HashSet<Key> set) in _inside)
        {
            for (int k = 0; k + length <= letters.Length; k++)
            {
                if (TryHashLower(letters.Slice(k, length), out Key inner) && set.Contains(inner))
                {
                    return text[a..b];
                }
            }
        }

        if (wordBefore || !TryHashLower(letters, out Key h))
        {
            return null;
        }

        if (!wordAfter && (_word.Contains(h) || (_wordNoHyphen.Contains(h) && (a == 0 || text[a - 1] != '-'))))
        {
            return text[a..b];
        }

        if (_digits.Contains(h))
        {
            int p = b;
            if (p < text.Length && text[p] is ' ' or '-')
            {
                p++;
            }

            if (p < text.Length && IsDigit(text[p]))
            {
                return text[a..b];
            }
        }

        if (_phase.Contains(h) && b + 1 < text.Length && text[b] == ' ' && IsUpperOrDigit(text[b + 1]))
        {
            return text[a..b];
        }

        if (_stem.Contains(h) && MarkdownSuffix(text, b))
        {
            return text[a..b];
        }

        if (_dashed.Contains(Exact(letters)) && !wordAfter && b + 1 < text.Length && text[b] == '-' && IsDigit(text[b + 1]))
        {
            int p = b + 1;
            while (p < text.Length && IsDigit(text[p]))
            {
                p++;
            }

            if (p >= text.Length || !IsWord(text[p]))
            {
                return text[a..p];
            }
        }

        return null;
    }

    private static Key Exact(ReadOnlySpan<char> s) => TryHash(s, out Key k) ? k : default;

    // ".md" in any letter case, not followed by another word character.
    private static bool MarkdownSuffix(string text, int at)
    {
        if (at + 3 > text.Length || text[at] != '.' || char.ToLowerInvariant(text[at + 1]) != 'm' || char.ToLowerInvariant(text[at + 2]) != 'd')
        {
            return false;
        }

        return at + 3 == text.Length || !IsWord(text[at + 3]);
    }

    // Phrases are matched as plain substrings of the lower-cased line, as a literal pattern would be.
    private string? FindPhrase(string text)
    {
        if (_phrase.Count == 0)
        {
            return null;
        }

        string lower = text.ToLowerInvariant();
        foreach ((int length, HashSet<Key> set) in _phrase)
        {
            for (int k = 0; k + length <= lower.Length; k++)
            {
                if (TryHash(lower.AsSpan(k, length), out Key key) && set.Contains(key))
                {
                    return text.Substring(k, length);
                }
            }
        }

        return null;
    }
}
