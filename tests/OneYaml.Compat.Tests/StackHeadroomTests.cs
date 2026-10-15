using System.Text;
using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.RepresentationModel;
using CmS = global::OneYaml.Compat.YamlDotNet.Serialization;
using Cs = global::OneYaml.Compat.SharpYaml;

namespace OneYaml.Compat.Tests;

public sealed class HeadroomChain
{
    public HeadroomChain? C { get; set; }

    public string? S { get; set; }
}

// The default depth limit has to be reachable on a 1 MB stack, the default of a Windows thread, through the
// entry points of both compat packages.
public class CompatStackHeadroomTests
{
    private static int Levels => global::OneYaml.YamlLimits.Default.MaxDepth - 1;

    private static void OnMegabyteStack(Action run)
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static string BlockChain(int levels)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < levels; i++)
        {
            sb.Append(' ', i).Append("C:\n");
        }

        sb.Append(' ', levels).Append("S: \"a\\tb \\u00e9\"\n");
        return sb.ToString();
    }

    private static HeadroomChain Chain(int levels)
    {
        var node = new HeadroomChain { S = "x" };
        for (int i = 1; i < levels; i++)
        {
            node = new HeadroomChain { C = node };
        }

        return node;
    }

    private static int Length(HeadroomChain? node)
    {
        int n = 0;
        for (; node is not null; node = node.C)
        {
            n++;
        }

        return n;
    }

    private sealed class Counting : Cm.YamlVisitorBase
    {
        public int Scalars;

        public override void Visit(Cm.YamlScalarNode scalar) => Scalars++;
    }

    [Fact]
    public void YamlDotNet_deserializer_reaches_the_depth_limit()
    {
        // This recursion limit counts the scalar at the bottom as a level, as the real library does.
        int levels = Levels - 1;
        string yaml = BlockChain(levels);
        OnMegabyteStack(() =>
        {
            CmS.IDeserializer d = new CmS.DeserializerBuilder().WithMaximumRecursion(Levels + 1).Build();
            Assert.Equal(levels + 1, Length(d.Deserialize<HeadroomChain>(yaml)));
            Assert.NotNull(d.Deserialize<object>(yaml));
        });
    }

    [Fact]
    public void YamlDotNet_serializer_reaches_the_depth_limit()
    {
        HeadroomChain chain = Chain(Levels);
        OnMegabyteStack(() =>
        {
            string yaml = new CmS.SerializerBuilder().WithMaximumRecursion(Levels + 1).Build().Serialize(chain);
            Assert.Equal(Levels, Length(global::OneYaml.YamlSerializer.Deserialize<HeadroomChain>(yaml, new global::OneYaml.YamlSerializerOptions { PropertyNameCaseInsensitive = true })));
        });
    }

    [Fact]
    public void YamlDotNet_representation_model_reaches_the_depth_limit()
    {
        string yaml = BlockChain(Levels);
        OnMegabyteStack(() =>
        {
            var stream = new Cm.YamlStream();
            stream.Load(new StringReader(yaml));
            var counting = new Counting();
            stream.Accept(counting);
            Assert.Equal(Levels + 2, counting.Scalars);
            Assert.NotEmpty(stream.Documents[0].RootNode.ToString());
            var writer = new StringWriter();
            stream.Save(writer, false);
            Assert.Equal(yaml.Length > 0, writer.ToString().Length > 0);
        });
    }

    [Fact]
    public void Printing_and_saving_a_deep_model_on_a_small_stack_end_in_an_exception()
    {
        // The depth limit assumes a large stack; with less, the walk has to stop on its own and not overflow.
        var stream = new Cm.YamlStream();
        stream.Load(new StringReader(BlockChain(Levels)));
        Cm.YamlNode root = stream.Documents[0].RootNode;
        var thrown = new List<Exception?>();
        foreach (Action<Cm.YamlNode> walk in new Action<Cm.YamlNode>[]
        {
            node => node.ToString(),
            node => stream.Save(new StringWriter(), false),
            node => node.Accept(new Counting()),
        })
        {
            Exception? failure = null;
            var thread = new Thread(() => failure = Record.Exception(() => walk(root)), 192 * 1024);
            thread.Start();
            thread.Join();
            thrown.Add(failure);
        }

        // Each walk either finished or stopped with the library's own exception; none brought the process down.
        Assert.All(thrown, failure => Assert.True(failure is null || failure is global::OneYaml.Compat.YamlDotNet.Core.YamlException, failure?.ToString()));
    }

    [Fact]
    public void SharpYaml_serializer_reaches_the_depth_limit()
    {
        string yaml = BlockChain(Levels);
        HeadroomChain chain = Chain(Levels);
        OnMegabyteStack(() =>
        {
            var options = new Cs.YamlSerializerOptions { MaxDepth = Levels + 1 };
            Assert.Equal(Levels + 1, Length(Cs.YamlSerializer.Deserialize<HeadroomChain>(yaml, options)));
            string written = Cs.YamlSerializer.Serialize(chain, options);
            Assert.Equal(Levels, Length(Cs.YamlSerializer.Deserialize<HeadroomChain>(written, options)));
        });
    }
}
