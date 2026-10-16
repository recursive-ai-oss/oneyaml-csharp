using System.Diagnostics;
using System.Security.Cryptography;
using OneYaml;
using OneYaml.Fuzzing;
using OneYaml.Tests.Fuzzing;

// Mutational fuzzing of the reader, the generic load and document, typed binding and patches. Usage:
//   OneYaml.Fuzz --target reader|doc|bind|patch --seconds 60 --seed 1 --out DIR
//   OneYaml.Fuzz --target T --replay FILE   runs one saved input under both limits and prints the outcome
//                [--max-runs N] [--max-classes 5] [--cap-ms 5000] [--slow-ms 1000] [--max-length 65536] [--plant]
// Seeds come from the directories named by ONEYAML_YTS_DIR, ONEYAML_KIT and ONEYAML_WORKLOADS.
// Every input runs under the default and the untrusted limits. A failure is shrunk and written to DIR as a .yaml
// next to a .txt with the details; the run stops after --max-classes distinct classes of failure.
internal static class Program
{
    private static long s_startedTicks;
    private static byte[] s_current = [];
    private static string s_outDirectory = ".";
    private static int s_capMs = 5000;
    private static int s_slowMs = 1000;

    private static int Main(string[] args)
    {
        string targetName = "reader";
        int seconds = 60;
        int seed = 1;
        int maxLength = 65536;
        int maxClasses = 5;
        long maxRuns = long.MaxValue;
        string? replay = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--plant")
            {
                FuzzTarget.PlantBugs = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("missing value for " + args[i]);
                return 2;
            }

            string value = args[++i];
            switch (args[i - 1])
            {
                case "--target": targetName = value; break;
                case "--replay": replay = value; break;
                case "--seconds": seconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--out": s_outDirectory = value; break;
                case "--cap-ms": s_capMs = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--slow-ms": s_slowMs = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-length": maxLength = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-classes": maxClasses = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-runs": maxRuns = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine("unknown option " + args[i - 1]);
                    return 2;
            }
        }

        FuzzTarget? target = targetName switch
        {
            "reader" => new ReaderTarget(),
            "doc" => new DocumentTarget(),
            "bind" => new BindTarget(),
            "patch" => new PatchTarget(),
            _ => null,
        };
        if (target is null)
        {
            Console.Error.WriteLine("unknown target " + targetName);
            return 2;
        }

        if (replay is not null)
        {
            byte[] saved = File.ReadAllBytes(replay);
            foreach (YamlLimits limits in new[] { YamlLimits.Default, YamlLimits.Untrusted })
            {
                (string? failure, Exception? error, bool ok) = Execute(target, saved, -1, limits);
                Console.WriteLine($"{(limits == YamlLimits.Untrusted ? "untrusted" : "default")}: {(failure is null ? "no failure" : failure)} accepted={ok}");
                if (error is not null)
                {
                    Console.WriteLine(error);
                }
            }

            return 0;
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

        // A small stack, as a Windows thread has: a recursion that follows input depth shows up here first.
        int result = 0;
        var worker = new Thread(() => result = Loop(target, seeds, seed, seconds, maxLength, maxClasses, maxRuns), 1024 * 1024);
        worker.Start();
        worker.Join();
        return result;
    }

    private static int Loop(FuzzTarget target, List<byte[]> seeds, int seed, int seconds, int maxLength, int maxClasses, long maxRuns)
    {
        var mutator = new Mutator(seed);
        var pool = new List<byte[]>(seeds);
        var hints = new List<int>(Enumerable.Repeat(-1, seeds.Count));
        foreach ((byte[] data, int hint) in target.ExtraSeeds())
        {
            pool.Add(data);
            hints.Add(hint);
        }

        var limitsChoices = new[] { YamlLimits.Default, YamlLimits.Untrusted };
        var classes = new HashSet<string>();
        long runs = 0;
        long accepted = 0;
        long rejected = 0;
        long bytes = 0;
        long slowChecked = 0;
        var clock = Stopwatch.StartNew();
        long nextReport = 1_000_000;
        while (clock.Elapsed.TotalSeconds < seconds && runs < maxRuns && classes.Count < maxClasses)
        {
            int at = mutator.Next(pool.Count);
            byte[] input = mutator.Mutate(pool[at], pool, maxLength);
            int hint = hints[at];
            YamlLimits limits = limitsChoices[mutator.Next(limitsChoices.Length)];
            s_current = input;
            (string? failure, Exception? error, bool ok) = Execute(target, input, hint, limits);
            if (failure is not null)
            {
                if (classes.Add(failure))
                {
                    byte[] small = Minimise(target, input, hint, limits, failure);
                    Save("fail", small, $"target {target.Name}, limits {(limits == YamlLimits.Untrusted ? "untrusted" : "default")}, hint {hint}, original {input.Length} bytes\nclass {failure}\n\n{error}");
                }
            }
            else
            {
                bytes += input.Length;
                if (ok)
                {
                    accepted++;
                    if (pool.Count < 4000 && input.Length < 8192)
                    {
                        pool.Add(input);
                        hints.Add(hint);
                    }
                }
                else
                {
                    rejected++;
                }
            }

            if (limits == YamlLimits.Untrusted)
            {
                slowChecked++;
            }

            runs++;
            if (runs >= nextReport)
            {
                nextReport += 1_000_000;
                Console.WriteLine($"{clock.Elapsed.TotalSeconds:F0}s runs={runs} accepted={accepted} rejected={rejected} pool={pool.Count} heapMB={GC.GetTotalMemory(false) >> 20} rssMB={Environment.WorkingSet >> 20} {target.Stats()} classes={classes.Count}");
            }
        }

        Console.WriteLine($"done {target.Name}: {runs} inputs ({bytes / (1024 * 1024)} MiB) in {clock.Elapsed.TotalSeconds:F0} s, {runs / Math.Max(clock.Elapsed.TotalSeconds, 1):F0}/s, accepted={accepted}, rejected={rejected}, untrustedRuns={slowChecked}, {target.Stats()}, failureClasses={classes.Count}");
        return classes.Count == 0 ? 0 : 1;
    }

    // Runs one input and names what went wrong, if anything. Under the untrusted limits an input of at most 64 KB that
    // takes longer than the slow bound, or allocates far more than its size, is a failure too: that is where
    // superlinear behaviour shows.
    private static (string? Failure, Exception? Error, bool Accepted) Execute(FuzzTarget target, byte[] input, int hint, YamlLimits limits)
    {
        Volatile.Write(ref s_startedTicks, Stopwatch.GetTimestamp());
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            target.BeginRun();
            bool accepted = target.Run(input, hint, limits);
            double ms = target.Measured ? target.LibraryMs : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = target.Measured ? target.LibraryBytes : GC.GetAllocatedBytesForCurrentThread() - before;
            bool untrusted = limits == YamlLimits.Untrusted;
            if (untrusted && input.Length <= 65536 && ms > s_slowMs)
            {
                return ($"slow:{target.Name}", new FuzzFailure("slow", $"{ms:F0} ms for {input.Length} bytes"), accepted);
            }

            if ((untrusted || target.CapAlways) && allocated > (target.AllocationFactor * input.Length) + target.AllocationFloor)
            {
                return ($"allocation:{target.Name}", new FuzzFailure("allocation", $"allocated {allocated} bytes for {input.Length} bytes of input"), accepted);
            }

            return (null, null, accepted);
        }
        catch (FuzzFailure ex)
        {
            return (ex.FailureClass, ex, false);
        }
        catch (InvariantViolationException ex)
        {
            return ("invariant:" + new string(ex.Message.Where(c => !char.IsDigit(c)).Take(60).ToArray()), ex, false);
        }
        catch (OutOfMemoryException ex) when (limits != YamlLimits.Untrusted)
        {
            // The contract allows it under the default limits; under the untrusted preset it is a finding.
            return (null, ex, false);
        }
        catch (Exception ex)
        {
            string top = (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return ("exception:" + ex.GetType().Name + ":" + top, ex, false);
        }
        finally
        {
            Volatile.Write(ref s_startedTicks, 0);
        }
    }

    // Removes chunks, big first, while the same class of failure remains.
    private static byte[] Minimise(FuzzTarget target, byte[] input, int hint, YamlLimits limits, string failure)
    {
        var budget = Stopwatch.StartNew();
        byte[] best = input;
        int chunk = Math.Max(best.Length / 2, 1);
        while (chunk >= 1 && budget.Elapsed.TotalSeconds < 90)
        {
            bool removed = false;
            for (int start = 0; start < best.Length && budget.Elapsed.TotalSeconds < 90;)
            {
                int length = Math.Min(chunk, best.Length - start);
                byte[] candidate = [.. best.AsSpan(0, start), .. best.AsSpan(start + length)];
                if (Execute(target, candidate, hint, limits).Failure == failure)
                {
                    best = candidate;
                    removed = true;
                }
                else
                {
                    start += length;
                }
            }

            if (!removed || chunk == 1)
            {
                chunk /= 2;
            }
        }

        return best;
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
