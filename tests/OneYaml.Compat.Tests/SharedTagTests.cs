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
}
