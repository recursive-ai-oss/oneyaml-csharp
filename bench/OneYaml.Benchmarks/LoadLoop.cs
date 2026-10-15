using System.Security.Cryptography;
using System.Text;
using OneYaml;

namespace OneYaml.Benchmarks;

/// <summary>
/// Generic-load comparison for a busy machine. <c>--load-loop name seconds</c> loads one workload again and again
/// with OneYaml (reflection and generated), VYaml and SharpYaml in turn, doing what the kit's harness does for the
/// generic-load task, and prints each one's best time of a batch and the bytes one load allocates.
/// <c>--load-hash name...</c> prints a hash of the loaded value graph, so two builds can be checked for identical values.
/// </summary>
internal static class LoadLoop
{
    private static readonly SharpYaml.YamlSerializerOptions s_sharpLoad = new() { ReferenceHandling = SharpYaml.YamlReferenceHandling.Preserve };

    private static (string, Func<object?>)[] Jobs(byte[] bytes)
    {
        bool multi = Encoding.UTF8.GetString(bytes).Contains("\n---", StringComparison.Ordinal);
        if (multi)
        {
            return
            [
                ("oneyaml", () => YamlSerializer.DeserializeAll<object>(bytes)),
                ("oneyaml-gen", () => YamlSerializer.DeserializeAll(Encoding.UTF8.GetString(bytes), OneYamlContext.Default.Object)),
                ("oneyaml-gen-bytes", () => YamlSerializer.DeserializeAll(bytes, OneYamlContext.Default.Object)),
                ("vyaml", () => VYaml.Serialization.YamlSerializer.DeserializeMultipleDocuments<object>(bytes).ToList()),
            ];
        }

        return
        [
            ("oneyaml", () => YamlSerializer.Deserialize<object>(bytes)),
            ("oneyaml-gen", () => YamlSerializer.Deserialize(bytes, OneYamlContext.Default.Object)),
            ("vyaml", () => VYaml.Serialization.YamlSerializer.Deserialize<object>(bytes)),
            ("sharpyaml", () => SharpYaml.YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(bytes), s_sharpLoad)),
        ];
    }

    // A competitor may refuse a workload (SharpYaml stops at nesting depth 64); it then has no row.
    private static bool Works(Func<object?> run)
    {
        try
        {
            run();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Loop(string name, int seconds)
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS") ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name + ".yaml"));
        (string, Func<object?>)[] jobs = Jobs(bytes).Where(job => Works(job.Item2)).ToArray();
        var best = new double[jobs.Length];
        var alloc = new long[jobs.Length];
        var reps = new int[jobs.Length];
        Array.Fill(best, double.MaxValue);
        for (int j = 0; j < jobs.Length; j++)
        {
            for (int i = 0; i < 30; i++)
            {
                jobs[j].Item2();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            jobs[j].Item2();
            alloc[j] = GC.GetAllocatedBytesForCurrentThread() - before;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++)
            {
                jobs[j].Item2();
            }

            double each = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / 20;
            reps[j] = (int)Math.Clamp(4 / Math.Max(each, 0.001), 1, 2000);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            for (int j = 0; j < jobs.Length; j++)
            {
                Func<object?> run = jobs[j].Item2;
                int n = reps[j];
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++)
                {
                    run();
                }

                best[j] = Math.Min(best[j], System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000 / n);
            }
        }

        Console.WriteLine($"{name,-26} " + string.Join(" ", jobs.Select((job, k) => $"{job.Item1}={best[k]:F2}us/{alloc[k] / 1024.0:F1}K")));
    }

    public static void Hash(string[] names)
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
            foreach ((string label, Func<object?> run) in new (string, Func<object?>)[]
            {
                ("refl", () => YamlSerializer.DeserializeAll<object>(input)),
                ("gen", () => YamlSerializer.DeserializeAll(Encoding.UTF8.GetString(input), OneYamlContext.Default.Object)),
                ("str", () => YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(input))),
            })
            {
                try
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Walk(sha, run());
                    line += $" {label}=" + Convert.ToHexString(sha.GetHashAndReset())[..16];
                }
                catch (YamlException e)
                {
                    line += $" {label}=" + e.Category;
                }
            }

            Console.WriteLine(line);
        }
    }

    // Type tags and lengths go in with every value, so a long that became a double changes the hash.
    private static void Walk(IncrementalHash sha, object? value)
    {
        switch (value)
        {
            case null: Put(sha, "n;"); break;
            case string s: Put(sha, "s" + s.Length + ":" + s + ";"); break;
            case bool b: Put(sha, b ? "T;" : "F;"); break;
            case long l: Put(sha, "l" + l + ";"); break;
            case double d: Put(sha, "d" + BitConverter.DoubleToInt64Bits(d) + ";"); break;
            case System.Numerics.BigInteger g: Put(sha, "g" + g + ";"); break;
            case Dictionary<string, object?> map:
                Put(sha, "{" + map.Count + ":");
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    Put(sha, "k" + pair.Key.Length + ":" + pair.Key + "=");
                    Walk(sha, pair.Value);
                }

                Put(sha, "};");
                break;
            case System.Collections.IList list:
                Put(sha, "[" + list.Count + ":");
                foreach (object? item in list)
                {
                    Walk(sha, item);
                }

                Put(sha, "];");
                break;
            default: Put(sha, "?" + value.GetType().FullName + ";"); break;
        }
    }

    private static void Put(IncrementalHash sha, string text) => sha.AppendData(Encoding.UTF8.GetBytes(text));
}
