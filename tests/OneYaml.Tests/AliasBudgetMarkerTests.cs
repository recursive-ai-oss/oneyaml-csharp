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

    public sealed class Holder
    {
        public object? E { get; set; }
    }

    // A chain of mappings that merge the one before: the aliases are charged for what they stand for, and the copies the
    // merges make are charged on top of that. Read into a member of type object, so that aliases are expanded.
    private static string MergeChain()
    {
        string keys = string.Join(", ", Enumerable.Range(1, 20).Select(i => "k" + i + ": 1"));
        return "e:\n  a: &a {" + keys + "}\n  b: &b {<<: *a}\n  c: &c {<<: *b}\n  d: &d {<<: *c}\n  f: {<<: *d}\n";
    }

    [Fact]
    public void Merging_mappings_marks_a_refused_merge_and_says_so()
    {
        var options = new YamlSerializerOptions { MergeKeys = true, Limits = new YamlLimits { AliasNodeFactor = 0, AliasNodeFloor = 50, AliasByteFloor = 100_000 } };
        YamlException e = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder>(MergeChain(), options));
        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Contains("Merging mappings", e.Message, StringComparison.Ordinal);
        Assert.True(e.IsAliasBudget);
    }

    [Fact]
    public void Reading_a_node_that_holds_aliases_marks_a_refused_expansion_on_the_replay()
    {
        using YamlDocument doc = YamlDocument.Parse(Fan, new YamlDocumentOptions { Limits = Tiny });
        YamlException e = Assert.Throws<YamlException>(() => doc.Root["b"].Deserialize<object>());
        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Contains("Expanding the aliases", e.Message, StringComparison.Ordinal);
        Assert.True(e.IsAliasBudget);
    }

    [Fact]
    public void A_patch_that_compares_through_aliases_keeps_the_mark_when_it_names_the_operation()
    {
        using YamlDocument doc = YamlDocument.Parse(Fan + "c: []\n", new YamlDocumentOptions { Limits = Tiny });
        byte[] before = doc.ToUtf8Bytes();
        YamlException e = Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"test","path":"/b","value":[[1,2,3,4,5,6,7,8,9],[1,2,3,4,5,6,7,8,9],[1,2,3,4,5,6,7,8,9],[1,2,3,4,5,6,7,8,9],[1,2,3,4,5,6,7,8,9],[1,2,3,4,5,6,7,8,9]]}]"""));
        Assert.Equal(YamlErrorCategory.Limit, e.Category);
        Assert.Equal(0, e.OperationIndex!.Value);
        Assert.True(e.IsAliasBudget);
        Assert.Equal(before, doc.ToUtf8Bytes());
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
