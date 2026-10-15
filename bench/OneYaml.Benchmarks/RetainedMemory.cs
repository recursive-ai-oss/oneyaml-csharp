using System.Text;
using SharpYaml.Syntax;

namespace OneYaml.Benchmarks;

/// <summary>
/// Prints the managed memory a parsed document keeps alive, for the lossless document and for
/// SharpYaml's syntax tree: <c>-- --retained name…</c> with workload names.
/// </summary>
internal static class RetainedMemory
{
    public static void Print(string[] workloads)
    {
        string dir = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS")
            ?? throw new InvalidOperationException("Set ONEYAML_WORKLOADS to the directory holding the workload files.");
        Console.WriteLine("workload | input bytes | OneYaml retained | SharpYaml retained | ratio | OneYaml / input");
        foreach (string w in workloads)
        {
            string flat = Path.Combine(dir, w + ".yaml");
            byte[] bytes = File.ReadAllBytes(File.Exists(flat) ? flat : Path.Combine(dir, "workloads", w, "input.yaml"));
            string text = Encoding.UTF8.GetString(bytes);
            long one = Retained(() => YamlDocument.Parse(bytes));
            long sharp = Retained(() => YamlSyntaxTree.Parse(text, new YamlSyntaxOptions { IncludeTrivia = true }));
            Console.WriteLine($"{w} | {bytes.Length} | {one} | {sharp} | {(double)one / sharp:F3} | {(double)one / bytes.Length:F2}");
        }
    }

    // The growth of the live heap while the object is held, after full collections.
    private static long Retained(Func<object> make)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        object held = make();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(held);
        return after - before;
    }
}
