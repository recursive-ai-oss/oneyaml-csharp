using BenchmarkDotNet.Running;

namespace OneYaml.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet writes its reports under BenchmarkDotNet.Artifacts in the working
    // directory unless told otherwise, so every run passes --artifacts (see the README
    // in this directory) and the folder never lands in the repository.
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--retained")
        {
            RetainedMemory.Print(args.Skip(1).ToArray());
            return 0;
        }

        if (args.Length > 0 && args[0] == "--emit-hash")
        {
            UnchangedEmitBenchmarks.PrintHashes(args[1]);
            return 0;
        }

        if (args.Length > 0 && args[0] == "--dump-hash")
        {
            DumpHash.Print(args.Skip(1).ToArray());
            return 0;
        }

        if (args.Length > 0 && args[0] == "--dump-loop")
        {
            DumpHash.Loop(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }

        if (args.Length > 0 && args[0] == "--load-loop")
        {
            LoadLoop.Loop(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }

        if (args.Length > 0 && args[0] == "--load-hash")
        {
            LoadLoop.Hash(args.Skip(1).ToArray());
            return 0;
        }

        if (args.Length > 0 && args[0] == "--typed-loop")
        {
            DumpHash.TypedLoop(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
