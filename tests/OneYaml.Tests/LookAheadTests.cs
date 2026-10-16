using System.Text;
using Xunit;

namespace OneYaml.Tests;

// Looking ahead for a discriminator key that is not first: what it costs in memory and in the alias budget, and the
// verbatim-tag rule the reader checks on the way.
public class LookAheadTests
{
    private static readonly ApiConsistencyContext s_context = new();

    // 0 = reflection, 1 = generated metadata.
    public static TheoryData<int> Paths => new() { 0, 1 };

    private static WideShape? Read(int path, string yaml, YamlSerializerOptions? options = null) => path == 0
        ? YamlSerializer.Deserialize<WideShape>(yaml, options)
        : YamlSerializer.Deserialize(yaml, (YamlTypeInfo<WideShape>)(options is null ? s_context : new ApiConsistencyContext(options)).GetTypeInfo(typeof(WideShape))!);

    private static string WithBigValue(int items, bool keyLast)
    {
        var sb = new StringBuilder("{");
        if (!keyLast)
        {
            sb.Append("$type: circle, ");
        }

        sb.Append("big: [").Append(string.Join(',', Enumerable.Repeat("1", items))).Append("], radius: 3");
        if (keyLast)
        {
            sb.Append(", $type: circle");
        }

        return sb.Append('}').ToString();
    }

    private static long Allocated(Action run)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Each path reads its own size of value, and each is read once: a log that went back to the shared pool would be
    // handed out again to the next read of the same size, and that read would not be counted.
    [Theory]
    [InlineData(0, 1_300_000)]
    [InlineData(1, 700_000)]
    public void Looking_past_a_large_value_costs_a_few_dozen_bytes_for_each_node_it_passes(int path, int items)
    {
        string last = WithBigValue(items, keyLast: true);
        string first = WithBigValue(items, keyLast: false);
        long early = Allocated(() => Read(path, first));
        WideShape? shape = null;
        long late = Allocated(() => shape = Read(path, last));

        Assert.Equal(3, ((WideCircle)shape!).Radius);

        // The log of passed events held about 80 bytes for each node while it grew; it now holds 24 and never copies.
        Assert.InRange(late - early, 0, 32L * items);
        Assert.True(late - early > 16L * items, "The look-ahead log has to be allocated for the nodes it passes.");
    }

    [Theory, MemberData(nameof(Paths))]
    public void The_log_of_a_look_ahead_leaves_no_large_array_in_the_shared_pool(int path)
    {
        const int items = 200_000;
        Read(path, WithBigValue(items, keyLast: true));

        // The log of 200,000 nodes needed an array of this many events. Asking the pool for one of that size has to allocate:
        // nothing of the kind was given back.
        const int logSize = 1 << 18;
        long before = GC.GetAllocatedBytesForCurrentThread();
        LookEvent[] rented = System.Buffers.ArrayPool<LookEvent>.Shared.Rent(logSize);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(rented); // Not given back: the next case asks for the same size.

        Assert.True(allocated >= logSize, $"The shared pool already held an array of {logSize} events.");
    }

    private static string Fan()
    {
        var sb = new StringBuilder("a: &a [1,1,1,1,1,1,1,1,1,1]\n");
        string previous = "a";
        foreach (string name in new[] { "b", "c", "d", "e" })
        {
            sb.Append(name).Append(": &").Append(name).Append(" [").Append(string.Join(",", Enumerable.Repeat("*" + previous, 10))).Append("]\n");
            previous = name;
        }

        return sb.ToString();
    }

    [Theory, MemberData(nameof(Paths))]
    public void A_key_that_comes_last_after_aliases_binds_where_the_same_document_with_the_key_first_does(int path)
    {
        // The aliases expand to about 111,000 nodes, which the budget allows once. Passing them again while looking ahead for
        // the key is not a second expansion.
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted with { AliasNodeFloor = 150_000, AliasNodeFactor = 1 } };
        string first = "$type: circle\nlabel: x\n" + Fan();
        string last = "label: x\n" + Fan() + "$type: circle\n";

        Assert.IsType<WideCircle>(Read(path, first, options));
        Assert.IsType<WideCircle>(Read(path, last, options));
    }

    // Nested mappings whose discriminator comes last each look ahead over the same logged expansion. Each look-ahead
    // passes it again, so each is charged, and the total stays within the budget the alias expansion is held to.
    private static string NestedKeyLast(int depth, bool keyLast)
    {
        string tail = keyLast ? "$type: circle" : "";
        var sb = new StringBuilder("{ ");
        sb.Append(keyLast ? "" : "$type: circle, ");
        sb.Append("fill: &a0 [x], other: &a1 [").Append(string.Join(",", Enumerable.Repeat("*a0", 10)));
        sb.Append("], more: &a2 [").Append(string.Join(",", Enumerable.Repeat("*a1", 10)));
        sb.Append("], again: &a3 [").Append(string.Join(",", Enumerable.Repeat("*a2", 10))).Append("], inner: ");
        for (int i = 0; i < depth; i++)
        {
            sb.Append(keyLast ? "{ label: x, inner: " : "{ $type: circle, label: x, inner: ");
        }

        sb.Append(keyLast ? "{ fill: *a3, $type: circle }" : "{ $type: circle, fill: *a3 }");
        for (int i = 0; i < depth; i++)
        {
            sb.Append(keyLast ? ", $type: circle }" : " }");
        }

        return sb.Append(keyLast ? ", " + tail + " }" : " }").ToString();
    }

    [Theory, MemberData(nameof(Paths))]
    public void Nested_look_aheads_over_one_alias_expansion_are_charged_each_time_they_pass_it(int path)
    {
        // One expansion is 2,111 nodes. Held to 30,000, it is read once, but 40 look-aheads over it are not.
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted with { MaxDepth = 200, AliasNodeFloor = 30_000, AliasNodeFactor = 1 } };

        Assert.IsType<WideCircle>(Read(path, NestedKeyLast(40, keyLast: false), options));
        Assert.IsType<WideCircle>(Read(path, NestedKeyLast(8, keyLast: true), options));

        YamlException ex = Assert.Throws<YamlException>(() => Read(path, NestedKeyLast(40, keyLast: true), options));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
    }

    [Theory, MemberData(nameof(Paths))]
    public void Ends_of_collections_passed_while_looking_ahead_are_not_charged(int path)
    {
        // 30,000 empty mappings: 30,000 starts to count, and the same number of ends that cost nothing.
        string many = "label: x\nitems: [" + string.Join(',', Enumerable.Repeat("{}", 30_000)) + "]\n$type: circle\n";
        var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted with { AliasNodeFloor = 35_000, AliasNodeFactor = 1 } };

        Assert.IsType<WideCircle>(Read(path, many, options));
    }

    [Theory]
    [InlineData("- !<!> foo\n")]
    [InlineData("!<!> foo\n")]
    [InlineData("a: !<!>\n")]
    public void A_verbatim_tag_that_is_just_a_bang_is_a_syntax_error(string yaml)
    {
        YamlException fromReader = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml));
        Assert.Equal(YamlErrorCategory.Syntax, fromReader.Category);

        YamlException fromDocument = Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        Assert.Equal(YamlErrorCategory.Syntax, fromDocument.Category);
    }

    [Fact]
    public void Other_verbatim_tags_that_begin_with_a_bang_stay_valid()
    {
        using YamlDocument doc = YamlDocument.Parse("a: !<!local> x\nb: !<!!foo> y\nc: !<tag:example.com,2026:t> z\n");

        Assert.Equal("!local", doc.Root["a"].Tag);
        Assert.Equal("!!foo", doc.Root["b"].Tag);
    }
}
