using Xunit;

namespace OneYaml.Tests.Attributes;

// A key a merge source gives itself beats a key merged into that source, also for the keys that extension data collects.
// Among several sources the first one wins, and the keys of the mapping that is read itself beat every source.
public class MergeExtensionDataTests
{
    private static readonly YamlSerializerOptions Merging = new() { MergeKeys = true };
    private static readonly AttributeContext Generated = new(Merging);

    // Each case: the document, then the expected x and y of the extension data (0 for a missing key), then the name.
    public static IEnumerable<object[]> Cases() =>
    [
        ["<<: {<<: {x: 1}, x: 2}\n", 2, 0, ""],
        ["<<: {x: 2, <<: {x: 1}}\n", 2, 0, ""],
        ["<<: {<<: {<<: {x: 1}, x: 2}, x: 3}\n", 3, 0, ""],
        ["<<: {<<: {<<: {x: 1}, x: 2}}\n", 2, 0, ""],
        ["<<: {<<: {x: 1, y: 7}, y: 3}\n", 1, 3, ""],
        ["<<: [{x: 1}, {x: 5, y: 7}]\n", 1, 7, ""],
        ["<<: {<<: [{x: 1}, {x: 5, y: 7}], x: 4}\n", 4, 7, ""],
        ["<<: [{<<: {x: 1}, x: 2}, {x: 9, y: 4}]\n", 2, 4, ""],
        ["<<: [{x: 9, y: 4}, {<<: {x: 1}, x: 2}]\n", 9, 4, ""],
        ["<<: [{x: 1}, {<<: {x: 7}, x: 8}]\n", 1, 0, ""],
        ["x: 6\n<<: {<<: {x: 1}, x: 2}\n", 6, 0, ""],
        ["<<: {<<: {x: 1}, x: 2}\nx: 6\n", 6, 0, ""],
        ["<<: {<<: {name: n1, x: 1}, name: n2, x: 2}\n", 2, 0, "n2"],
    ];

    [Theory, MemberData(nameof(Cases))]
    public void Reflection_reads_the_precedence_of_the_generic_load(string yaml, int x, int y, string name)
    {
        Settings s = YamlSerializer.Deserialize<Settings>(yaml, Merging)!;
        Assert.Equal((x, y, name), (Extra(s.Extra, "x"), Extra(s.Extra, "y"), s.Name));
    }

    [Theory, MemberData(nameof(Cases))]
    public void The_generated_metadata_reads_the_same(string yaml, int x, int y, string name)
    {
        Settings s = YamlSerializer.Deserialize(yaml, Generated.Settings)!;
        Assert.Equal((x, y, name), (Extra(s.Extra, "x"), Extra(s.Extra, "y"), s.Name));
    }

    [Theory, MemberData(nameof(Cases))]
    public void A_constructor_bound_type_reads_the_same(string yaml, int x, int y, string name)
    {
        Labelled l = YamlSerializer.Deserialize<Labelled>("size: 1\nname: " + (name.Length == 0 ? "q" : name) + "\n" + yaml, Merging)!;
        Assert.Equal((x, y), (Extra(l.Extra, "x"), Extra(l.Extra, "y")));
        Labelled g = YamlSerializer.Deserialize("size: 1\nname: q\n" + yaml, Generated.Labelled)!;
        Assert.Equal((x, y), (Extra(g.Extra, "x"), Extra(g.Extra, "y")));
    }

    private static int Extra(Dictionary<string, object?> extra, string key) =>
        extra.TryGetValue(key, out object? v) ? (int)(long)v! : 0;
}
