using Xunit;

namespace OneYaml.Tests;

// An explicit key of a mapping takes the place of a value merged into it, wherever the merge key stands, also when the
// mapping is itself the source of another merge key. Among several sources the first one wins.
public class MergePrecedenceTests
{
    private static readonly YamlSerializerOptions Merging = new() { MergeKeys = true };

    public sealed class Pair
    {
        public int A { get; set; }

        public int B { get; set; }
    }

    public sealed record PairRecord(int A, int B);

    public sealed class Wide
    {
        public int M0 { get; set; }
        public int M1 { get; set; }
        public int M2 { get; set; }
        public int M3 { get; set; }
        public int M4 { get; set; }
        public int M5 { get; set; }
        public int M6 { get; set; }
        public int M7 { get; set; }
        public int M8 { get; set; }
        public int M9 { get; set; }
        public int M10 { get; set; }
        public int M11 { get; set; }
        public int M12 { get; set; }
        public int M13 { get; set; }
        public int M14 { get; set; }
        public int M15 { get; set; }
        public int M16 { get; set; }
        public int M17 { get; set; }
        public int M18 { get; set; }
        public int M19 { get; set; }
        public int M20 { get; set; }
        public int M21 { get; set; }
        public int M22 { get; set; }
        public int M23 { get; set; }
        public int M24 { get; set; }
        public int M25 { get; set; }
        public int M26 { get; set; }
        public int M27 { get; set; }
        public int M28 { get; set; }
        public int M29 { get; set; }
        public int M30 { get; set; }
        public int M31 { get; set; }
        public int M32 { get; set; }
        public int M33 { get; set; }
        public int M34 { get; set; }
    }

    public sealed class Holder<T>
    {
        public T Out { get; set; } = default!;
    }

    // Each case: the document, then the expected a and b of "out" (a missing key reads as 0).
    public static IEnumerable<object[]> Orders() =>
    [
        ["s: &s {<<: {a: 1}, a: 2}\nout: {<<: *s}\n", 2, 0],
        ["s: &s {<<: {a: 1}, a: 2, b: 3}\nout:\n  <<: *s\n", 2, 3],
        ["s: &s {a: 2, <<: {a: 1}}\nout: {<<: *s}\n", 2, 0],
        ["s: &s {<<: {a: 1, b: 7}, b: 3}\nout: {<<: *s}\n", 1, 3],
        ["s: &s {<<: {<<: {a: 1}, a: 2}, a: 3}\nout: {<<: *s}\n", 3, 0],
        ["s: &s {<<: {<<: {a: 1}, a: 2}}\nout: {<<: *s}\n", 2, 0],
        ["s: &s {<<: [{a: 1}, {a: 5, b: 7}]}\nout: {<<: *s}\n", 1, 7],
        ["s: &s {<<: [{a: 1}, {a: 5, b: 7}], a: 4}\nout: {<<: *s}\n", 4, 7],
        ["s: &s {<<: {a: 1}, a: 2}\nt: &t {a: 9, b: 4}\nout: {<<: [*s, *t]}\n", 2, 4],
        ["s: &s {<<: {a: 1}, a: 2}\nt: &t {a: 9, b: 4}\nout: {<<: [*t, *s]}\n", 9, 4],
        ["s: &s {<<: {a: 1}, a: 2}\nout: {<<: *s, a: 8}\n", 8, 0],
        ["s: &s {<<: {a: 1}, a: 2}\nout: {a: 8, <<: *s}\n", 8, 0],
        ["s: &s {<<: {a: 1}, a: 2}\nout: {<<: {<<: *s}}\n", 2, 0],
        ["out: {<<: {<<: {a: 1}, a: 2}}\n", 2, 0],
        ["s: &s {b: 1, <<: {a: 1, b: 5}, a: 2}\nout: {<<: *s}\n", 2, 1],
    ];

    [Theory, MemberData(nameof(Orders))]
    public void A_class_follows_the_precedence_of_the_generic_load(string yaml, int a, int b)
    {
        Pair p = YamlSerializer.Deserialize<Holder<Pair>>(yaml, Merging)!.Out;
        Assert.Equal((a, b), (p.A, p.B));
    }

    [Theory, MemberData(nameof(Orders))]
    public void A_record_follows_the_precedence_of_the_generic_load(string yaml, int a, int b)
    {
        PairRecord p = YamlSerializer.Deserialize<Holder<PairRecord>>(yaml, Merging)!.Out;
        Assert.Equal((a, b), (p.A, p.B));
    }

    [Theory, MemberData(nameof(Orders))]
    public void A_dictionary_follows_the_precedence_of_the_generic_load(string yaml, int a, int b)
    {
        Dictionary<string, int> d = YamlSerializer.Deserialize<Holder<Dictionary<string, int>>>(yaml, Merging)!.Out;
        Assert.Equal((a, b), (d.GetValueOrDefault("a"), d.GetValueOrDefault("b")));
        Assert.Equal(d.Count, (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0) + (d.ContainsKey("a") && a == 0 ? 1 : 0));
    }

    [Theory, MemberData(nameof(Orders))]
    public void The_generic_load_and_the_document_agree_with_the_table(string yaml, int a, int b)
    {
        var generic = (Dictionary<string, object?>)((Dictionary<string, object?>)YamlSerializer.Deserialize<object>(yaml, Merging)!)["out"]!;
        Assert.Equal((a, b), ((int)(long)(generic.GetValueOrDefault("a") ?? 0L), (int)(long)(generic.GetValueOrDefault("b") ?? 0L)));

        using var doc = YamlDocument.Parse(yaml);
        Pair p = doc.Root["out"].Deserialize<Pair>(Merging)!;
        Assert.Equal((a, b), (p.A, p.B));
        Dictionary<string, int> d = doc.Root["out"].Deserialize<Dictionary<string, int>>(Merging)!;
        Assert.Equal((a, b), (d.GetValueOrDefault("a"), d.GetValueOrDefault("b")));
    }

    [Fact]
    public void A_type_with_more_than_thirty_two_members_keeps_the_precedence_for_members_past_the_first_word()
    {
        const string yaml = "s: &s {<<: {m33: 1, m34: 1, m1: 1}, m33: 2, m1: 2}\nout: {<<: *s, m34: 5}\n";
        Wide w = YamlSerializer.Deserialize<Holder<Wide>>(yaml, Merging)!.Out;
        Assert.Equal((2, 2, 5), (w.M1, w.M33, w.M34));
    }

    [Fact]
    public void A_repeated_key_in_a_source_after_a_nested_merge_is_still_refused()
    {
        const string yaml = "s: &s {<<: {a: 1}, a: 2, a: 3}\nout: {<<: *s}\n";
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder<Pair>>(yaml, Merging)).Category);
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder<Dictionary<string, int>>>(yaml, Merging)).Category);
    }

    [Fact]
    public void Keys_merged_into_a_source_are_decided_for_others_but_not_given_by_the_source()
    {
        var inner = new MemberSet(70, mergeSource: true);
        inner.Add(3);
        inner.Add(40);
        inner.Add(69);
        var source = new MemberSet(70, mergeSource: true);
        source.MarkMerged(in inner);
        foreach (int i in new[] { 3, 40, 69 })
        {
            Assert.False(source.Contains(i));
        }

        var decided = new MemberSet(70);
        decided.UnionWithAll(in source);
        foreach (int i in new[] { 3, 40, 69 })
        {
            Assert.True(decided.Contains(i));
        }

        Assert.False(decided.Contains(4));
        source.Add(40);
        Assert.True(source.Contains(40));
        Assert.False(source.Contains(41));
    }
}
