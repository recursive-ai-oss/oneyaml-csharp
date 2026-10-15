using System.Diagnostics;
using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Edits at the end of a block collection that has a block scalar or trailing comments (the edited state must
/// equal a fresh parse of the same bytes, for every line ending), float keys with huge exponents, and
/// merge patches that delete many members.
/// </summary>
public class SpliceAndKeyCostTests
{
    private static string Edit(string yaml, Action<YamlDocument> edit)
    {
        byte[] spliced = Run(yaml, edit, splice: true);
        Assert.Equal(Run(yaml, edit, splice: false), spliced);
        return Encoding.UTF8.GetString(spliced);
    }

    private static byte[] Run(string yaml, Action<YamlDocument> edit, bool splice)
    {
        DocState.NoSplice = !splice;
        try
        {
            using YamlDocument doc = YamlDocument.Parse(yaml);
            edit(doc);
            return doc.ToBytes();
        }
        finally
        {
            DocState.NoSplice = false;
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Removing_an_entry_takes_the_indented_blank_line_that_would_join_the_scalar_before_it(string nl)
    {
        // The blank line with three spaces ended the removed entry's own literal (indented by four).
        // Left behind, it would follow a literal indented by two and become part of its text.
        string yaml = "a: |\n  text\nb:\n  m: |\n    x\n   \nc: 1\n".Replace("\n", nl);
        string result = Edit(yaml, d => d.Remove("/b"));
        Assert.Equal("a: |\n  text\nc: 1\n".Replace("\n", nl), result);
        using YamlDocument again = YamlDocument.Parse(result);
        Assert.Equal("text\n", again.Get<string>("/a"));
    }

    // The edit must be applied in place, not by parsing the whole text again: a region that misreads the line
    // ends of the text would fall back silently and still give the right bytes.
    private static void AssertInPlace(string yaml, Action<YamlDocument> edit)
    {
        using YamlDocument doc = YamlDocument.Parse(yaml);
        SpliceCheck.ResetThread();
        edit(doc);
        Assert.True(SpliceCheck.ThreadSpliced > 0 && SpliceCheck.ThreadFull == 0, $"in place {SpliceCheck.ThreadSpliced}, whole text {SpliceCheck.ThreadFull}");
    }

    // ---- A block scalar that becomes the last value of the region ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Removing_the_last_entry_hands_the_blank_line_to_the_block_scalar_before_it(string nl)
    {
        string yaml = "top:\n   key1: |\n     t\n     # in scalar\n   key2:\n     # inner\n     k: 1\n      \n  # x\n# tail\nafter: 1\n".Replace("\n", nl);
        AssertInPlace(yaml, d => d.Remove("/top/key2"));
        string result = Edit(yaml, d =>
        {
            d.Remove("/top/key2");
            d.Set("/top/key1", "a b");
        });
        Assert.Equal("top:\n   key1: |-\n     a b\n  # x\n# tail\nafter: 1\n".Replace("\n", nl), result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void The_state_after_removing_the_last_entry_equals_a_fresh_parse(string nl)
    {
        // The observer installed for the test run compares every incremental state with a parse of its bytes.
        string yaml = "top:\n   key1: >\n     t\n   key2: v\n      \n  # x\nafter: 1\n".Replace("\n", nl);
        using YamlDocument doc = YamlDocument.Parse(yaml);
        doc.Remove("/top/key2");
        doc.Set("/top/key1", "q");
        doc.Set("/top/added", 1);
        using YamlDocument fresh = YamlDocument.Parse(doc.ToBytes());
        Assert.Equal(fresh.Get<string>("/top/key1"), doc.Get<string>("/top/key1"));
        Assert.Equal(fresh.ToBytes(), doc.ToBytes());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Replacing_a_last_block_scalar_sees_the_comment_and_blank_lines_after_it(string nl)
    {
        string yaml = "top:\n  a: |\n    t\n  # c1\n\n  \t\n  # c2\nafter: 1\n".Replace("\n", nl);
        AssertInPlace(yaml, d => d.Set("/top/a", "x y"));
        string result = Edit(yaml, d => d.Set("/top/a", "x y"));
        Assert.Equal("top:\n  a: |-\n    x y\n  # c1\n\n  \t\n  # c2\nafter: 1\n".Replace("\n", nl), result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void A_kept_literal_at_the_end_keeps_its_blank_lines_through_an_edit_before_it(string nl)
    {
        string yaml = "top:\n  a: 1\n  b: |+\n    t\n\n\n  # c\nafter: 1\n".Replace("\n", nl);
        string result = Edit(yaml, d => d.Set("/top/a", 2));
        Assert.Equal(yaml.Replace("a: 1", "a: 2"), result);
        string removed = Edit(yaml, d => d.Remove("/top/a"));
        Assert.Equal("top:\n  b: |+\n    t\n\n\n  # c\nafter: 1\n".Replace("\n", nl), removed);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Comment_lines_after_a_plain_last_value_are_not_reparsed(string nl)
    {
        var sb = new StringBuilder("top:" + nl + "  a: 1" + nl + "  b: 2" + nl);
        for (int i = 0; i < 3000; i++)
        {
            sb.Append("  # a long comment line that follows the last entry").Append(nl);
        }

        sb.Append("after: 1").Append(nl);
        using YamlDocument doc = YamlDocument.Parse(sb.ToString());
        SpliceCheck.ResetThread();
        doc.Set("/top/b", 3);
        Assert.True(SpliceCheck.ThreadSpliced > 0, "the edit should be applied in place");
        Assert.True(SpliceCheck.ThreadBytes < 1000, $"{SpliceCheck.ThreadBytes} bytes were parsed again for a one-byte edit");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void A_comment_on_the_last_line_of_an_entry_does_not_pull_in_the_rest_of_the_file(string nl)
    {
        // Every line ending must be recognised as the end of the comment, or the region would run to the end of the text.
        var sb = new StringBuilder("top:" + nl + "  a: 1" + nl + "  b: 2 # tail" + nl + "  # more" + nl);
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("key").Append(i).Append(": value").Append(nl);
        }

        using YamlDocument doc = YamlDocument.Parse(sb.ToString());
        SpliceCheck.ResetThread();
        doc.Set("/top/b", 3);
        Assert.True(SpliceCheck.ThreadSpliced > 0 && SpliceCheck.ThreadFull == 0, $"in place {SpliceCheck.ThreadSpliced}, whole text {SpliceCheck.ThreadFull}");
        Assert.True(SpliceCheck.ThreadBytes < 1000, $"{SpliceCheck.ThreadBytes} bytes were parsed again for a one-byte edit");
        Assert.Equal(3, doc.Get<int>("/top/b"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Lines_after_a_block_scalar_stop_at_the_next_line_with_content(string nl)
    {
        var sb = new StringBuilder("top:" + nl + "  a: 1" + nl + "  b: |" + nl + "    text" + nl + "  # c" + nl + nl);
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("key").Append(i).Append(": value").Append(nl);
        }

        using YamlDocument doc = YamlDocument.Parse(sb.ToString());
        SpliceCheck.ResetThread();
        doc.Set("/top/b", "other");
        Assert.True(SpliceCheck.ThreadSpliced > 0 && SpliceCheck.ThreadFull == 0, $"in place {SpliceCheck.ThreadSpliced}, whole text {SpliceCheck.ThreadFull}");
        Assert.True(SpliceCheck.ThreadBytes < 1000, $"{SpliceCheck.ThreadBytes} bytes were parsed again for a small edit");
    }

    // The end of the trailing lines is found without a whole-file parse, so each line ending has to be
    // recognised on its own: a region that ran too far would be thrown away and retried one level up,
    // which gives the same bytes and hides the mistake.
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void The_trailing_lines_of_a_collection_end_at_the_next_line_with_content(string nl)
    {
        byte[] src = Encoding.UTF8.GetBytes(("b: 2 # tail\n  # more\n\n  \t\n# last\nkey: v\n").Replace("\n", nl));
        int afterTwo = 4;
        int firstLineEnd = Encoding.UTF8.GetString(src).IndexOf("  # more", StringComparison.Ordinal);
        int key = Encoding.UTF8.GetString(src).IndexOf("key:", StringComparison.Ordinal);
        int last = Encoding.UTF8.GetString(src).IndexOf("# last", StringComparison.Ordinal);
        // Only the rest of the last line, with its comment and break.
        Assert.Equal(firstLineEnd, DocState.TrailingTriviaEnd(src, afterTwo, followingLines: false));
        // Every blank and comment line after it, up to the next line with content.
        Assert.Equal(key, DocState.TrailingTriviaEnd(src, afterTwo, followingLines: true));
        Assert.NotEqual(last, key);
        // A comment that is the last line of the text, with or without a break.
        byte[] tail = Encoding.UTF8.GetBytes(("b: 2\n# only" + nl).Replace("\n", nl));
        Assert.Equal(tail.Length, DocState.TrailingTriviaEnd(tail, 4, followingLines: true));
        byte[] bare = Encoding.UTF8.GetBytes("b: 2 # no break");
        Assert.Equal(bare.Length, DocState.TrailingTriviaEnd(bare, 4, followingLines: false));
    }

    // ---- Float keys with very long exponents ----

    private static string Digits(int count, char c = '7') => new(c, count);

    [Fact]
    public void Float_keys_with_a_million_digit_exponent_are_compared_in_linear_time()
    {
        string exp = Digits(1_000_000);
        string same = $"{{1e{exp}: a, 2e{exp}: b}}";
        var watch = Stopwatch.StartNew();
        using (YamlDocument doc = YamlDocument.Parse(same))
        {
            Assert.Equal(2, doc.Root.Count);
        }

        var generic = Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(same));
        Assert.Equal(2, generic.Count);
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlDocument.Parse($"{{1e{exp}: a, 10e{exp[..^1]}6: b}}")).Category);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed.TotalSeconds:F1} s");
    }

    [Theory]
    [InlineData("1e100000000000000000000", "10e99999999999999999999")]
    [InlineData("1e100000000000000000000", "0.1e100000000000000000001")]
    [InlineData("1e-100000000000000000000", "0.1e-99999999999999999999")]
    [InlineData("1e-100000000000000000000", "10e-100000000000000000001")]
    [InlineData("1e+0000000000000000000000000400", "1E400")]
    [InlineData("123.456e999999999999999999", "1.23456e1000000000000000001")]
    [InlineData("-1e99999999999999999999", "-0.01e100000000000000000001")]
    public void The_same_value_with_a_huge_exponent_is_a_duplicate_key_in_both_readers(string a, string b)
    {
        string yaml = $"{{{a}: x, {b}: y}}";
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml)).Category);
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml)).Category);
    }

    [Theory]
    [InlineData("1e100000000000000000000", "1e100000000000000000001")]
    [InlineData("1e100000000000000000000", "-1e100000000000000000000")]
    [InlineData("1e-100000000000000000000", "1e100000000000000000000")]
    [InlineData("1e-100000000000000000000", "2e-100000000000000000000")]
    [InlineData("1e400", "2e400")]
    public void Different_values_with_a_huge_exponent_are_two_keys_in_both_readers(string a, string b)
    {
        string yaml = $"{{{a}: x, {b}: y}}";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Assert.Equal(2, doc.Root.Count);
        Assert.Equal(2, Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml)).Count);
    }

    [Fact]
    public void Literals_that_round_to_the_same_subnormal_are_one_key()
    {
        // 3e-324 and 4e-324 are both the smallest double, so they are the same value; 1e-400 and 2e-400 are not,
        // because a double cannot hold either of them and each keeps its own value.
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlDocument.Parse("{3e-324: a, 4e-324: b}")).Category);
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("{3e-324: a, 4e-324: b}")).Category);
    }

    [Theory]
    [InlineData("e400", 20_000)]
    [InlineData("e-400", 20_000)]
    public void Many_float_keys_beyond_the_double_range_do_not_share_one_bucket(string suffix, int count)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            sb.Append(i).Append(suffix).Append(": v\n");
        }

        string yaml = sb.ToString();
        var watch = Stopwatch.StartNew();
        using (YamlDocument doc = YamlDocument.Parse(yaml))
        {
            Assert.Equal(count, doc.Root.Count);
        }

        Assert.Equal(count, Assert.IsType<Dictionary<string, object?>>(YamlSerializer.Deserialize<object>(yaml)).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed.TotalSeconds:F1} s");
    }

    // ---- Merge patches that delete many members ----

    [Fact]
    public void Deleting_every_key_in_one_patch_gives_what_one_removal_at_a_time_gives()
    {
        const string yaml = "# head\na: 1\n# about b\nb: [1, 2]\nc:\n  x: 1\nd: |\n  text\n\ne: 5\n";
        string[] names = ["c", "a", "e", "d", "b", "absent"];
        string patch = "{" + string.Join(",", names.Select(n => $"\"{n}\":null")) + "}";
        string batch = Edit(yaml, d => d.ApplyMergePatch(patch));
        string oneByOne = Edit(yaml, d =>
        {
            foreach (string n in names)
            {
                d.ApplyMergePatch("{\"" + n + "\":null}");
            }
        });
        Assert.Equal(oneByOne, batch);
        Assert.Contains("{}", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_every_key_of_a_large_mapping_is_linear()
    {
        long Measure(int count, out long bytes)
        {
            var sb = new StringBuilder();
            var patch = new StringBuilder("{");
            for (int i = 0; i < count; i++)
            {
                sb.Append("key").Append(i).Append(": ").Append(i).Append('\n');
                patch.Append(i == 0 ? "" : ",").Append("\"key").Append(i).Append("\":null");
            }

            patch.Append('}');
            using YamlDocument doc = YamlDocument.Parse(sb.ToString());
            string p = patch.ToString();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            doc.ApplyMergePatch(p);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("{}\n", Encoding.UTF8.GetString(doc.ToBytes()));
            return watch.ElapsedMilliseconds;
        }

        Measure(500, out _);
        long small = Measure(4_000, out long smallBytes);
        long large = Measure(16_000, out long largeBytes);
        // Four times the keys must not cost anything like sixteen times the allocation or time.
        Assert.True(largeBytes < smallBytes * 8, $"{smallBytes} -> {largeBytes} bytes");
        Assert.True(large < 1_000 && large < Math.Max(small, 20) * 10, $"{small} ms -> {large} ms");
    }

    [Fact]
    public void Member_names_chosen_to_collide_under_a_fixed_hash_do_not_make_the_patch_quadratic()
    {
        // Names that share one FNV-1a hash: the same 32-bit state is reached by appending four bytes that undo
        // the previous four, so every pair of these blocks collides.
        string[] names = Colliding(17);
        var watch = Stopwatch.StartNew();
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\nc: 3\n");
        doc.ApplyMergePatch("{" + string.Join(",", names.Select(n => $"\"{n}\":null")) + "}");
        Assert.Equal("a: 1\nb: 2\nc: 3\n", Encoding.UTF8.GetString(doc.ToBytes()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    // Builds 2^bits names, each a run of `bits` six-letter blocks where every block is one of two strings that
    // leave the 32-bit FNV-1a state in the same place. The state after each block is the same for both choices,
    // so a pair that collides from that state can be found level by level with a birthday search.
    private static string[] Colliding(int bits)
    {
        const uint prime = 16777619;
        var pairs = new (string, string)[bits];
        uint state = 2166136261;
        for (int level = 0; level < bits; level++)
        {
            var seen = new Dictionary<uint, string>();
            bool found = false;
            var random = new Random(level + 1);
            for (int i = 0; i < 600_000 && !found; i++)
            {
                var chars = new char[6];
                for (int c = 0; c < chars.Length; c++)
                {
                    chars[c] = (char)('a' + random.Next(26));
                }

                string s = new(chars);
                uint h = state;
                foreach (char c in s)
                {
                    h = (h ^ c) * prime;
                }

                if (seen.TryGetValue(h, out string? other) && other != s)
                {
                    pairs[level] = (other, s);
                    state = h;
                    found = true;
                }
                else
                {
                    seen[h] = s;
                }
            }

            Assert.True(found, "no colliding pair found");
        }

        var result = new string[1 << bits];
        for (int i = 0; i < result.Length; i++)
        {
            var sb = new StringBuilder(bits * 6);
            for (int b = 0; b < bits; b++)
            {
                sb.Append((i >> b & 1) == 0 ? pairs[b].Item1 : pairs[b].Item2);
            }

            result[i] = sb.ToString();
        }

        return result;
    }
}
