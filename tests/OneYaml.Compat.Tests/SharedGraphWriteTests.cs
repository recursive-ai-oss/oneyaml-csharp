using Xunit;
using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using CmCore = global::OneYaml.Compat.YamlDotNet.Core;

namespace OneYaml.Compat.Tests;

public class SharedGraphWriteTests
{
    // Each level holds the level below it several times over, so the graph is small and its expansion is huge.
    private static List<object> Laughs(int levels, int fanout)
    {
        var level = new List<object> { 1 };
        for (int i = 0; i < levels; i++)
        {
            var next = new List<object>();
            for (int j = 0; j < fanout; j++)
            {
                next.Add(level);
            }

            level = next;
        }

        return level;
    }

    [Fact]
    public void Writing_a_graph_that_shares_its_parts_without_aliases_stops_at_the_default_budget()
    {
        // 8^7 is about 2 million leaves, over the default allowance of one million nodes.
        List<object> graph = Laughs(levels: 7, fanout: 8);

        Assert.Throws<CmCore.MaximumRecursionLevelReachedException>(() => new Cm.SerializerBuilder().DisableAliases().Build().Serialize(graph));
        Assert.Throws<CmCore.MaximumRecursionLevelReachedException>(() => new Cm.SerializerBuilder().JsonCompatible().Build().Serialize(graph));
    }

    [Fact]
    public void The_limits_of_the_builder_set_the_allowance_for_expanded_parts()
    {
        var tight = new global::OneYaml.YamlLimits { AliasNodeFactor = 2, AliasNodeFloor = 1_000, AliasByteFactor = 2, AliasByteFloor = 1_000 };
        Cm.ISerializer serializer = new Cm.SerializerBuilder().DisableAliases().WithLimits(tight).Build();

        // 5^6 is 15,625 leaves against an allowance of 1,000 nodes.
        Assert.Throws<CmCore.MaximumRecursionLevelReachedException>(() => serializer.Serialize(Laughs(levels: 6, fanout: 5)));

        // Sharing within the allowance still writes in full.
        string text = serializer.Serialize(Laughs(levels: 2, fanout: 3));
        Assert.Equal(9, text.Count(c => c == '1'));
    }

    [Fact]
    public void A_long_list_without_sharing_is_not_counted_as_expansion()
    {
        var list = Enumerable.Range(0, 1_500_000).ToList();

        string text = new Cm.SerializerBuilder().DisableAliases().Build().Serialize(list);

        Assert.StartsWith("- 0\n- 1\n", text, StringComparison.Ordinal);
    }
}
