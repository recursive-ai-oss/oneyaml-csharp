namespace OneYaml.Fuzzing;

/// <summary>A broken rule found by a target, named by a stable class so repeats of one problem count once.</summary>
internal sealed class FuzzFailure : Exception
{
    public FuzzFailure(string failureClass, string message)
        : base(message)
    {
        FailureClass = failureClass;
    }

    public string FailureClass { get; }
}

/// <summary>One surface under test. A target must be deterministic in its input, hint and limits, so a failure can be shrunk.</summary>
internal abstract class FuzzTarget
{
    public abstract string Name { get; }

    /// <summary>Whether the allocation cap applies under every limits preset, not only the untrusted one.</summary>
    public virtual bool CapAlways => false;

    /// <summary>Bytes allowed per input byte before the run counts as amplifying memory.</summary>
    public virtual long AllocationFactor => 500;

    // Aliases may legitimately expand to the untrusted preset's budget (100,000 nodes, 8 MiB), so the floor sits above that.
    public virtual long AllocationFloor => 64L << 20;

    /// <summary>Inputs that belong to one model, with the model's index as a hint.</summary>
    public virtual IEnumerable<(byte[] Data, int Hint)> ExtraSeeds() => [];

    /// <summary>Runs one input. Returns whether the input was accepted as valid; throws on a broken rule.</summary>
    public abstract bool Run(byte[] input, int hint, YamlLimits limits);

    public virtual string Stats() => string.Empty;

    /// <summary>Allocation and time spent inside the library, when the target brackets its calls with <see cref="Library"/>; otherwise whole-run figures are used.</summary>
    public bool Measured { get; private set; }

    public long LibraryBytes { get; private set; }

    public double LibraryMs { get; private set; }

    public void BeginRun()
    {
        Measured = false;
        LibraryBytes = 0;
        LibraryMs = 0;
    }

    /// <summary>Brackets a call into the library, so the oracle's own cost is not charged to it.</summary>
    internal LibraryScope Library() => new(this);

    internal readonly struct LibraryScope : IDisposable
    {
        private readonly FuzzTarget _target;
        private readonly long _bytes;
        private readonly long _ticks;

        public LibraryScope(FuzzTarget target)
        {
            _target = target;
            _bytes = GC.GetAllocatedBytesForCurrentThread();
            _ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            _target.Measured = true;
            _target.LibraryBytes += GC.GetAllocatedBytesForCurrentThread() - _bytes;
            _target.LibraryMs += System.Diagnostics.Stopwatch.GetElapsedTime(_ticks).TotalMilliseconds;
        }
    }

    /// <summary>Set by the self-check option: targets then corrupt their own results for some inputs, and the run must report them.</summary>
    public static bool PlantBugs { get; set; }

    protected static bool Plant(byte[] input) => PlantBugs && Seeded(input, 0).Next(64) == 0;

    /// <summary>A random source that depends only on the input, so a rerun makes the same choices.</summary>
    protected static Random Seeded(byte[] input, int hint)
    {
        ulong h = 14695981039346656037UL ^ (ulong)hint;
        foreach (byte b in input)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        return new Random((int)(h ^ (h >> 32)));
    }
}
