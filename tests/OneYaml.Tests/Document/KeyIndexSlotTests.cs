using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>The account of which large mappings have a key index, when readers on several threads race to build them.</summary>
public class KeyIndexSlotTests
{
    [Fact]
    public void A_table_left_in_a_slot_by_a_reader_that_lost_a_race_does_not_keep_the_mapping_unindexed()
    {
        var cache = new MapCache();

        // Reader A starts on mapping 10 and takes the slot that has spent the least.
        int slotA = cache.Charge(10, 1, out _);
        for (int node = 100; node < 100 + MapCache.Slots - 1; node++)
        {
            Assert.True(cache.Charge(node, 500, out _) >= 0);
        }

        // Reader B finds every slot taken and gives the cheapest one, A's, to mapping 99.
        int slotB = cache.Charge(99, 5, out _);
        Assert.Equal(slotA, slotB);

        // A had already seen that the slot was its own; its table lands there although the slot now follows mapping 99.
        cache.Install(slotA, new KeyTable { Node = 10, Capacity = 64 });
        Assert.Null(cache.FindTable(99));

        // The next search through mapping 99 is charged again, and its table replaces the stale one.
        int slot = cache.Charge(99, 5, out long work);
        Assert.Equal(slotB, slot);
        Assert.Equal(10, work);
        var table = new KeyTable { Node = 99, Capacity = 64 };
        cache.Publish(slot, table);
        Assert.Same(table, cache.FindTable(99));
    }
}
