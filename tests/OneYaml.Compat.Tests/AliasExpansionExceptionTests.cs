using System.Text;
using Xunit;
using YdnCore = global::OneYaml.Compat.YamlDotNet.Core;
using YdnSer = global::OneYaml.Compat.YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// The alias budget has its own exception type; it is still a MaximumRecursionLevelReachedException for existing catch blocks.
public class AliasExpansionExceptionTests
{
    private static string Bomb(int levels, int width)
    {
        var sb = new StringBuilder("a0: &a0 [" + string.Join(", ", Enumerable.Repeat("x", width)) + "]\n");
        for (int i = 1; i <= levels; i++)
        {
            sb.Append('a').Append(i).Append(": &a").Append(i).Append(" [").Append(string.Join(", ", Enumerable.Repeat("*a" + (i - 1), width))).Append("]\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void An_alias_bomb_on_read_throws_the_alias_exception_which_the_base_catch_still_catches()
    {
        YdnSer.IDeserializer deserializer = new YdnSer.DeserializerBuilder().Build();
        string yaml = Bomb(9, 9);

        Assert.Throws<YdnCore.AliasExpansionLimitExceededException>(() => deserializer.Deserialize<object>(yaml));

        bool caught = false;
        try
        {
            deserializer.Deserialize<object>(yaml);
        }
        catch (YdnCore.MaximumRecursionLevelReachedException)
        {
            caught = true;
        }

        Assert.True(caught);
    }

    [Fact]
    public void A_graph_that_shares_its_parts_throws_the_alias_exception_when_written_without_aliases()
    {
        object leaf = new List<object> { 1, 2, 3, 4, 5, 6, 7, 8 };
        object graph = leaf;
        for (int i = 0; i < 7; i++)
        {
            graph = Enumerable.Repeat(graph, 8).ToList();
        }

        var serializer = new YdnSer.SerializerBuilder().DisableAliases().Build();
        Assert.Throws<YdnCore.AliasExpansionLimitExceededException>(() => serializer.Serialize(graph));
    }

    [Fact]
    public void A_deep_nest_still_throws_exactly_the_base_type()
    {
        string yaml = new string('[', 5000) + new string(']', 5000);
        YdnSer.IDeserializer deserializer = new YdnSer.DeserializerBuilder().Build();

        var ex = Assert.ThrowsAny<YdnCore.MaximumRecursionLevelReachedException>(() => deserializer.Deserialize<object>(yaml));
        Assert.Equal(typeof(YdnCore.MaximumRecursionLevelReachedException), ex.GetType());
    }
}
