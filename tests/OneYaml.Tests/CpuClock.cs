using System.Diagnostics;
using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// Measures the processor time a piece of work uses. Wall-clock time on a shared runner also counts the time the
/// work waited for a core, which says nothing about the code; processor time still grows with any quadratic step.
/// </summary>
internal readonly struct CpuClock
{
    private readonly TimeSpan _start;

    private CpuClock(TimeSpan start) => _start = start;

    public static CpuClock Start()
    {
        using Process process = Process.GetCurrentProcess();
        return new CpuClock(process.TotalProcessorTime);
    }

    public TimeSpan Elapsed
    {
        get
        {
            using Process process = Process.GetCurrentProcess();
            return process.TotalProcessorTime - _start;
        }
    }
}

/// <summary>
/// Tests that bound processor time run alone, after the parallel tests of this assembly have finished, so the
/// process time they read is their own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingGroup
{
    public const string Name = "Timing";
}
