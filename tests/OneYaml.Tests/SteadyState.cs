using Xunit;

namespace OneYaml.Tests;

/// <summary>
/// Measurements that stay true on a busy machine: allocations are counted per thread, so other tests do not
/// add to them, and the wait for the runtime to finish optimising the code under test is bounded by a number of
/// rounds and not by a number of warm-up calls.
/// </summary>
internal static class SteadyState
{
    private const int MaxRounds = 3000;

    /// <summary>
    /// Requires that calling <paramref name="work"/> <paramref name="calls"/> times allocates nothing on this thread.
    /// </summary>
    /// <remarks>
    /// While the runtime still runs unoptimised code (it swaps in optimised code on a background thread that a busy
    /// machine starves for a while), the same calls allocate objects that the optimised code keeps on the stack. So
    /// a round that allocates is run again until the round limit, and the code passes after two rounds in a row that
    /// allocate nothing. Code that allocates on every call, or on any input the work covers, never reaches that and
    /// fails with the bytes of the last round.
    /// </remarks>
    /// <param name="work">The steady-state operation; it must cover all the inputs of interest.</param>
    /// <param name="calls">How many calls make up a round.</param>
    public static void AssertAllocatesNothing(Action work, int calls)
    {
        ArgumentNullException.ThrowIfNull(work);
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
        while (rounds < MaxRounds);

        Assert.Fail($"Still allocating after {rounds} rounds of {calls} calls; the last round allocated {last} bytes.");
    }
}
