using System.Text;
using BenchmarkDotNet.Attributes;

namespace OneYaml.Benchmarks;

/// <summary>Keeps the benchmark project wired up until real benchmarks land.</summary>
[MemoryDiagnoser]
public class SmokeBenchmark
{
    private readonly byte[] _input = Encoding.UTF8.GetBytes("a: 1\n");

    [Benchmark]
    public int CountNewlines() => _input.AsSpan().Count((byte)'\n');
}
