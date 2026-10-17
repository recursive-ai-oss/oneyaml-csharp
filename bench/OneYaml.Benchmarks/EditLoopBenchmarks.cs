using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace OneYaml.Benchmarks;

/// <summary>
/// Loops of single edits on a large document, through the pointer methods and through the indexers: what a program
/// that walks a document and changes values one by one pays. Each run parses the input, so the parse is part of
/// every row, and ends with the write, so a run that edited a copy or a shared state is held to the same bytes.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(EditLoopConfig))]
public class EditLoopBenchmarks
{
    private byte[] _bytes = [];
    private int[] _order = [];

    [Params("keys-100k", "records-8m")]
    public string Shape { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        if (Shape == "keys-100k")
        {
            var yaml = new StringBuilder("# settings\nbig:\n");
            for (int i = 0; i < 100_000; i++)
            {
                yaml.Append(CultureInfo.InvariantCulture, $"  key{i}: value{i}\n");
            }

            _bytes = Encoding.UTF8.GetBytes(yaml.ToString());
            _order = Shuffled(100_000);
        }
        else
        {
            string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
                ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
            string flat = Path.Combine(dir, Shape + ".yaml");
            _bytes = File.ReadAllBytes(File.Exists(flat) ? flat : Path.Combine(dir, "workloads", Shape, "input.yaml"));
            using YamlDocument doc = YamlDocument.Parse(_bytes);
            _order = Shuffled(doc.Root.Count);
        }
    }

    // The positions come in a fixed shuffled order, each once, so that no edit follows the one before it in the text.
    private static int[] Shuffled(int count)
    {
        int[] order = Enumerable.Range(0, count).ToArray();
        var rnd = new Random(12345);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    [Benchmark(Description = "parse + 1,000 Set by pointer + write")]
    public byte[] SetByPointer1k() => SetByPointer(1_000);

    [Benchmark(Description = "parse + 10,000 Set by pointer + write")]
    public byte[] SetByPointer10k() => SetByPointer(10_000);

    [Benchmark(Description = "parse + 1,000 indexer writes + write")]
    public byte[] SetByIndexer1k() => SetByIndexer(1_000);

    [Benchmark(Description = "parse + 10,000 indexer writes + write")]
    public byte[] SetByIndexer10k() => SetByIndexer(10_000);

    private byte[] SetByPointer(int edits)
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        int n = Math.Min(edits, _order.Length);
        if (Shape == "keys-100k")
        {
            for (int i = 0; i < n; i++)
            {
                doc.Set("/big/key" + _order[i].ToString(CultureInfo.InvariantCulture), "patched-" + i.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                doc.Set("/" + _order[i].ToString(CultureInfo.InvariantCulture) + "/score", i);
            }
        }

        return doc.ToUtf8Bytes();
    }

    private byte[] SetByIndexer(int edits)
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        int n = Math.Min(edits, _order.Length);
        if (Shape == "keys-100k")
        {
            YamlNode big = doc.Root["big"];
            for (int i = 0; i < n; i++)
            {
                big["key" + _order[i].ToString(CultureInfo.InvariantCulture)] = "patched-" + i.ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            YamlNode root = doc.Root;
            for (int i = 0; i < n; i++)
            {
                root[_order[i]]["score"] = i;
            }
        }

        return doc.ToUtf8Bytes();
    }

    private sealed class EditLoopConfig : ManualConfig
    {
        public EditLoopConfig()
        {
            // Runs of seconds: one operation of the job is one whole run.
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(1).WithIterationCount(2).WithInvocationCount(1).WithUnrollFactor(1));
        }
    }
}

/// <summary>A walk over every item of a sequence that changes one field of each, on a document of about a megabyte.</summary>
[MemoryDiagnoser]
[Config(typeof(EachItemConfig))]
public class EachItemBenchmarks
{
    private byte[] _bytes = [];

    [Params("records-1m")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        string flat = Path.Combine(dir, Workload + ".yaml");
        if (File.Exists(flat))
        {
            _bytes = File.ReadAllBytes(flat);
            return;
        }

        // The first megabyte of the records file, cut where a record begins.
        byte[] all = File.ReadAllBytes(Path.Combine(dir, "workloads", "records-8m", "input.yaml"));
        int cut = Math.Min(all.Length, 1 << 20);
        while (cut > 0 && !(all[cut - 1] == (byte)'\n' && all.AsSpan(cut).StartsWith("- id:"u8)))
        {
            cut--;
        }

        _bytes = all.AsSpan(0, cut).ToArray();
    }

    [Benchmark(Description = "OneYaml parse + each item's field set through the indexer + write")]
    public byte[] EachItem()
    {
        using YamlDocument doc = YamlDocument.Parse(_bytes);
        int i = 0;
        foreach (YamlNode item in doc.Root.Items)
        {
            item["score"] = i++;
        }

        return doc.ToUtf8Bytes();
    }

    private sealed class EachItemConfig : ManualConfig
    {
        public EachItemConfig()
        {
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(1).WithIterationCount(2).WithInvocationCount(1).WithUnrollFactor(1));
        }
    }
}
