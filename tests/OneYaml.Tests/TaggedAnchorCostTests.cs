using System.Text;
using Xunit;

namespace OneYaml.Tests;

internal sealed class TagBox
{
    public List<string> All { get; set; } = [];
}

[YamlSerializable(typeof(TagBox))]
internal sealed partial class TagBoxContext : YamlSerializerContext
{
}

// Tagged nodes inside an anchored node are recorded so an alias can bring them in again. The record keeps where the
// tag is written, not its full text: with a long %TAG prefix, a full string per node would cost the prefix once for
// every tagged node, whether or not anything reads a tag.
public class TaggedAnchorCostTests
{
    private const int PrefixLength = 100_000;
    private const int Items = 2_000;

    // Far above what the records need, far below one prefix-sized string per node (2,000 x 200 KB).
    private const long Budget = 10_000_000;

    private static string Input(bool alternating)
    {
        var sb = new StringBuilder();
        sb.Append("%TAG !e! tag:x,2000:").Append('a', PrefixLength).Append("\n---\nall: &a\n");
        for (int i = 0; i < Items; i++)
        {
            sb.Append("- !e!x");
            if (alternating)
            {
                sb.Append(i % 2 == 0 ? "x" : "y");
            }
            else
            {
                sb.Append(i);
            }

            sb.Append(" a\n");
        }

        return sb.ToString();
    }

    private static long Allocated(Action run)
    {
        run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static TheoryData<bool> Shapes => new() { true, false };

    [Theory, MemberData(nameof(Shapes))]
    public void A_typed_load_by_reflection_does_not_keep_a_tag_string_per_node(bool alternating)
    {
        string yaml = Input(alternating);

        long bytes = Allocated(() => Assert.Equal(Items, YamlSerializer.Deserialize<TagBox>(yaml)!.All.Count));

        Assert.True(bytes < Budget, $"allocated {bytes:N0} bytes");
    }

    [Theory, MemberData(nameof(Shapes))]
    public void A_typed_load_by_the_generator_does_not_keep_a_tag_string_per_node(bool alternating)
    {
        string yaml = Input(alternating);
        var info = (YamlTypeInfo<TagBox>)new TagBoxContext().GetTypeInfo(typeof(TagBox))!;

        long bytes = Allocated(() => Assert.Equal(Items, YamlSerializer.Deserialize(yaml, info)!.All.Count));

        Assert.True(bytes < Budget, $"allocated {bytes:N0} bytes");
    }

    [Theory, MemberData(nameof(Shapes))]
    public void A_generic_load_does_not_keep_a_tag_string_per_node(bool alternating)
    {
        string yaml = Input(alternating);

        long bytes = Allocated(() => Assert.NotNull(YamlSerializer.Deserialize<Dictionary<string, object?>>(yaml)));

        Assert.True(bytes < Budget, $"allocated {bytes:N0} bytes");
    }

    [Theory, MemberData(nameof(Shapes))]
    public void Binding_a_document_does_not_keep_a_tag_string_per_node(bool alternating)
    {
        string yaml = Input(alternating);
        YamlDocument doc = YamlDocument.Parse(yaml);

        long bytes = Allocated(() => Assert.Equal(Items, doc.Root.Deserialize<TagBox>()!.All.Count));

        Assert.True(bytes < Budget, $"allocated {bytes:N0} bytes");
    }

    [Fact]
    public void A_tag_is_still_reported_in_full_when_it_is_read_from_an_alias_and_from_a_document()
    {
        string yaml = "%TAG !e! tag:x,2000:\n---\n- &a [!e!one b, !two c]\n- *a\n";
        var seen = new List<string?>();
        var reader = new Utf8YamlReader(Encoding.UTF8.GetBytes(yaml), new YamlSerializerOptions(), expandAliases: true);
        while (reader.Read())
        {
            if (reader.Type == Parsing.YamlTokenType.Scalar)
            {
                seen.Add(reader.Tag);
            }
        }

        Assert.Equal(["tag:x,2000:one", "!two", "tag:x,2000:one", "!two"], seen);
    }
}
