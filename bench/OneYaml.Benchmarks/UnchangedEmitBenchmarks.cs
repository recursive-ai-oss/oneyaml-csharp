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

    [Params("reference-a", "compose-wordpress", "helm-ingress-nginx", "records-64k", "compose-merge-64k", "wide-map-64k", "flow-heavy-64k", "openapi-100k", "openapi-8m",
        "synthetic-16k", "synthetic-32k", "synthetic-48k", "synthetic-64k", "synthetic-80k", "synthetic-100k")]
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

    /// <summary>A block mapping of short records, about the size in the name ("synthetic-64k" is 64 KiB), so a size sweep needs no files.</summary>
    /// <param name="name">The workload name.</param>
    /// <returns>The document bytes.</returns>
    public static byte[] Synthetic(string name)
    {
        int size = int.Parse(name.AsSpan("synthetic-".Length, name.Length - "synthetic-".Length - 1), System.Globalization.CultureInfo.InvariantCulture) * 1024;
        var sb = new StringBuilder(size + 64);
        for (int i = 0; sb.Length < size - 64; i++)
        {
            sb.Append("item").Append(i).Append(":\n  name: record number ").Append(i).Append("\n  enabled: true\n  weight: ").Append(i * 7 % 1000).Append('\n');
        }

        while (sb.Length < size)
        {
            sb.Append('#');
        }

        sb.Length = size - 1;
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Times batches of unchanged writes for both libraries at a range of sizes and prints the collections each batch
    /// caused: the clock covers the writes only, as in the benchmark.
    /// </summary>
    /// <param name="dir">The workload directory for the named files.</param>
    /// <param name="names">The workload names.</param>
    public static void PrintGc(string dir, string[] names)
    {
        Console.WriteLine("workload bytes batch | lib us/doc gen0 gen1 gen2 (per batch, median of 25)");
        foreach (string name in names)
        {
            byte[] bytes = name.StartsWith("synthetic-", StringComparison.Ordinal) ? Synthetic(name) : File.ReadAllBytes(Path.Combine(dir, name + ".yaml"));
            string text = Encoding.UTF8.GetString(bytes);
            int batch = Math.Clamp(4_000_000 / Math.Max(bytes.Length, 1), 1, 1000);
            foreach (string lib in (Environment.GetEnvironmentVariable("EMIT_LIBS") ?? "oneyaml,sharpyaml").Split(','))
            {
                var times = new List<double>();
                var g0 = new List<int>();
                var g1 = new List<int>();
                var g2 = new List<int>();
                for (int round = 0; round < 25; round++)
                {
                    var docs = new YamlDocument[lib == "oneyaml" ? batch : 0];
                    var trees = new YamlSyntaxTree[lib == "oneyaml" ? 0 : batch];
                    for (int i = 0; i < batch; i++)
                    {
                        if (lib == "oneyaml")
                        {
                            docs[i] = YamlDocument.Parse(bytes);
                        }
                        else
                        {
                            trees[i] = YamlSyntaxTree.Parse(new string(text.AsSpan()), Trivia);
                        }
                    }

                    if (Environment.GetEnvironmentVariable("EMIT_GC_COLLECT") == "1")
                    {
                        GC.Collect();
                    }

                    long faults0 = MinorFaults();
                    int a = GC.CollectionCount(0), b = GC.CollectionCount(1), c = GC.CollectionCount(2);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int total = 0;
                    for (int i = 0; i < batch; i++)
                    {
                        total += lib == "oneyaml" ? docs[i].ToUtf8Bytes().Length : Encoding.UTF8.GetBytes(trees[i].ToFullString()).Length;
                    }

                    double us = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds / batch;
                    GC.KeepAlive(total);
                    if (Environment.GetEnvironmentVariable("EMIT_GC_EACH") == "1")
                    {
                        Console.WriteLine($"  {name} {lib} round {round}: {us:F2} us/doc, gen0 {GC.CollectionCount(0) - a}, gen1 {GC.CollectionCount(1) - b}, gen2 {GC.CollectionCount(2) - c}, page faults {MinorFaults() - faults0}");
                    }

                    if (round >= 5)
                    {
                        times.Add(us);
                        g0.Add(GC.CollectionCount(0) - a);
                        g1.Add(GC.CollectionCount(1) - b);
                        g2.Add(GC.CollectionCount(2) - c);
                    }

                    foreach (YamlDocument d in docs)
                    {
                        d.Dispose();
                    }
                }

                times.Sort();
                g0.Sort();
                g1.Sort();
                g2.Sort();
                int m = times.Count / 2;
                Console.WriteLine($"{name,-16} {bytes.Length,7} {batch,4} | {lib,-9} {times[m],7:F2} {g0[m],4} {g1[m],4} {g2[m],4}");
            }
        }
    }

    // Minor page faults of this process so far (Linux); the field after the command name and state in /proc/self/stat.
    private static long MinorFaults()
    {
        try
        {
            string stat = File.ReadAllText("/proc/self/stat");
            string[] f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return long.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        string flat = Path.Combine(dir, Workload + ".yaml");
        _bytes = Workload.StartsWith("synthetic-", StringComparison.Ordinal)
            ? Synthetic(Workload)
            : File.ReadAllBytes(File.Exists(flat) ? flat : Path.Combine(dir, "workloads", Workload, "input.yaml"));
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
