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
        stream.Load(new StringReader(Laughs(3, 4000)));
        // The walk stops at its node and text budgets, which is what bounds the time and the memory it can take.
        Assert.ThrowsAny<Core.YamlException>(() => stream.Documents[0].RootNode.ToString());
        Assert.True(GC.GetTotalMemory(false) < 1L << 30);
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
}
