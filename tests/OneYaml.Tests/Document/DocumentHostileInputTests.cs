using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

// Hostile input aimed at the document's hash tables and integer parsing, and oversized streams.
[Collection(TimingGroup.Name)]
public class DocumentHostileInputTests
{
    // Names that share one FNV-1a 32-bit hash: at each stage two 5-letter blocks lead the running hash to the same
    // value, so any choice of block per stage gives the same hash for the whole name.
    private static string[] CollidingFnvNames(int stages)
    {
        uint state = 2166136261;
        var pairs = new List<(string, string)>();
        for (int s = 0; s < stages; s++)
        {
            var seen = new Dictionary<uint, string>();
            string? a = null, b = null;
            uint end = 0;
            for (int i = 0; a is null; i++)
            {
                string block = Block(i);
                uint h = Fnv(state, block);
                if (seen.TryGetValue(h, out string? other))
                {
                    a = other;
                    b = block;
                    end = h;
                }
                else
                {
                    seen[h] = block;
                }
            }

            pairs.Add((a!, b!));
            state = end;
        }

        var names = new string[1 << stages];
        for (int n = 0; n < names.Length; n++)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < stages; s++)
            {
                sb.Append((n >> s & 1) == 0 ? pairs[s].Item1 : pairs[s].Item2);
            }

            names[n] = sb.ToString();
        }

        return names;
    }

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static string Block(int i)
    {
        var chars = new char[5];
        for (int k = 0; k < 5; k++)
        {
            chars[k] = Alphabet[i % 62];
            i /= 62;
        }

        return new string(chars);
    }

    private static uint Fnv(uint h, string text)
    {
        foreach (char c in text)
        {
            h = (h ^ c) * 16777619;
        }

        return h;
    }

    private static TimeSpan ParseTime(string yaml)
    {
        TimeSpan best = TimeSpan.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var clock = CpuClock.Start();
            using YamlDocument doc = YamlDocument.Parse(yaml);
            TimeSpan t = clock.Elapsed;
            if (t < best)
            {
                best = t;
            }
        }

        return best;
    }

    [Fact]
    public void Anchor_names_with_one_hash_do_not_make_parsing_quadratic()
    {
        string[] names = CollidingFnvNames(14);
        Assert.Single(names.Select(n => Fnv(2166136261, n)).Distinct());
        Assert.Equal(names.Length, names.Distinct().Count());
        string Make(int count) => string.Concat(names.Take(count).Select(n => "- &" + n + " 1\n"));
        string small = Make(4096);
        string large = Make(16384);

        ParseTime(small);
        TimeSpan timeSmall = ParseTime(small);
        TimeSpan timeLarge = ParseTime(large);

        // Four times the names: about four times the work when lookups are constant; sixteen when they scan a cluster.
        Assert.True(timeLarge.TotalMilliseconds < Math.Max(timeSmall.TotalMilliseconds, 5) * 9, $"4096 names {timeSmall.TotalMilliseconds} ms, 16384 names {timeLarge.TotalMilliseconds} ms");
    }

    [Fact]
    public void Integer_keys_beyond_the_digit_limit_are_refused_before_they_are_compared()
    {
        string yaml = "? " + new string('1', 6000) + "\n: 1\n? " + new string('2', 6000) + "\n: 1\n";

        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Fact]
    public void Integer_keys_within_the_digit_limit_still_compare_exactly()
    {
        string same = "? " + new string('1', 4000) + "\n: 1\n? 0x" + new string('f', 10) + "\n: 1\n? " + new string('1', 4000) + "0\n: 1\n";
        using YamlDocument ok = YamlDocument.Parse(same);

        string dup = "? " + new string('1', 4000) + "\n: 1\n? +" + new string('1', 4000) + "\n: 1\n";
        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(dup));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }

    [Fact]
    public void A_seekable_stream_over_the_input_limit_is_refused_without_being_read()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 1000 } };
        var stream = new CountingStream(new byte[5000]);

        YamlException ex = Assert.Throws<YamlException>(() => YamlDocument.Parse(stream, options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task A_seekable_stream_over_the_input_limit_is_refused_without_being_read_asynchronously()
    {
        var options = new YamlDocumentOptions { Limits = YamlLimits.Default with { MaxInputBytes = 1000 } };
        var stream = new CountingStream(new byte[5000]);

        YamlException ex = await Assert.ThrowsAsync<YamlException>(() => YamlDocument.ParseAsync(stream, options, TestContext.Current.CancellationToken));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, stream.Reads);
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
