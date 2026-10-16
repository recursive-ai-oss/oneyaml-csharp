using System.Text;
using OneYaml.Parsing;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Inputs built to make a reader do work out of proportion to their size.</summary>
[Collection(TimingGroup.Name)]
public class HostileInputCostTests
{
    // Anchor names that all share one hash under the plain 32-bit FNV-1a function (a multicollision: after each
    // stage the two blocks lead to the same state, so every combination of blocks hashes alike).
    private static List<string> CollidingNames(int stages)
    {
        const uint Prime = 16777619;
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        static uint Step(uint h, string block)
        {
            foreach (char c in block)
            {
                h = (h ^ c) * Prime;
            }

            return h;
        }

        var random = new Random(7);
        uint state = 2166136261;
        var pairs = new List<(string, string)>();
        for (int s = 0; s < stages; s++)
        {
            var seen = new Dictionary<uint, string>();
            while (true)
            {
                string block = string.Create(5, random, static (span, r) =>
                {
                    for (int i = 0; i < span.Length; i++)
                    {
                        span[i] = Alphabet[r.Next(Alphabet.Length)];
                    }
                });
                uint next = Step(state, block);
                if (seen.TryGetValue(next, out string? other) && other != block)
                {
                    pairs.Add((other, block));
                    state = next;
                    break;
                }

                seen[next] = block;
            }
        }

        var names = new List<string> { string.Empty };
        foreach ((string a, string b) in pairs)
        {
            var grown = new List<string>(names.Count * 2);
            foreach (string n in names)
            {
                grown.Add(n + a);
                grown.Add(n + b);
            }

            names = grown;
        }

        return names;
    }

    private static string AnchorList(List<string> names)
    {
        var sb = new StringBuilder();
        foreach (string n in names)
        {
            sb.Append("- &").Append(n).Append(" 1\n");
        }

        return sb.ToString();
    }

    private static double BestCpuSeconds(Action run)
    {
        run();
        double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            CpuClock clock = CpuClock.Start();
            run();
            best = Math.Min(best, clock.Elapsed.TotalSeconds);
        }

        return best;
    }

    [Fact]
    public void Anchor_names_with_one_hash_value_cost_time_in_proportion_to_their_number()
    {
        List<string> names = CollidingNames(16);
        string small = AnchorList(names.GetRange(0, 16384));
        string large = AnchorList(names);
        Assert.Equal(65536, names.Count);

        double t1 = BestCpuSeconds(() => YamlSerializer.Deserialize<object>(small));
        double t4 = BestCpuSeconds(() => YamlSerializer.Deserialize<object>(large));

        // Four times the names: about 4x the time when linear, about 16x when every lookup walks all the others.
        Assert.True(t4 < 9 * Math.Max(t1, 0.02), $"16,384 names took {t1:F3} s and 65,536 names took {t4:F3} s");
    }

    [Fact]
    public void Anchor_names_with_one_hash_value_are_linear_for_a_typed_read_too()
    {
        List<string> names = CollidingNames(16);
        string small = AnchorList(names.GetRange(0, 16384));
        string large = AnchorList(names);

        double t1 = BestCpuSeconds(() => YamlSerializer.Deserialize<List<int>>(small));
        double t4 = BestCpuSeconds(() => YamlSerializer.Deserialize<List<int>>(large));

        Assert.True(t4 < 9 * Math.Max(t1, 0.02), $"16,384 names took {t1:F3} s and 65,536 names took {t4:F3} s");
    }

    // Two suffixes alternate, so a cache that remembers only the last resolved tag cannot absorb the cost.
    private static string TaggedNodes(int prefixLength, int nodes) =>
        "%TAG !e! tag:x,2000:" + new string('a', prefixLength) + "\n---\n" + string.Concat(Enumerable.Range(0, nodes).Select(i => i % 2 == 0 ? "- !e!x a\n" : "- !e!y a\n"));

    [Fact]
    public void A_long_tag_prefix_used_by_many_nodes_allocates_nothing_per_node()
    {
        string yaml = TaggedNodes(100_000, 2000);
        YamlSerializer.Deserialize<object>(yaml);

        long before = GC.GetAllocatedBytesForCurrentThread();
        object? value = YamlSerializer.Deserialize<object>(yaml);
        long generic = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        List<string>? typed = YamlSerializer.Deserialize<List<string>>(yaml);
        long reflected = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2000, ((List<object?>)value!).Count);
        Assert.Equal(2000, typed!.Count);

        // The input is 118 KB. Building the full tag for each node would take 2,000 x 100 KB = 200 MB.
        Assert.True(generic < 4_000_000, $"generic load allocated {generic:N0} bytes");
        Assert.True(reflected < 4_000_000, $"typed load allocated {reflected:N0} bytes");
    }

    [Theory]
    [InlineData("!!str", "Str")]
    [InlineData("!!int", "Int")]
    [InlineData("!!float", "Float")]
    [InlineData("!!bool", "Bool")]
    [InlineData("!!null", "Null")]
    [InlineData("!!binary", "Binary")]
    [InlineData("!!map", "Map")]
    [InlineData("!!seq", "Seq")]
    [InlineData("!!timestamp", "Unknown")]
    [InlineData("!!st%72", "Str")]
    [InlineData("!", "Str")]
    [InlineData("!local", "Unknown")]
    [InlineData("!<tag:yaml.org,2002:int>", "Int")]
    [InlineData("!<tag:yaml.org,2002:in%74>", "Int")]
    [InlineData("!<!>", "Str")]
    [InlineData("!<tag:yaml.org,2002:integer>", "Unknown")]
    [InlineData("!e!int", "Int")]
    [InlineData("!e!seq", "Seq")]
    [InlineData("!f!int", "Unknown")]
    [InlineData("!e!i%6et", "Int")]
    [InlineData("!!aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Unknown")]
    public void The_tag_class_matches_the_class_of_the_resolved_tag(string tag, string expected)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("%TAG !e! tag:yaml.org,2002:\n%TAG !f! tag:other,2000:\n---\n" + tag + " x\n");
        using var reader = new YamlReader(utf8);
        bool seen = false;
        while (reader.Read())
        {
            if (reader.HasTag)
            {
                seen = true;
                Assert.Equal(Enum.Parse<TagClass>(expected), reader.GetTagClass());
                Assert.Equal(ClassOfResolvedTag(reader.GetResolvedTag()), reader.GetTagClass());
            }
        }

        Assert.True(seen);
    }

    // The class a fully resolved tag stands for, worked out from the string alone.
    private static TagClass ClassOfResolvedTag(string? tag) => tag switch
    {
        null => TagClass.None,
        "!" => TagClass.Str,
        "tag:yaml.org,2002:str" => TagClass.Str,
        "tag:yaml.org,2002:int" => TagClass.Int,
        "tag:yaml.org,2002:float" => TagClass.Float,
        "tag:yaml.org,2002:bool" => TagClass.Bool,
        "tag:yaml.org,2002:null" => TagClass.Null,
        "tag:yaml.org,2002:binary" => TagClass.Binary,
        "tag:yaml.org,2002:map" => TagClass.Map,
        "tag:yaml.org,2002:seq" => TagClass.Seq,
        _ => TagClass.Unknown,
    };

    [Fact]
    public void Nodes_that_share_a_tag_share_its_string()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("%TAG !e! tag:x,2000:\n---\n- !e!x a\n- !e!x b\n- !e!y c\n");
        using var reader = new YamlReader(utf8);
        var tags = new List<string>();
        while (reader.Read())
        {
            if (reader.HasTag)
            {
                tags.Add(reader.GetResolvedTag()!);
            }
        }

        Assert.Equal(["tag:x,2000:x", "tag:x,2000:x", "tag:x,2000:y"], tags);
        Assert.Same(tags[0], tags[1]);
    }

    private sealed class LongStream(long length) : Stream
    {
        public int Reads;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return 0;
        }
    }

    [Fact]
    public void A_seekable_stream_longer_than_the_limit_is_refused_before_any_buffer_is_taken()
    {
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
        var stream = new LongStream(YamlLimits.Untrusted.MaxInputBytes + 1);
        YamlSerializer.Deserialize<object>(new MemoryStream(), options);

        long before = GC.GetAllocatedBytesForCurrentThread();
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(stream, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Equal(0, stream.Reads);
        Assert.True(allocated < 100_000, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public async Task A_seekable_stream_longer_than_the_limit_is_refused_before_any_buffer_is_taken_asynchronously()
    {
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
        var stream = new LongStream(YamlLimits.Untrusted.MaxInputBytes + 1);

        YamlException e = await Assert.ThrowsAsync<YamlException>(() => YamlSerializer.DeserializeAsync<object>(stream, options, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Text_whose_utf8_size_passes_the_largest_array_ends_in_a_limit_error()
    {
        // 720 million three-byte characters: 2.16 billion bytes, past what an int or an array can hold.
        string text = new('\u4E2D', 720_000_000);
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(text));
        Assert.Equal(YamlErrorCategory.Limit, e.Category);
    }
}
