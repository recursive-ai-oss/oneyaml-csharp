using BenchmarkDotNet.Running;

namespace OneYaml.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet writes its reports under BenchmarkDotNet.Artifacts in the working
    // directory unless told otherwise, so every run passes --artifacts (see the README
    // in this directory) and the folder never lands in the repository.
    private static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
