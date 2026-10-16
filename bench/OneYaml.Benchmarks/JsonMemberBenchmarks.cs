using System.Text;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace OneYaml.Benchmarks;

/// <summary>Reads a list of System.Text.Json nodes out of YAML: the cost of one small scalar member and of one number member.</summary>
[MemoryDiagnoser]
public class JsonMemberBenchmarks
{
    private string _mixed = string.Empty;
    private string _strings = string.Empty;

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

        _mixed = mixed.ToString();
        _strings = strings.ToString();
    }

    [Benchmark(Description = "List<JsonNode>, strings, numbers")]
    public List<JsonNode?>? Mixed() => YamlSerializer.Deserialize<List<JsonNode?>>(_mixed);

    [Benchmark(Description = "List<JsonNode>, strings only")]
    public List<JsonNode?>? Strings() => YamlSerializer.Deserialize<List<JsonNode?>>(_strings);
}
