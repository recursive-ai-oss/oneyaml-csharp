using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

public class SharedTagTests
{
    [Fact]
    public void Nodes_that_write_one_tag_do_not_each_hold_the_resolved_string()
    {
        string prefix = new('a', 100_000);
        string yaml = "%TAG !e! tag:x,2000:" + prefix + "\n---\n" + string.Concat(Enumerable.Repeat("- !e!x a\n- !e!y b\n", 1000));
        var warm = new Cm.YamlStream();
        warm.Load(new StringReader(yaml));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(yaml));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 2,000 nodes with a 100,000-character prefix held one 200 KB string each: 400 MB.
        Assert.True(allocated < 20_000_000, $"loading allocated {allocated:N0} bytes");

        var items = ((Cm.YamlSequenceNode)stream.Documents[0].RootNode).Children;
        Assert.Equal("tag:x,2000:" + prefix + "x", items[0].Tag.Value);
        Assert.Equal("tag:x,2000:" + prefix + "y", items[1].Tag.Value);
        Assert.Same(items[0].Tag.Value, items[2].Tag.Value);
    }

    [Fact]
    public void A_tag_set_by_hand_replaces_the_loaded_one()
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader("- !!str 1\n"));
        Cm.YamlNode node = ((Cm.YamlSequenceNode)stream.Documents[0].RootNode).Children[0];
        Assert.Equal("tag:yaml.org,2002:str", node.Tag.Value);
        node.Tag = new global::OneYaml.Compat.YamlDotNet.Core.TagName("!mine");
        Assert.Equal("!mine", node.Tag.Value);
        node.Tag = default;
        Assert.True(node.Tag.IsEmpty);
    }

    // Distinct suffixes under one long prefix: a resolved string per tag would cost the prefix for every node.
    private static string DistinctTags(int prefixLength, int count) =>
        "%TAG !e! tag:x,2000:" + new string('a', prefixLength) + "\n---\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"- !e!x{i} a\n"));

    private static Cm.YamlStream Load(string yaml)
    {
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(yaml));
        return stream;
    }

    [Fact]
    public void Hashing_and_comparing_nodes_does_not_build_their_tags()
    {
        string yaml = DistinctTags(100_000, 2_000);
        var first = (Cm.YamlSequenceNode)Load(yaml).Documents[0].RootNode;
        var second = (Cm.YamlSequenceNode)Load(yaml).Documents[0].RootNode;
        var hashes = new HashSet<int>();
        foreach (Cm.YamlNode node in first.Children)
        {
            hashes.Add(node.GetHashCode());
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int equal = 0;
        for (int i = 0; i < first.Children.Count; i++)
        {
            equal += first.Children[i].Equals(second.Children[i]) ? 1 : 0;
            equal += first.Children[i].GetHashCode() == second.Children[i].GetHashCode() ? 1 : 0;
            equal += first.Children[i].Equals(second.Children[(i + 1) % second.Children.Count]) ? 100 : 0;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2 * first.Children.Count, equal);
        Assert.Equal(first.Children.Count, hashes.Count);

        // 2,000 tags of 200 KB each would be 400 MB.
        Assert.True(allocated < 20_000_000, $"comparing allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Saving_an_unchanged_stream_does_not_build_the_tags()
    {
        string yaml = DistinctTags(100_000, 2_000);
        Cm.YamlStream stream = Load(yaml);
        var output = new StringWriter();
        stream.Save(output, false);
        Assert.Equal(yaml, output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));

        long before = GC.GetAllocatedBytesForCurrentThread();
        stream.Save(new StringWriter(), false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The text is written as it was read: a copy of it, not a copy per tag.
        Assert.True(allocated < 20_000_000, $"saving allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Tags_that_agree_in_full_text_compare_equal_however_they_were_written()
    {
        var a = (Cm.YamlSequenceNode)Load("%TAG !e! tag:x,2000:\n---\n- !e!x a\n- !<tag:x,2000:x> a\n- !<tag:x,2000:y> a\n").Documents[0].RootNode;

        Assert.True(a.Children[0].Equals(a.Children[1]));
        Assert.Equal(a.Children[0].GetHashCode(), a.Children[1].GetHashCode());
        Assert.False(a.Children[0].Equals(a.Children[2]));
        a.Children[2].Tag = new global::OneYaml.Compat.YamlDotNet.Core.TagName("tag:x,2000:x");
        Assert.True(a.Children[0].Equals(a.Children[2]));
        Assert.Equal(a.Children[0].GetHashCode(), a.Children[2].GetHashCode());
    }

    [Fact]
    public void An_unresolved_tag_is_cut_in_the_message()
    {
        var deserializer = new global::OneYaml.Compat.YamlDotNet.Serialization.DeserializerBuilder().Build();
        string yaml = "!<tag:x,2000:" + new string('a', 100_000) + "> {a: 1}\n";

        Exception error = Assert.ThrowsAny<Exception>(() => deserializer.Deserialize<object>(yaml));

        Assert.IsAssignableFrom<global::OneYaml.Compat.YamlDotNet.Core.YamlException>(error);
        Assert.True(error.Message.Length < 1_000, $"the message has {error.Message.Length} characters");
    }
}
