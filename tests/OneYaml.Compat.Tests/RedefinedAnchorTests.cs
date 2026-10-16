using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using Rl = global::YamlDotNet.RepresentationModel;

namespace OneYaml.Compat.Tests;

// An alias means the node whose anchor was written last before it, even when an enclosing node with the same name ends later.
public class RedefinedAnchorTests
{
    [Fact]
    public void The_model_resolves_a_redefined_anchor_as_the_real_library_does()
    {
        const string yaml = "a: &b\n se: &b\n   1\ncopy: *b\n";
        var r = new Rl.YamlStream();
        r.Load(new StringReader(yaml));
        var c = new Cm.YamlStream();
        c.Load(new StringReader(yaml));
        Assert.Equal(RepresentationTests.Shape(r), RepresentationTests.Shape(c));
        var copy = ((Cm.YamlMappingNode)c.Documents[0].RootNode).Children[new Cm.YamlScalarNode("copy")];
        Assert.IsType<Cm.YamlScalarNode>(copy);
    }
}
