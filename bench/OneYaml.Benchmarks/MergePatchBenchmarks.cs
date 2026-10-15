using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace OneYaml.Benchmarks;

/// <summary>
/// RFC 7396 merge patches: a typical small one, and the removal of many keys from a large mapping.
/// Each run parses the input, applies the patch and writes the result.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(MergeConfig))]
public class MergePatchBenchmarks
{
    private byte[] _bytes = [];
    private string _patch = string.Empty;

    [Params("small-config", "mapping-20k-delete-600", "mapping-500k-delete-12000")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var yaml = new StringBuilder();
        var patch = new StringBuilder("{");
        switch (Workload)
        {
            case "small-config":
                yaml.Append("# service settings\nname: api\nreplicas: 3\nimage:\n  repo: example/api\n  tag: \"1.4.2\"\nenv:\n  LOG: info\n  MODE: prod\n  DEBUG: \"0\"\nports: [80, 443]\n");
                patch.Append("\"replicas\":5,\"image\":{\"tag\":\"1.5.0\"},\"env\":{\"DEBUG\":null,\"REGION\":\"eu\"}");
                break;
            case "mapping-20k-delete-600":
                Build(yaml, patch, 20_000, 600);
                break;
            default:
                Build(yaml, patch, 500_000, 12_000);
                break;
        }

        _bytes = Encoding.UTF8.GetBytes(yaml.ToString());
        _patch = patch.Append('}').ToString();
    }

    // Keys k000000.. with a value each; the deletions come in runs of 20 spread over the whole mapping.
    private static void Build(StringBuilder yaml, StringBuilder patch, int keys, int deletions)
    {
        for (int i = 0; i < keys; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"k{i:D6}: value\n");
        }

        int runs = deletions / 20;
        int gap = keys / runs;
        bool first = true;
        for (int r = 0; r < runs; r++)
        {
            for (int j = 0; j < 20; j++)
            {
                patch.Append(first ? string.Empty : ",").Append(CultureInfo.InvariantCulture, $"\"k{r * gap + 100 + j:D6}\":null");
                first = false;
            }
        }
    }

    [Benchmark(Description = "OneYaml parse + ApplyMergePatch + write")]
    public byte[] OneYamlMergePatch()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        doc.ApplyMergePatch(_patch);
        return doc.ToUtf8Bytes();
    }

    private sealed class MergeConfig : ManualConfig
    {
        public MergeConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        }
    }
}
