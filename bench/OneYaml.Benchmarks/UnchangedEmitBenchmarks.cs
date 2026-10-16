using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using SharpYaml.Syntax;

namespace OneYaml.Benchmarks;

/// <summary>
/// Writing a parsed document back unchanged, in the shape of a batch: many parsed documents are
/// prepared outside the clock and then each one is written once. The batch holds about 4 MB of
/// input, so a small file means many documents and a large file means few. Reported times are per
/// batch; divide by <see cref="BatchSize"/> (printed in the setup line) for one document. The
/// "Warm" rows write one document over and over, which keeps its buffer in cache.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(EmitConfig))]
public class UnchangedEmitBenchmarks
{
    private static readonly YamlSyntaxOptions Trivia = new() { IncludeTrivia = true };

    private byte[] _bytes = [];
    private string _text = string.Empty;
    private YamlDocument[] _docs = [];
    private YamlSyntaxTree[] _trees = [];
    private YamlSyntaxTree[] _ownTrees = [];
    private YamlDocument _warmDoc = null!;
    private YamlSyntaxTree _warmTree = null!;

    [Params("reference-a", "compose-wordpress", "helm-ingress-nginx", "records-64k", "compose-merge-64k", "openapi-100k", "openapi-8m")]
    public string Workload { get; set; } = string.Empty;

    /// <summary>The number of documents in one timed batch.</summary>
    public int BatchSize { get; private set; }

    /// <summary>Prints a hash of the unchanged output for every workload file in the directory, so two builds can be compared.</summary>
    /// <param name="dir">The workload directory.</param>
    public static void PrintHashes(string dir)
    {
        foreach (string file in Directory.GetFiles(dir, "input.yaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            byte[] input = File.ReadAllBytes(file);
            using YamlDocument doc = YamlDocument.Parse(input);
            byte[] output = doc.ToUtf8Bytes();
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output))[..16];
            Console.WriteLine($"{hash} {(output.AsSpan().SequenceEqual(input) ? "same-as-input" : "DIFFERS")} {Path.GetFileName(Path.GetDirectoryName(file))}");
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        string flat = Path.Combine(dir, Workload + ".yaml");
        _bytes = File.ReadAllBytes(File.Exists(flat) ? flat : Path.Combine(dir, "workloads", Workload, "input.yaml"));
        _text = Encoding.UTF8.GetString(_bytes);
        BatchSize = Math.Clamp(4_000_000 / Math.Max(_bytes.Length, 1), 1, 1000);
        _warmDoc = YamlDocument.Parse(_bytes);
        _warmTree = YamlSyntaxTree.Parse(_text, Trivia);
        Console.WriteLine($"// {Workload}: {_bytes.Length} bytes, batch {BatchSize}");
    }

    [IterationSetup]
    public void PrepareBatch()
    {
        _docs = new YamlDocument[BatchSize];
        _trees = new YamlSyntaxTree[BatchSize];
        _ownTrees = new YamlSyntaxTree[BatchSize];
        for (int i = 0; i < BatchSize; i++)
        {
            _docs[i] = YamlDocument.Parse(_bytes);
            _trees[i] = YamlSyntaxTree.Parse(_text, Trivia);
            // A text of its own, as a caller who read each document from its own source would have.
            _ownTrees[i] = YamlSyntaxTree.Parse(new string(_text.AsSpan()), Trivia);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _warmDoc.Dispose();

    [Benchmark]
    public int OneYamlCold()
    {
        int total = 0;
        foreach (YamlDocument d in _docs)
        {
            total += d.ToUtf8Bytes().Length;
        }

        return total;
    }

    [Benchmark]
    public int SharpYamlCold()
    {
        int total = 0;
        foreach (YamlSyntaxTree t in _trees)
        {
            total += Encoding.UTF8.GetBytes(t.ToFullString()).Length;
        }

        return total;
    }

    [Benchmark]
    public int SharpYamlColdOwnText()
    {
        int total = 0;
        foreach (YamlSyntaxTree t in _ownTrees)
        {
            total += Encoding.UTF8.GetBytes(t.ToFullString()).Length;
        }

        return total;
    }

    [Benchmark]
    public int OneYamlWarm()
    {
        int total = 0;
        for (int i = 0; i < BatchSize; i++)
        {
            total += _warmDoc.ToUtf8Bytes().Length;
        }

        return total;
    }

    [Benchmark]
    public int SharpYamlWarm()
    {
        int total = 0;
        for (int i = 0; i < BatchSize; i++)
        {
            total += Encoding.UTF8.GetBytes(_warmTree.ToFullString()).Length;
        }

        return total;
    }

    private sealed class EmitConfig : ManualConfig
    {
        public EmitConfig()
        {
            // One invocation per iteration: each timed batch must see freshly prepared documents.
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance)
                .WithInvocationCount(1).WithUnrollFactor(1).WithWarmupCount(5).WithIterationCount(30));
        }
    }
}
