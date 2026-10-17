using Xunit;

namespace OneYaml.Tests;

// A type with more than 32 members: reading it without merge keys must not allocate for the sets that track the keys seen.
internal sealed class FortyMembers
{
    public int M0 { get; set; }

    public int M1 { get; set; }

    public int M2 { get; set; }

    public int M3 { get; set; }

    public int M4 { get; set; }

    public int M5 { get; set; }

    public int M6 { get; set; }

    public int M7 { get; set; }

    public int M8 { get; set; }

    public int M9 { get; set; }

    public int M10 { get; set; }

    public int M11 { get; set; }

    public int M12 { get; set; }

    public int M13 { get; set; }

    public int M14 { get; set; }

    public int M15 { get; set; }

    public int M16 { get; set; }

    public int M17 { get; set; }

    public int M18 { get; set; }

    public int M19 { get; set; }

    public int M20 { get; set; }

    public int M21 { get; set; }

    public int M22 { get; set; }

    public int M23 { get; set; }

    public int M24 { get; set; }

    public int M25 { get; set; }

    public int M26 { get; set; }

    public int M27 { get; set; }

    public int M28 { get; set; }

    public int M29 { get; set; }

    public int M30 { get; set; }

    public int M31 { get; set; }

    public int M32 { get; set; }

    public int M33 { get; set; }

    public int M34 { get; set; }

    public int M35 { get; set; }

    public int M36 { get; set; }

    public int M37 { get; set; }

    public int M38 { get; set; }

    public int M39 { get; set; }
}

[YamlSerializable(typeof(List<FortyMembers>))]
internal sealed partial class FortyContext : YamlSerializerContext
{
}

public class MemberSetAllocationTests
{
    private const int Items = 2000;

    private static readonly string Document = string.Concat(Enumerable.Repeat("- {m0: 1, m17: 2, m39: 3}\n", Items));

    // Allocation is counted on this thread only; the smallest of several rounds decides, so one-off work cannot.
    private static long PerObject(Func<List<FortyMembers>?> read)
    {
        long best = long.MaxValue;
        for (int round = 0; round < 8; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            List<FortyMembers>? list = read();
            best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / Items);
            Assert.Equal(Items, list!.Count);
        }

        return best;
    }

    // One object of 40 ints is 176 bytes, a list slot 8 more (up to 16 with growth). Two sets of 64 bytes each would show.
    private const long Limit = 208;

    [Fact]
    public void Reading_a_type_with_forty_members_allocates_only_the_object_by_reflection()
    {
        long perObject = PerObject(() => YamlSerializer.Deserialize<List<FortyMembers>>(Document));
        Assert.True(perObject <= Limit, $"Each object took {perObject} bytes.");
    }

    [Fact]
    public void Reading_a_type_with_forty_members_allocates_only_the_object_by_the_generator()
    {
        var info = (YamlTypeInfo<List<FortyMembers>>)new FortyContext().GetTypeInfo(typeof(List<FortyMembers>))!;
        long perObject = PerObject(() => YamlSerializer.Deserialize(Document, info));
        Assert.True(perObject <= Limit, $"Each object took {perObject} bytes.");
    }
}
