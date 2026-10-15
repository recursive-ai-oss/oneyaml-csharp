using System.Security.Cryptography;
using OneYaml;

namespace OneYaml.Benchmarks;

/// <summary>
/// Prints a SHA-256 of what the generic dump writes for each workload, so two builds can be compared for
/// identical output: <c>--dump-hash name...</c> (all workloads in the directory when none is named).
/// </summary>
internal static class DumpHash
{
    public static void Print(string[] names)
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS") ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS.");
        if (names.Length == 0)
        {
            names = Directory.GetFiles(dir, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToArray();
        }

        foreach (string name in names)
        {
            byte[] input = File.ReadAllBytes(Path.Combine(dir, name + ".yaml"));
            string line = name;
            try
            {
                object? tree = YamlSerializer.Deserialize<object>(input);
                byte[] reflection = YamlSerializer.SerializeToUtf8Bytes(tree);
                if (Environment.GetEnvironmentVariable("ONEYAML_DUMP_OUT") is { } outDir)
                {
                    File.WriteAllBytes(Path.Combine(outDir, name + ".out.yaml"), reflection);
                }

                line += " refl=" + Hash(reflection);
                line += " str=" + Hash(System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(tree)));
                line += " gen=" + Hash(YamlSerializer.SerializeToUtf8Bytes(tree, OneYamlContext.Default.Object));
            }
            catch (YamlException e)
            {
                line += " " + e.Category;
            }

            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// Dumps one workload's tree again and again with OneYaml, VYaml and SharpYaml in turn and prints each one's best time
    /// of a batch. The best of many batches is steady on a busy machine, which a mean is not.
    /// </summary>
    public static void Loop(string name, int seconds)
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS") ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name + ".yaml"));
        object? tree = YamlSerializer.Deserialize<object>(bytes);
        object? vyaml = VYaml.Serialization.YamlSerializer.Deserialize<object>(bytes);
        var sharpLoad = new SharpYaml.YamlSerializerOptions { ReferenceHandling = SharpYaml.YamlReferenceHandling.Preserve };
        object? sharp = SharpYaml.YamlSerializer.Deserialize<object>(System.Text.Encoding.UTF8.GetString(bytes), sharpLoad);
        var sharpDump = new SharpYaml.YamlSerializerOptions();

        (string, Action)[] jobs =
        [
            ("oneyaml", () => YamlSerializer.SerializeToUtf8Bytes(tree)),
            ("oneyaml-gen", () => YamlSerializer.SerializeToUtf8Bytes(tree, OneYamlContext.Default.Object)),
            ("vyaml", () => VYaml.Serialization.YamlSerializer.Serialize(vyaml)),
            ("sharpyaml", () => System.Text.Encoding.UTF8.GetBytes(SharpYaml.YamlSerializer.Serialize(sharp, sharpDump))),
        ];

        var best = new double[jobs.Length];
        Array.Fill(best, double.MaxValue);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            for (int j = 0; j < jobs.Length; j++)
            {
                Action run = jobs[j].Item2;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < 10; i++)
                {
                    run();
                }

                best[j] = Math.Min(best[j], System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 100);
            }
        }

        Console.WriteLine($"{name,-26} " + string.Join(" ", jobs.Select((job, k) => $"{job.Item1}={best[k]:F1}")));
    }

    /// <summary>
    /// Dumps a typed model again and again with OneYaml (reflection and generated), VYaml and SharpYaml in turn and prints
    /// each one's best time of a batch and the bytes one dump allocates.
    /// </summary>
    public static void TypedLoop(string name, int seconds)
    {
        (byte[] bytes, _) = Workloads.Load(name);
        bool records = name.StartsWith("records", StringComparison.Ordinal);
        object model = records ? YamlSerializer.Deserialize<List<OneYamlKit.Bench.Models.Record>>(bytes)! : YamlSerializer.Deserialize<OneYamlKit.Bench.Models.Workflow>(bytes)!;

        (string, Func<object>)[] jobs = records
            ?
            [
                ("oneyaml", () => YamlSerializer.SerializeToUtf8Bytes((List<OneYamlKit.Bench.Models.Record>)model)),
                ("oneyaml-gen", () => YamlSerializer.SerializeToUtf8Bytes((List<OneYamlKit.Bench.Models.Record>)model, OneYamlContext.Default.ListRecord)),
                ("vyaml", () => VYaml.Serialization.YamlSerializer.Serialize((List<OneYamlKit.Bench.Models.Record>)model)),
                ("sharpyaml", () => System.Text.Encoding.UTF8.GetBytes(SharpYaml.YamlSerializer.Serialize((List<OneYamlKit.Bench.Models.Record>)model, OneYamlKit.Bench.Models.SharpYamlContext.Default.ListRecord))),
            ]
            :
            [
                ("oneyaml", () => YamlSerializer.SerializeToUtf8Bytes((OneYamlKit.Bench.Models.Workflow)model)),
                ("oneyaml-gen", () => YamlSerializer.SerializeToUtf8Bytes((OneYamlKit.Bench.Models.Workflow)model, OneYamlContext.Default.Workflow)),
                ("vyaml", () => VYaml.Serialization.YamlSerializer.Serialize((OneYamlKit.Bench.Models.Workflow)model)),
                ("sharpyaml", () => System.Text.Encoding.UTF8.GetBytes(SharpYaml.YamlSerializer.Serialize((OneYamlKit.Bench.Models.Workflow)model, OneYamlKit.Bench.Models.SharpYamlContext.Default.Workflow))),
            ];

        var best = new double[jobs.Length];
        var alloc = new long[jobs.Length];
        Array.Fill(best, double.MaxValue);
        for (int j = 0; j < jobs.Length; j++)
        {
            jobs[j].Item2();
            long before = GC.GetAllocatedBytesForCurrentThread();
            jobs[j].Item2();
            alloc[j] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            for (int j = 0; j < jobs.Length; j++)
            {
                Func<object> run = jobs[j].Item2;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < 10; i++)
                {
                    run();
                }

                best[j] = Math.Min(best[j], System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 100);
            }
        }

        Console.WriteLine($"{name,-14} " + string.Join(" ", jobs.Select((job, k) => $"{job.Item1}={best[k]:F1}us/{alloc[k] / 1024}K")));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];
}
