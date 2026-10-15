using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using CmS = global::OneYaml.Compat.YamlDotNet.Serialization;
using Core = global::OneYaml.Compat.YamlDotNet.Core;
using Rl = global::YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// Output that must read back as what was written, and walks that must stop on hostile input.
public class BoundsAndKeysTests
{
    // ---- keys too long to be implicit ----

    [Fact]
    public void A_key_longer_than_an_implicit_key_is_written_after_a_question_mark_and_reads_back()
    {
        string key = new('k', 1100);
        string yaml = new CmS.Serializer().Serialize(new Dictionary<string, int> { [key] = 1 });

        Assert.StartsWith("? " + key, yaml, StringComparison.Ordinal);
        Dictionary<string, int>? back = global::OneYaml.YamlSerializer.Deserialize<Dictionary<string, int>>(yaml);
        Assert.Equal(new Dictionary<string, int> { [key] = 1 }, back);
    }

    [Fact]
    public void A_long_multi_line_key_added_to_a_loaded_stream_survives_a_save()
    {
        string key = new string('k', 600) + "\n" + new string('k', 600);
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader("a: 1\n"));
        ((Cm.YamlMappingNode)stream.Documents[0].RootNode).Add(key, "v");
        var w = new StringWriter();
        stream.Save(w, false);

        Dictionary<string, string>? back = global::OneYaml.YamlSerializer.Deserialize<Dictionary<string, string>>(w.ToString());
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", [key] = "v" }, back);
    }

    // ---- generated anchors ----

    [Fact]
    public void Generated_anchor_names_skip_the_names_the_model_already_uses()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader("a: &o0 x\n"));
        var root = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        Cm.YamlNode named = root.Children[new Cm.YamlScalarNode("a")];
        var shared = new Cm.YamlSequenceNode("s");
        root.Children.Clear();
        root.Children.Add("b", shared);
        root.Children.Add("a", named);
        root.Children.Add("c", shared);
        var w = new StringWriter();
        stream.Save(w);

        var again = new Rl.YamlStream();
        again.Load(new StringReader(w.ToString()));
        var map = (Rl.YamlMappingNode)again.Documents[0].RootNode;
        Assert.Equal("x", ((Rl.YamlScalarNode)map.Children[new Rl.YamlScalarNode("a")]).Value);
        Assert.Equal("s", ((Rl.YamlScalarNode)((Rl.YamlSequenceNode)map.Children[new Rl.YamlScalarNode("c")]).Children[0]).Value);
    }

    // ---- recursion in the typed path ----

    private static string AliasChain(int n)
    {
        var sb = new StringBuilder("junk:\n- &a0 [x]\n");
        for (int i = 1; i < n; i++)
        {
            sb.Append("- &a").Append(i).Append(" [*a").Append(i - 1).Append("]\n");
        }

        sb.Append("r: *a").Append(n - 1).Append('\n');
        return sb.ToString();
    }

    public sealed class Holder
    {
        [CmS.YamlMember(Alias = "r")]
        public object? R { get; set; }
    }

    [Theory]
    [InlineData(1_500)]
    [InlineData(5_000)]
    [InlineData(200_000)]
    public void A_huge_recursion_setting_never_goes_past_the_depth_limit(int n)
    {
        CmS.IDeserializer d = new CmS.DeserializerBuilder().IgnoreUnmatchedProperties().WithMaximumRecursion(1_000_000).Build();
        Assert.ThrowsAny<Core.YamlException>(() => d.Deserialize<Holder>(AliasChain(n)));
    }

    [Fact]
    public void A_huge_recursion_setting_never_goes_past_the_depth_limit_on_write()
    {
        var head = new List<object>();
        List<object> current = head;
        for (int i = 0; i < 5_000; i++)
        {
            var next = new List<object>();
            current.Add(next);
            current = next;
        }

        CmS.ISerializer s = new CmS.SerializerBuilder().WithMaximumRecursion(1_000_000).Build();
        Assert.ThrowsAny<Core.YamlException>(() => s.Serialize(head));
    }

    // ---- walks over the representation model ----

    private static string Laughs(int levels, int scalarLength)
    {
        var sb = new StringBuilder();
        sb.Append("lv0: &lv0 [");
        for (int i = 0; i < 10; i++)
        {
            sb.Append(i > 0 ? ", " : "").Append(new string('x', scalarLength));
        }

        sb.Append("]\n");
        for (int level = 1; level <= levels; level++)
        {
            sb.Append("lv").Append(level).Append(": &lv").Append(level).Append(" [");
            for (int i = 0; i < 10; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append("*lv").Append(level - 1);
            }

            sb.Append("]\n");
        }

        return sb.ToString();
    }

    private sealed class Counting : Cm.YamlVisitorBase
    {
        public long Scalars;

        public override void Visit(Cm.YamlScalarNode scalar) => Scalars++;
    }

    [Fact]
    public void A_visitor_over_a_billion_laughs_model_stops_with_an_exception()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(Laughs(8, 1)));
        var v = new Counting();
        Assert.ThrowsAny<Core.YamlException>(() => stream.Accept(v));
        Assert.True(v.Scalars < 2_000_000);
    }

    [Fact]
    public void A_visitor_over_a_deep_alias_chain_stops_before_the_stack_does()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(AliasChain(45_000)));
        var v = new Counting();
        Exception? error = null;
        var t = new Thread(() => error = Record.Exception(() => stream.Accept(v)), 1024 * 1024);
        t.Start();
        t.Join();
        Assert.IsAssignableFrom<Core.YamlException>(error);
    }

    [Fact]
    public void ToString_of_a_model_that_prints_far_larger_than_its_text_stops_with_an_exception()
    {
        var stream = new Cm.YamlStream();
        // The last level unrolls to about 400 million characters, some 800 MB of output if nothing stopped the walk.
        stream.Load(new StringReader(Laughs(4, 4000)));
        Cm.YamlNode root = stream.Documents[0].RootNode;

        // Count what this walk allocates on this thread, not the heap of the process, which other tests in it change.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<Core.YamlException>(() => root.ToString());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The text limit is 32 million characters, about 64 MB of builder chunks.
        Assert.True(allocated < 200L * 1024 * 1024, allocated.ToString());
    }

    [Fact]
    public void Equals_on_models_that_hold_far_more_text_than_their_source_stops_with_an_exception()
    {
        var first = new Cm.YamlStream();
        first.Load(new StringReader(Laughs(3, 4000)));
        var second = new Cm.YamlStream();
        second.Load(new StringReader(Laughs(3, 4000)));
        Assert.ThrowsAny<Core.YamlException>(() => first.Documents[0].RootNode.Equals(second.Documents[0].RootNode));
    }

    [Fact]
    public void Hashing_a_shared_structure_visits_each_distinct_node_once_and_agrees_with_equality()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(Laughs(5, 1)));
        var root = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        Cm.YamlNode big = root.Children[new Cm.YamlScalarNode("lv5")];

        // lv5 is a sequence of ten references to lv4, and so on down to a sequence of scalars: six sequences and the
        // scalar nodes of the lowest one, however many times the tree repeats them.
        int visits = 0;
        _ = Cm.YamlNode.StructureHash(big, ref visits);
        Assert.True(visits <= 6 + 10, visits.ToString());

        var a = new Cm.YamlSequenceNode("x", new Cm.YamlSequenceNode("y"));
        var b = new Cm.YamlSequenceNode("x", new Cm.YamlSequenceNode("y"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Hashing_looks_at_the_whole_structure_not_only_the_first_nodes()
    {
        static Cm.YamlSequenceNode Key(int last)
        {
            var seq = new Cm.YamlSequenceNode();
            for (int i = 0; i < 40; i++)
            {
                seq.Add("0");
            }

            seq.Add(last.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return seq;
        }

        Assert.NotEqual(Key(1).GetHashCode(), Key(2).GetHashCode());
        Assert.Equal(Key(7).GetHashCode(), Key(7).GetHashCode());
    }

    [Fact]
    public void Complex_keys_that_agree_on_a_long_prefix_do_not_all_hash_alike()
    {
        // Keys equal in their first 32 nodes and different after: one bucket for all of them made loading quadratic.
        const int Keys = 8000;
        var sb = new StringBuilder();
        for (int i = 0; i < Keys; i++)
        {
            sb.Append("? [").Append(string.Join(", ", Enumerable.Repeat("0", 32))).Append(", ").Append(i).Append("]\n: v\n");
        }

        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(sb.ToString()));
        var map = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal(Keys, map.Children.Count);
        var hashes = new HashSet<int>();
        foreach (KeyValuePair<Cm.YamlNode, Cm.YamlNode> pair in map.Children)
        {
            hashes.Add(pair.Key.GetHashCode());
        }

        // A 32-bit hash can meet by chance; a handful of meetings is fine, thousands is the collision.
        Assert.True(hashes.Count > Keys - 10, hashes.Count.ToString());
    }

    [Fact]
    public void Loading_a_mapping_whose_keys_are_large_shared_structures_charges_each_key_by_its_distinct_nodes()
    {
        var sb = new StringBuilder(Laughs(4, 1));
        sb.Append("m:\n");
        for (int i = 0; i < 3000; i++)
        {
            sb.Append("  ? [*lv4, ").Append(i).Append("]\n  : x\n");
        }

        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(sb.ToString()));
        var root = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        var m = (Cm.YamlMappingNode)root.Children[new Cm.YamlScalarNode("m")];
        Assert.Equal(3000, m.Children.Count);
        foreach (Cm.YamlNode key in m.Children.Keys.Take(50))
        {
            int visits = 0;
            _ = Cm.YamlNode.StructureHash(key, ref visits);
            Assert.True(visits <= 20, visits.ToString());
        }
    }

    [Fact]
    public void Loading_complex_keys_that_share_one_large_anchor_takes_time_in_proportion_to_the_input()
    {
        var sb = new StringBuilder("base: &a [");
        for (int i = 0; i < 100_000; i++)
        {
            sb.Append(i).Append(i < 99_999 ? "," : "]\n");
        }

        for (int i = 0; i < 1000; i++)
        {
            sb.Append("? [*a, ").Append(i).Append("]\n: x\n");
        }

        var stream = new Cm.YamlStream();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        stream.Load(new StringReader(sb.ToString()));
        watch.Stop();

        Assert.Equal(1001, ((Cm.YamlMappingNode)stream.Documents[0].RootNode).Children.Count);
        // Each key walked the whole anchor again, which took many seconds.
        Assert.True(watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds.ToString());
    }

    [Fact]
    public void Equal_models_that_contain_themselves_hash_alike()
    {
        var c = new Cm.YamlSequenceNode();
        var d = new Cm.YamlSequenceNode();
        c.Add(d);
        d.Add(c);
        var d2 = new Cm.YamlSequenceNode();
        d2.Add(c);
        var x = new Cm.YamlSequenceNode(c, d);
        var y = new Cm.YamlSequenceNode(c, d2);

        Assert.True(x.Equals(y));
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        Assert.True(d.Equals(d2));
        Assert.Equal(d.GetHashCode(), d2.GetHashCode());
    }

    [Fact]
    public void Hashing_a_model_past_the_node_limit_returns_a_hash_instead_of_throwing()
    {
        var big = new Cm.YamlSequenceNode();
        for (int i = 0; i < 1_100_000; i++)
        {
            big.Add(new Cm.YamlScalarNode("v"));
        }

        var set = new HashSet<Cm.YamlNode> { big };
        Assert.Contains(big, set);
    }

    // ---- a hash depends on structure alone ----

    // The large models of these tests are garbage when they end; other tests in this class look at the size of the heap.
    private static void ReleaseMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string SharedKeyDocument(int items)
    {
        string numbers = string.Join(',', Enumerable.Range(0, items));
        return $"base: &a [{numbers}]\nm:\n  ? [*a]\n  : 1\n  ? [*a, [{numbers}]]\n  : 2\n";
    }

    [Fact]
    public void A_loaded_mapping_finds_its_own_keys_when_one_of_them_is_larger_than_a_million_distinct_nodes()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(SharedKeyDocument(520_000)));
        var root = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        var m = (Cm.YamlMappingNode)root.Children[new Cm.YamlScalarNode("m")];
        Assert.Equal(2, m.Children.Count);
        int found = 0;
        foreach (Cm.YamlNode key in m.Children.Keys)
        {
            Assert.True(m.Children.ContainsKey(key));
            Assert.NotNull(m[key]);
            found++;
        }

        Assert.Equal(2, found);
        ReleaseMemory();
    }

    [Fact]
    public void Equal_models_hash_alike_however_they_share_nodes()
    {
        var shared = new Cm.YamlSequenceNode();
        var copy = new Cm.YamlSequenceNode();
        for (int i = 0; i < 600_000; i++)
        {
            shared.Add(new Cm.YamlScalarNode("v" + i));
            copy.Add(new Cm.YamlScalarNode("v" + i));
        }

        // The first holds the same 600,000 nodes twice, the second holds 1,200,000 distinct ones.
        var once = new Cm.YamlSequenceNode(shared, shared);
        var twice = new Cm.YamlSequenceNode(shared, copy);
        Assert.True(once.Equals(twice));
        Assert.Equal(once.GetHashCode(), twice.GetHashCode());
        ReleaseMemory();
    }

    [Fact]
    public void A_hash_taken_after_a_load_matches_the_one_the_load_used()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader("? [a, [b, c], {d: e}]\n: 1\n? [a, [b, c]]\n: 2\n"));
        var map = (Cm.YamlMappingNode)stream.Documents[0].RootNode;
        var rebuilt = new Cm.YamlSequenceNode("a", new Cm.YamlSequenceNode("b", "c"), new Cm.YamlMappingNode("d", "e"));
        Assert.Equal("1", ((Cm.YamlScalarNode)map[rebuilt]).Value);
        Assert.Equal(rebuilt.GetHashCode(), map.Children[0].Key.GetHashCode());
    }

    [Fact]
    public void A_hash_of_a_very_deep_model_neither_throws_nor_overflows_the_stack()
    {
        var inner = new Cm.YamlSequenceNode();
        Cm.YamlSequenceNode top = inner;
        for (int i = 0; i < 100_000; i++)
        {
            var outer = new Cm.YamlSequenceNode();
            outer.Add(top);
            top = outer;
        }

        _ = top.GetHashCode();
        Assert.Equal(top.GetHashCode(), top.GetHashCode());
    }

    private sealed class WatchingParser(Core.IParser inner, Action whileLoading) : Core.IParser
    {
        public Core.Events.ParsingEvent? Current => inner.Current;

        public bool MoveNext()
        {
            whileLoading();
            return inner.MoveNext();
        }
    }

    [Fact]
    public void A_parser_that_hashes_nodes_while_a_stream_loads_sees_no_state_of_the_load()
    {
        var hashes = new List<(int Before, int After)>();
        var parser = new WatchingParser(new Core.Parser(new StringReader("? [a, b]\n: 1\n? [c]\n: 2\n")), () =>
        {
            var node = new Cm.YamlSequenceNode("x");
            int before = node.GetHashCode();
            node.Add("y");
            hashes.Add((before, node.GetHashCode()));
        });

        var stream = new Cm.YamlStream();
        stream.Load(parser);
        Assert.Equal(2, ((Cm.YamlMappingNode)stream.Documents[0].RootNode).Children.Count);
        Assert.NotEmpty(hashes);
        Assert.All(hashes, h => Assert.NotEqual(h.Before, h.After));
    }

    private sealed class ScriptedParser(IEnumerable<Core.Events.ParsingEvent> events) : Core.IParser
    {
        private readonly IEnumerator<Core.Events.ParsingEvent> _events = events.GetEnumerator();

        public Core.Events.ParsingEvent? Current { get; private set; }

        public bool MoveNext()
        {
            bool more = _events.MoveNext();
            Current = more ? _events.Current : null;
            return more;
        }
    }

    [Fact]
    public void A_key_that_points_back_at_a_collection_still_being_built_loads_from_a_custom_parser()
    {
        var anchor = new Core.AnchorName("a");
        var none = Core.TagName.Empty;
        Core.Events.ParsingEvent[] events =
        [
            new Core.Events.StreamStart(),
            new Core.Events.DocumentStart(),
            new Core.Events.SequenceStart(anchor, none, true, Core.Events.SequenceStyle.Block),
            new Core.Events.MappingStart(Core.AnchorName.Empty, none, true, Core.Events.MappingStyle.Block),
            new Core.Events.SequenceStart(Core.AnchorName.Empty, none, true, Core.Events.SequenceStyle.Flow),
            new Core.Events.AnchorAlias(anchor),
            new Core.Events.SequenceEnd(),
            new Core.Events.Scalar("1"),
            new Core.Events.MappingEnd(),
            new Core.Events.SequenceEnd(),
            new Core.Events.DocumentEnd(true),
            new Core.Events.StreamEnd(),
        ];

        var stream = new Cm.YamlStream();
        stream.Load(new ScriptedParser(events));
        Assert.Single(stream.Documents);
        var root = (Cm.YamlSequenceNode)stream.Documents[0].RootNode;
        Assert.Single(root.Children);
    }
}
