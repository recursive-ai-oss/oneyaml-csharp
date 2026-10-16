using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// What a node replayed from a document tells the binder (tags, anchors), the cost of try-reads that
/// miss on values the kind check cannot rule out, one set of rules for text in messages, and merge keys
/// read into typed collections.
/// </summary>
public class ReplayFollowUpTests
{
    private static string Doc(string items) => "items:\n" + items;

    [Fact]
    public void A_get_of_an_out_of_range_number_still_names_the_position()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 99999999999\n");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<int>("/b"));
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Pointer_text_in_a_message_follows_the_exception_excerpt_rules()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<int>("/x\ty"));

        // The exception escapes a tab as \t; a second rule set wrote \u0009.
        Assert.Contains("x\\ty", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0009", ex.Message, StringComparison.Ordinal);
        Assert.Equal(YamlException.Excerpt("x\ty", 200), MessageText.Bound("x\ty"));
        Assert.Equal(YamlException.Excerpt(new string('k', 500), 200), MessageText.Bound(Encoding.UTF8.GetBytes(new string('k', 500))));
    }

    // ---- tags on replay ----

    private sealed class TagSeen
    {
        [YamlConverter(typeof(TagOnly))]
        public string? A { get; set; }

        [YamlConverter(typeof(TagOnly))]
        public string? B { get; set; }

        [YamlConverter(typeof(TagOnly))]
        public string? C { get; set; }

        [YamlConverter(typeof(TagOnly))]
        public string? D { get; set; }

        [YamlConverter(typeof(TagOnly))]
        public string? E { get; set; }
    }

    private sealed class TagOnly : YamlConverter<string>
    {
        public override string? Read(ref Utf8YamlReader reader, Type typeToConvert, YamlSerializerOptions options)
        {
            string raw = reader.TryGetTag(out ReadOnlySpan<byte> tag) ? Encoding.UTF8.GetString(tag) : string.Empty;
            return reader.Tag + "|" + raw;
        }

        public override void Write(Utf8YamlEmitter writer, string value, YamlSerializerOptions options) => writer.WriteStringValue(value);
    }

    [Fact]
    public void A_replayed_node_reports_the_same_tag_as_the_serializer()
    {
        string yaml = "%TAG !e! tag:example.com,2000:\n%TAG !! tag:other,2001:\n---\na: !t 1\nb: !!str 2\nc: 3\nd: !<tag:example.com,2000:y> 4\ne: !e!z%21 5\n";
        TagSeen direct = YamlSerializer.Deserialize<TagSeen>(yaml)!;
        using YamlDocument doc = YamlDocument.Parse(yaml);
        TagSeen replayed = doc.Root.Deserialize<TagSeen>()!;

        Assert.Equal("!t|!t", direct.A);
        Assert.Equal("tag:example.com,2000:z!|!e!z%21", direct.E);
        Assert.Equal(direct.A, replayed.A);
        Assert.Equal(direct.B, replayed.B);
        Assert.Equal(direct.C, replayed.C);
        Assert.Equal(direct.D, replayed.D);
        Assert.Equal(direct.E, replayed.E);
    }

    [Fact]
    public void A_replayed_node_reports_tags_with_the_default_handles()
    {
        string yaml = "a: !t 1\nb: !!str 2\nc: 3\nd: !<tag:example.com,2000:y> 4\ne: !x 5\n";
        TagSeen direct = YamlSerializer.Deserialize<TagSeen>(yaml)!;
        TagSeen replayed = YamlDocument.Parse(yaml).Root.Deserialize<TagSeen>()!;
        Assert.Equal("tag:yaml.org,2002:str|!!str", direct.B);
        Assert.Equal(direct.A, replayed.A);
        Assert.Equal(direct.B, replayed.B);
        Assert.Equal(direct.C, replayed.C);
        Assert.Equal(direct.D, replayed.D);
        Assert.Equal(direct.E, replayed.E);
    }

    [YamlPolymorphic(Discriminator = YamlDiscriminatorKind.Tag)]
    [YamlDerivedType(typeof(Cat), "cat")]
    [YamlDerivedType(typeof(Dog), "!dog")]
    private abstract class Animal;

    private sealed class Cat : Animal
    {
        public int Lives { get; set; }
    }

    private sealed class Dog : Animal
    {
        public string Name { get; set; } = "";
    }

    private sealed class Zoo
    {
        public List<Animal> Animals { get; set; } = [];
    }

    [Fact]
    public void Tag_discriminated_polymorphism_reads_the_same_from_a_document()
    {
        string yaml = "animals:\n  - !cat {lives: 9}\n  - !dog {name: Rex}\n";
        Zoo direct = YamlSerializer.Deserialize<Zoo>(yaml)!;
        using YamlDocument doc = YamlDocument.Parse(yaml);
        Zoo replayed = doc.Root.Deserialize<Zoo>()!;

        Assert.Equal(2, replayed.Animals.Count);
        Assert.Equal(9, Assert.IsType<Cat>(replayed.Animals[0]).Lives);
        Assert.Equal("Rex", Assert.IsType<Dog>(replayed.Animals[1]).Name);
        Assert.Equal(direct.Animals.Select(a => a.GetType()), replayed.Animals.Select(a => a.GetType()));
    }

    // ---- merge keys into typed collections ----

    private static string MergeChain(int n)
    {
        var sb = new StringBuilder();
        sb.Append("a0: &a0 {k0: 0}\n");
        for (int i = 1; i < n; i++)
        {
            sb.Append('a').Append(i).Append(": &a").Append(i).Append(" {<<: *a").Append(i - 1).Append(", k").Append(i).Append(": ").Append(i).Append("}\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void Merge_keys_read_into_a_typed_dictionary_are_bounded_like_the_generic_load()
    {
        string yaml = MergeChain(3_000);
        var options = new YamlSerializerOptions { MergeKeys = true };

        // Load once with the generic path to know the limit is what stops it.
        Assert.Equal(YamlErrorCategory.Limit, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml, options)).Category);

        long before = GC.GetAllocatedBytesForCurrentThread();
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Dictionary<string, object?>>(yaml, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        // The entries copied by merges used to be free: 8 GB of dictionaries were built before the limit hit.
        Assert.True(allocated < 300_000_000, $"allocated {allocated:N0} bytes; {ex.Message}");
    }

    [Fact]
    public void Merge_keys_into_a_typed_dictionary_still_merge()
    {
        var options = new YamlSerializerOptions { MergeKeys = true };
        Dictionary<string, Dictionary<string, int>> v = YamlSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
            "base: &b {x: 1, y: 2}\nuse: {<<: *b, y: 3, z: 4}\n", options)!;
        Assert.Equal(new Dictionary<string, int> { ["x"] = 1, ["y"] = 3, ["z"] = 4 }, v["use"]);
    }
}

/// <summary>The cost of try-reads that miss, measured as processor time in a group that runs alone.</summary>
[Collection(TimingGroup.Name)]
public class TryReadMissCostTests
{
    private static double SecondsPerMiss(int count)
    {
        string yaml = "items:\n" + string.Concat(Enumerable.Repeat("  - 99999999999\n", count));
        using YamlDocument doc = YamlDocument.Parse(yaml);

        // Indexing a sequence walks to the item, which is not what is measured here: take the views first.
        YamlNode[] items = doc.Root["items"].Items.ToArray();
        for (int i = 0; i < 50; i++)
        {
            Assert.False(items[i].TryGetValue(out int _));
        }

        double best = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            CpuClock clock = CpuClock.Start();
            for (int i = 0; i < count; i++)
            {
                Assert.False(items[i].TryGetValue(out int _));
            }

            best = Math.Min(best, clock.Elapsed.TotalSeconds);
        }

        return best / count;
    }

    [Fact]
    public void A_try_read_of_an_out_of_range_number_costs_the_same_in_a_small_and_a_large_document()
    {
        // The kind check cannot rule these out, so the number converter refuses them. A located error looks up
        // the line, column and path in the text before the node, which made each miss grow with the document.
        double small = SecondsPerMiss(2_000);
        double large = SecondsPerMiss(8_000);
        Assert.True(large < small * 2.5, $"{small * 1e6:N1} us per miss at 2,000 items, {large * 1e6:N1} us at 8,000");
    }
}
