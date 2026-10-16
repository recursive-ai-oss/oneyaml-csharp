using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OneYaml.Tests;

// What reading and writing the System.Text.Json members costs, counted in allocated bytes (not time), and the
// behaviour the cheaper paths must keep.
public class JsonMemberCostTests
{
    public sealed class Holder
    {
        public JsonNode? Node { get; set; }

        public JsonElement Settings { get; set; }
    }

    // The least bytes of several runs after a warm-up: code that has not been optimised yet allocates more than the
    // code that runs in steady state, and the steady state is what is meant.
    private static long Allocated(Action action)
    {
        for (int i = 0; i < 8; i++)
        {
            action();
        }

        long least = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Theory]
    [InlineData("7", "'7'", 100)]
    [InlineData("2.5", "'2.5'", 260)]
    public void A_node_with_many_numbers_is_parsed_from_one_text(string number, string text, int most)
    {
        const int items = 4000;
        string Yaml(string value) => "node:\n" + string.Concat(Enumerable.Range(0, items).Select(i => "  k" + i + ": " + value + "\n"));
        string numbers = Yaml(number);
        string strings = Yaml(text);

        long forNumbers = Allocated(() => YamlSerializer.Deserialize<Holder>(numbers));
        long forStrings = Allocated(() => YamlSerializer.Deserialize<Holder>(strings));

        // A tree of this size is written as one JSON text and parsed once, so a number costs little more than a string;
        // a document and a parse of its own per number cost about 140 bytes more still.
        long extra = (forNumbers - forStrings) / items;
        Assert.True(extra < most, $"A number took {extra} bytes more than a string.");
    }

    [Fact]
    public void Numbers_in_a_node_keep_their_digits_and_convert_to_every_number_type()
    {
        const string yaml = "node: {a: 7, b: 2.50, c: -0, d: 1e3, e: [1, 2.0, 3]}\n";

        JsonNode node = YamlSerializer.Deserialize<Holder>(yaml)!.Node!;

        Assert.Equal("{\"a\":7,\"b\":2.50,\"c\":-0,\"d\":1e3,\"e\":[1,2.0,3]}", node.ToJsonString());
        Assert.Equal(7, node["a"]!.GetValue<int>());
        Assert.Equal(7L, node["a"]!.GetValue<long>());
        Assert.Equal(7m, node["a"]!.GetValue<decimal>());
        Assert.Equal(2.5, node["b"]!.GetValue<double>());
        Assert.Equal(2.5m, node["b"]!.GetValue<decimal>());
        Assert.Equal(2, node["e"]![1]!.GetValue<double>());
    }

    [Fact]
    public void Writing_small_objects_of_a_JSON_element_allocates_no_name_set_per_object()
    {
        const int objects = 1000;
        var json = new StringBuilder("[");
        for (int i = 0; i < objects; i++)
        {
            json.Append(i == 0 ? "" : ",").Append("{\"a\":1,\"b\":2,\"c\":3}");
        }

        json.Append(']');
        using JsonDocument document = JsonDocument.Parse(json.ToString());
        var holder = new Holder { Settings = document.RootElement.Clone() };

        long bytes = Allocated(() => YamlSerializer.Serialize(holder));

        // A set per object cost about 160 bytes each on top of this.
        Assert.True(bytes / objects < 380, $"An object took {bytes / objects} bytes.");
    }

    [Fact]
    public void A_repeated_name_is_found_in_small_and_large_objects_and_the_name_sets_are_reused()
    {
        string Object(int names, string? repeat)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < names; i++)
            {
                sb.Append(i == 0 ? "" : ",").Append("\"n").Append(i).Append("\":1");
            }

            return sb.Append(repeat is null ? "" : ",\"" + repeat + "\":2").Append('}').ToString();
        }

        // Names 40 wide twice in a row, then a repeat that is only found in the second one: the set of the first
        // must not leak into the second.
        foreach (int width in new[] { 2, 16, 17, 40 })
        {
            foreach (string repeat in new[] { "n0", "n" + (width - 1) })
            {
                using JsonDocument bad = JsonDocument.Parse("[" + Object(width, null) + "," + Object(width, repeat) + "]");
                YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Serialize(new Holder { Settings = bad.RootElement.Clone() }));
                Assert.Equal(YamlErrorCategory.Binding, ex.Category);
                Assert.Contains($"'{repeat}'", ex.Message, StringComparison.Ordinal);
            }

            using JsonDocument fine = JsonDocument.Parse("[" + Object(width, null) + "," + Object(width, null) + ",{\"x\":" + Object(width, null) + "}]");
            string yaml = YamlSerializer.Serialize(new Holder { Settings = fine.RootElement.Clone() });
            Assert.Equal(fine.RootElement.GetRawText(), YamlSerializer.Deserialize<Holder>(yaml)!.Settings.GetRawText());
        }

        // A large object that holds a large object that repeats a name of its own.
        using JsonDocument nested = JsonDocument.Parse("{" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"p{i}\":1")) + ",\"in\":" + Object(20, "n3") + "}");
        Assert.Throws<YamlException>(() => YamlSerializer.Serialize(new Holder { Settings = nested.RootElement.Clone() }));
    }
}
