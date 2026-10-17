using Xunit;

namespace OneYaml.Tests;

// A refused alias expansion is marked on the error, so a caller can tell it from the depth and size limits without
// reading the message.
public class AliasBudgetMarkerTests
{
    private static readonly YamlLimits Tiny = new()
    {
        AliasNodeFactor = 1,
        AliasNodeFloor = 10,
        AliasByteFactor = 1,
        AliasByteFloor = 100,
    };

    private const string Fan = "a: &a [1, 2, 3, 4, 5, 6, 7, 8, 9]\nb: [*a, *a, *a, *a, *a, *a]\n";

    [Fact]
    public void The_serializer_marks_a_refused_expansion()
    {
        YamlException generic = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(Fan, new YamlSerializerOptions { Limits = Tiny }));
        Assert.True(generic.IsAliasBudget);
    }

    [Fact]
    public void The_document_marks_a_refused_expansion()
    {
        using YamlDocument doc = YamlDocument.Parse(Fan, new YamlDocumentOptions { Limits = Tiny });
        YamlException e = Assert.Throws<YamlException>(() => doc.Get<object>("/b"));
        Assert.True(e.IsAliasBudget);
    }

    [Fact]
    public void Other_limits_and_other_errors_are_not_marked()
    {
        string deep = new string('[', 40) + new string(']', 40);
        YamlException depth = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(deep, new YamlSerializerOptions { Limits = new YamlLimits { MaxDepth = 8 } }));
        Assert.Equal(YamlErrorCategory.Limit, depth.Category);
        Assert.False(depth.IsAliasBudget);

        YamlException syntax = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>("[1", new YamlSerializerOptions()));
        Assert.False(syntax.IsAliasBudget);

        // The wording alone does not make an error an alias-budget one.
        var spoken = new YamlException(Tiny.AliasBudgetMessage("x") + " ", YamlErrorCategory.Limit, 0, 0, -1);
        Assert.False(spoken.IsAliasBudget);
    }
}
