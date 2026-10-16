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

// An alias inside the node its own anchor names is a cycle. Neither compat package reads it as an older definition of
// the same name; both refuse it.
public class AliasInsideItsOwnNodeTests
{
    private const string Yaml = "- &a 1\n- &a [*a]\n";

    [Fact]
    public void The_YamlDotNet_compat_model_and_deserializer_refuse_it()
    {
        var stream = new Cm.YamlStream();
        Assert.ThrowsAny<global::OneYaml.Compat.YamlDotNet.Core.YamlException>(() => stream.Load(new StringReader(Yaml)));

        var deserializer = new global::OneYaml.Compat.YamlDotNet.Serialization.Deserializer();
        Assert.ThrowsAny<global::OneYaml.Compat.YamlDotNet.Core.YamlException>(() => deserializer.Deserialize<List<object>>(Yaml));
    }

    [Fact]
    public void The_SharpYaml_compat_serializer_refuses_it()
    {
        Assert.ThrowsAny<Exception>(() => global::OneYaml.Compat.SharpYaml.YamlSerializer.Deserialize<List<object>>(Yaml));
    }
}
