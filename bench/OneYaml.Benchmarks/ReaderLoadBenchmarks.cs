using System.Text;
using BenchmarkDotNet.Attributes;
using Cs = OneYaml.Compat.SharpYaml;

namespace OneYaml.Benchmarks;

/// <summary>
/// Generic load from a text reader with default options: a string reader (which already holds its text) and a stream reader
/// (which does not), on a file of about 64 KB and one of about 1 MB made of sixteen copies of it. Run it on two builds to compare them.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ParseConfig))]
public class ReaderLoadBenchmarks
{
    private string _text = string.Empty;
    private byte[] _bytes = [];

    [Params("64k", "1m")]
    public string Size { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string one = Workloads.Load("records-64k").Text;
        var sb = new StringBuilder();
        for (int i = 0; i < (Size == "1m" ? 16 : 1); i++)
        {
            sb.Append(one);
        }

        _text = sb.ToString();
        _bytes = Encoding.UTF8.GetBytes(_text);
    }

    [Benchmark(Description = "core StringReader")]
    public object? CoreStringReader() => YamlSerializer.Deserialize<object>(new StringReader(_text));

    [Benchmark(Description = "core StreamReader")]
    public object? CoreStreamReader() => YamlSerializer.Deserialize<object>(new StreamReader(new MemoryStream(_bytes)));

    [Benchmark(Description = "compat SharpYaml StringReader")]
    public object? CompatStringReader() => Cs.YamlSerializer.Deserialize<object>(new StringReader(_text));

    [Benchmark(Description = "compat SharpYaml StreamReader")]
    public object? CompatStreamReader() => Cs.YamlSerializer.Deserialize<object>(new StreamReader(new MemoryStream(_bytes)));
}
