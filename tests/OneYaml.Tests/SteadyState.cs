using System.Diagnostics;
using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// Measurements that stay true on a busy machine: allocations are counted per thread, so other tests do not
/// add to them, and the wait for the runtime to finish optimising the code under test is bounded by a deadline
/// and not by a number of warm-up calls.
/// </summary>
internal static class SteadyState
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Requires that calling <paramref name="work"/> <paramref name="calls"/> times allocates nothing on this thread.
    /// </summary>
    /// <remarks>
    /// While the runtime still runs unoptimised code (it swaps in optimised code on a background thread that a busy
    /// machine starves for a while), the same calls allocate objects that the optimised code keeps on the stack. So
    /// a round that allocates is run again until the deadline, and the code passes after two rounds in a row that
    /// allocate nothing. Code that allocates on every call, or on any input the work covers, never reaches that and
    /// fails with the bytes of the last round.
    /// </remarks>
    /// <param name="work">The steady-state operation; it must cover all the inputs of interest.</param>
    /// <param name="calls">How many calls make up a round.</param>
    public static void AssertAllocatesNothing(Action work, int calls)
    {
        ArgumentNullException.ThrowIfNull(work);
        var clock = Stopwatch.StartNew();
        long last;
        int quiet = 0;
        int rounds = 0;
        do
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++)
            {
                work();
            }

            last = GC.GetAllocatedBytesForCurrentThread() - before;
            rounds++;
            if (last == 0)
            {
                if (++quiet == 2)
                {
                    return;
                }
            }
            else
            {
                quiet = 0;
                Thread.Sleep(10);
            }
        }
        while (clock.Elapsed < s_deadline);

        Assert.Fail($"Still allocating after {rounds} rounds of {calls} calls in {clock.Elapsed.TotalSeconds:F0} s; the last round allocated {last} bytes.");
    }

    /// <summary>The shortest of <paramref name="attempts"/> runs of <paramref name="work"/>, which a busy machine inflates least.</summary>
    public static TimeSpan Fastest(Action work, int attempts)
    {
        ArgumentNullException.ThrowIfNull(work);
        TimeSpan best = TimeSpan.MaxValue;
        for (int i = 0; i < attempts; i++)
        {
            long start = Stopwatch.GetTimestamp();
            work();
            TimeSpan took = Stopwatch.GetElapsedTime(start);
            if (took < best)
            {
                best = took;
            }
        }

        return best;
    }
}
