using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace OneYaml.Benchmarks;

/// <summary>Reads a list of System.Text.Json nodes out of YAML: the cost of one small scalar member and of one number member.</summary>
[MemoryDiagnoser]
public class JsonMemberBenchmarks
{
    private string _mixed = string.Empty;
    private string _strings = string.Empty;

    private string _tree = string.Empty;
    private Holder _element = new();

    /// <summary>A node member and an element member, as one value each.</summary>
    public sealed class Holder
    {
        /// <summary>The node member.</summary>
        public JsonNode? Node { get; set; }

        /// <summary>The element member.</summary>
        public JsonElement Settings { get; set; }
    }

    [Params(20_000)]
    public int Items { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var mixed = new StringBuilder();
        var strings = new StringBuilder();
        for (int i = 0; i < Items; i++)
        {
            mixed.Append("- ").Append((i % 3) switch { 0 => i.ToString(System.Globalization.CultureInfo.InvariantCulture), 1 => "s" + i, _ => "2.5" }).Append('\n');
            strings.Append("- s").Append(i).Append('\n');
        }

        var tree = new StringBuilder("node:\n");
        for (int i = 0; i < 2000; i++)
        {
            tree.Append("  k").Append(i).Append(": ").Append((i % 4) switch { 0 => i.ToString(System.Globalization.CultureInfo.InvariantCulture), 1 => "\"s" + i + "\"", 2 => "[1.5, true, null]", _ => "{x: " + i + ", y: text}" }).Append('\n');
        }

        _tree = tree.ToString();
        _element = new Holder { Settings = YamlSerializer.Deserialize<Holder>(_tree.Replace("node:", "settings:", StringComparison.Ordinal))!.Settings };
        _mixed = mixed.ToString();
        _strings = strings.ToString();
    }

    [Benchmark(Description = "List<JsonNode>, strings, numbers")]
    public List<JsonNode?>? Mixed() => YamlSerializer.Deserialize<List<JsonNode?>>(_mixed);

    [Benchmark(Description = "List<JsonNode>, strings only")]
    public List<JsonNode?>? Strings() => YamlSerializer.Deserialize<List<JsonNode?>>(_strings);

    [Benchmark(Description = "JsonNode member, 2,000 mixed entries, read")]
    public Holder? NodeTree() => YamlSerializer.Deserialize<Holder>(_tree);

    [Benchmark(Description = "JsonElement member, 2,000 mixed entries, write")]
    public string ElementTreeWrite() => YamlSerializer.Serialize(_element);
}
