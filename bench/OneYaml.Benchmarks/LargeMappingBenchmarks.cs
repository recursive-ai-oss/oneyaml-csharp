using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace OneYaml.Benchmarks;

/// <summary>
/// Many operations on one large mapping: replaces, removes and tests of existing keys, applied as one RFC 6902
/// patch, and the same keys read by pointer. Each run parses the input, so the parse is part of every row.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(LargeMappingConfig))]
public class LargeMappingBenchmarks
{
    private byte[] _bytes = [];
    private string _patch = string.Empty;
    private string[] _paths = [];

    [Params(100_000)]
    public int Keys { get; set; }

    [Params(1_000, 10_000, 100_000)]
    public int Ops { get; set; }

    [Params("replace", "remove", "test")]
    public string Verb { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var yaml = new StringBuilder("# settings\nbig:\n");
        for (int i = 0; i < Keys; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"  key{i}: value{i}\n");
        }

        _bytes = Encoding.UTF8.GetBytes(yaml.ToString());

        // The keys come in a fixed shuffled order, each once.
        var order = Enumerable.Range(0, Keys).ToArray();
        var rnd = new Random(12345);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        _paths = new string[Ops];
        var patch = new StringBuilder("[");
        for (int i = 0; i < Ops; i++)
        {
            _paths[i] = "/big/key" + order[i].ToString(CultureInfo.InvariantCulture);
            patch.Append(i == 0 ? string.Empty : ",").Append("{\"op\":\"").Append(Verb).Append("\",\"path\":\"").Append(_paths[i]).Append('"');
            if (Verb == "replace")
            {
                patch.Append(CultureInfo.InvariantCulture, $",\"value\":\"patched-{i}\"");
            }
            else if (Verb == "test")
            {
                patch.Append(CultureInfo.InvariantCulture, $",\"value\":\"value{order[i]}\"");
            }

            patch.Append('}');
        }

        _patch = patch.Append(']').ToString();
    }

    [Benchmark(Description = "OneYaml parse + ApplyPatch + write")]
    public byte[] ApplyPatch()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        doc.ApplyPatch(_patch);
        return doc.ToUtf8Bytes();
    }

    [Benchmark(Description = "OneYaml parse + Get<string> by pointer")]
    public int GetByPointer()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        int total = 0;
        foreach (string path in _paths)
        {
            total += doc.Get<string>(path).Length;
        }

        return total;
    }

    private sealed class LargeMappingConfig : ManualConfig
    {
        public LargeMappingConfig()
        {
            // Runs of seconds to minutes: one operation of the job is one whole run.
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(1).WithIterationCount(3).WithInvocationCount(1).WithUnrollFactor(1));
        }
    }
}
