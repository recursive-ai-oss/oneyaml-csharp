using System.Diagnostics;
using System.Security.Cryptography;
using OneYaml;
using OneYaml.Tests.Fuzzing;

// Mutational fuzzing of the reader. Usage:
//   OneYaml.Fuzz --seconds 60 --seed 1 --out DIR [--cap-ms 3000] [--max-length 65536]
// Seeds come from the directories named by ONEYAML_YTS_DIR, ONEYAML_KIT and ONEYAML_WORKLOADS.
// Inputs that break an invariant, throw something other than YamlException, or run past the
// time cap are written to DIR and reported; the run goes on after the first two kinds.
internal static class Program
{
    private static long s_startedTicks;
    private static byte[] s_current = [];
    private static string s_outDirectory = ".";
    private static int s_capMs = 3000;

    private static int Main(string[] args)
    {
        int seconds = 60;
        int seed = 1;
        int maxLength = 65536;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--seconds":
                    seconds = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--out":
                    s_outDirectory = args[i + 1];
                    break;
                case "--cap-ms":
                    s_capMs = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--max-length":
                    maxLength = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    Console.Error.WriteLine("unknown option " + args[i]);
                    return 2;
            }
        }

        Directory.CreateDirectory(s_outDirectory);
        List<byte[]> seeds = LoadSeeds(maxLength);
        if (seeds.Count == 0)
        {
            Console.Error.WriteLine("no seed inputs: set ONEYAML_YTS_DIR, ONEYAML_KIT or ONEYAML_WORKLOADS");
            return 2;
        }

        var watchdog = new Thread(Watch) { IsBackground = true, Name = "watchdog" };
        watchdog.Start();

        var mutator = new Mutator(seed);
        var pool = new List<byte[]>(seeds);
        var limitsChoices = new[] { YamlLimits.Default, YamlLimits.Untrusted };
        var failures = new HashSet<string>();
        long runs = 0;
        long accepted = 0;
        long rejected = 0;
        long bytes = 0;
        long encodingChecks = 0;
        int problems = 0;
        var clock = Stopwatch.StartNew();
        long nextReport = 5_000_000;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] baseInput = pool[mutator.Next(pool.Count)];
            byte[] input = mutator.Mutate(baseInput, seeds, maxLength);
            YamlLimits limits = limitsChoices[mutator.Next(limitsChoices.Length)];
            s_current = input;
            Volatile.Write(ref s_startedTicks, Stopwatch.GetTimestamp());
            try
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                ReaderInvariants.Outcome outcome = ReaderInvariants.Check(input, limits);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                if (allocated > (200L * input.Length) + (4L << 20))
                {
                    throw new InvariantViolationException($"allocated {allocated} bytes for {input.Length} bytes of input");
                }

                bytes += input.Length;
                if (outcome.Accepted)
                {
                    accepted++;
                    if (pool.Count < 4000 && input.Length < 8192)
                    {
                        pool.Add(input);
                    }
                }
                else
                {
                    rejected++;
                }

                // The same text in another encoding must give the same events.
                if (limits == YamlLimits.Default && (runs & 7) == 0)
                {
                    int variant = mutator.Next(8);
                    byte[]? other = Mutator.Transcode(input, variant);
                    bool detectable = (variant & 1) == 0 || (input.Length > 0 && input[0] is > 0 and < 0x80);
                    if (other is not null && detectable && !input.Contains((byte)0)
                        && !(input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF))
                    {
                        s_current = other;
                        ReaderInvariants.Outcome again = ReaderInvariants.Check(other, limits);
                        encodingChecks++;
                        if (again.Accepted != outcome.Accepted || (outcome.Accepted && again.Signature != outcome.Signature))
                        {
                            throw new InvariantViolationException($"encoding variant {variant} reads differently (accepted {outcome.Accepted} vs {again.Accepted})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string key = ex.GetType().Name + ":" + (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault();
                if (failures.Add(key))
                {
                    problems++;
                    Save("crash", s_current, ex.ToString());
                }
            }

            runs++;
            if (runs >= nextReport)
            {
                nextReport += 5_000_000;
                Console.WriteLine($"{clock.Elapsed.TotalSeconds:F0}s runs={runs} accepted={accepted} rejected={rejected} pool={pool.Count} encodingChecks={encodingChecks} problems={problems}");
            }
        }

        Console.WriteLine($"done: {runs} inputs ({bytes / (1024 * 1024)} MiB) in {clock.Elapsed.TotalSeconds:F0} s, accepted={accepted}, rejected={rejected}, encodingChecks={encodingChecks}, problems={problems}");
        return problems == 0 ? 0 : 1;
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(250);
            long started = Volatile.Read(ref s_startedTicks);
            if (started != 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > s_capMs)
            {
                Save("hang", s_current, "no result after " + s_capMs + " ms");
                Console.WriteLine("hang: input saved, stopping");
                Environment.Exit(3);
            }
        }
    }

    private static void Save(string kind, byte[] input, string detail)
    {
        string name = kind + "-" + Convert.ToHexString(SHA256.HashData(input))[..12].ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(s_outDirectory, name + ".yaml"), input);
        File.WriteAllText(Path.Combine(s_outDirectory, name + ".txt"), detail);
        Console.WriteLine($"{kind}: {name} ({input.Length} bytes)");
    }

    private static List<byte[]> LoadSeeds(int maxLength)
    {
        var seeds = new List<byte[]>();
        AddFiles(seeds, Environment.GetEnvironmentVariable("ONEYAML_YTS_DIR"), "in.yaml", maxLength);
        string? kit = Environment.GetEnvironmentVariable("ONEYAML_KIT");
        if (kit is not null)
        {
            AddFiles(seeds, Path.Combine(kit, "suites", "own", "cases"), "input.yaml", maxLength);
        }

        AddFiles(seeds, Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS"), "*.yaml", maxLength);
        return seeds;
    }

    private static void AddFiles(List<byte[]> seeds, string? directory, string pattern, int maxLength)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Length <= maxLength)
            {
                seeds.Add(File.ReadAllBytes(file));
            }
        }
    }
}
