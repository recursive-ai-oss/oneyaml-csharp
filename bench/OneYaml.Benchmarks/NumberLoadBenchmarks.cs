using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace OneYaml.Benchmarks;

/// <summary>One row of the number-heavy synthetic file: integers, short decimals, exponents and a few wide values.</summary>
public sealed class NumberRow
{
    public long Id { get; set; }

    public long Count { get; set; }

    public double Price { get; set; }

    public double Ratio { get; set; }

    public double Scaled { get; set; }

    public double Wide { get; set; }
}

/// <summary>
/// Load of number-heavy input with OneYaml alone, to compare two builds of the library: generic (<c>object</c>) load of the
/// kit workloads and of a synthetic file of numbers, and typed load of the records files and of the same synthetic rows.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class NumberLoadBenchmarks
{
    private byte[] _bytes = [];
    private bool _typedRows;
    private bool _typedRecords;

    [Params("records-4k", "records-64k", "helm-values-64k", "k8s-multi-16k", "openapi-100k", "numbers-synthetic")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _typedRows = Workload == "numbers-synthetic";
        _typedRecords = Workload.StartsWith("records", StringComparison.Ordinal);
        _bytes = _typedRows ? Synthetic() : Workloads.Load(Workload).Bytes;
        if (YamlSerializer.DeserializeAll<object>(_bytes).Count == 0)
        {
            throw new InvalidOperationException("The workload loaded as nothing.");
        }
    }

    // 2,000 rows; the values come from a fixed generator so every build reads the same bytes.
    private static byte[] Synthetic()
    {
        var rnd = new Random(42);
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("- id: ").Append(i * 7 + 100000).Append('\n');
            sb.Append("  count: ").Append(rnd.Next(-100000, 100000)).Append('\n');
            sb.Append("  price: ").Append((rnd.Next(1, 100000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  ratio: ").Append(rnd.NextDouble().ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  scaled: ").Append(rnd.Next(1, 999)).Append('e').Append(rnd.Next(-6, 7)).Append('\n');
            sb.Append("  wide: ").Append((rnd.NextDouble() * 1e5).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Benchmark(Description = "generic reflection")]
    public object? GenericReflection() => YamlSerializer.DeserializeAll<object>(_bytes);

    [Benchmark(Description = "generic generated")]
    public object? GenericGenerated() => YamlSerializer.DeserializeAll(_bytes, OneYamlContext.Default.Object);

    [Benchmark(Description = "typed reflection")]
    public object? TypedReflection() => _typedRows
        ? YamlSerializer.Deserialize<List<NumberRow>>(_bytes)
        : _typedRecords ? YamlSerializer.Deserialize<List<OneYamlKit.Bench.Models.Record>>(_bytes) : null;

    [Benchmark(Description = "typed generated")]
    public object? TypedGenerated() => _typedRows
        ? YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.ListNumberRow)
        : _typedRecords ? YamlSerializer.Deserialize(_bytes, OneYamlContext.Default.ListRecord) : null;
}
