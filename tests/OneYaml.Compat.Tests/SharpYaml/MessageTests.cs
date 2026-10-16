using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// What the messages and options of the compat layer say.
public class SharpYamlMessageTests
{
    [Fact]
    public void A_message_names_the_position_once_in_the_marks_of_the_compat_layer()
    {
        Cs.YamlException e = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("a: 1\nb: [\n"));

        Assert.Contains("Lin:", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(line ", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(", column ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_error_names_the_position_once()
    {
        var options = new Cs.YamlSerializerOptions { Limits = global::OneYaml.YamlLimits.Default with { AliasNodeFactor = 1, AliasNodeFloor = 10, AliasByteFactor = 1, AliasByteFloor = 100 } };

        Cs.YamlException e = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("a: &a [1, 2, 3, 4, 5, 6, 7, 8, 9]\nb: [*a, *a, *a, *a, *a, *a]\n", options));

        Assert.Contains("exceeds the budget set by YamlLimits", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(line ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxDepth_reads_back_the_value_set_and_the_lower_of_it_and_the_limit_applies()
    {
        var options = new Cs.YamlSerializerOptions { MaxDepth = 500, Limits = global::OneYaml.YamlLimits.Default with { MaxDepth = 3 } };

        Assert.Equal(500, options.MaxDepth);
        _ = Assert.ThrowsAny<Cs.YamlException>(() => Cs.YamlSerializer.Deserialize<object>("[[[[1]]]]", options));
    }
}
