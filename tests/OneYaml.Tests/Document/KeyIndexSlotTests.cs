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

        // The next search through mapping 99 is charged again. The stale table is taken out for good: nobody finds it, and
        // nothing answers for mapping 99 until a table for it is published.
        int slot = cache.Charge(99, 5, out long work);
        Assert.Equal(slotB, slot);
        Assert.Equal(10, work);
        Assert.Null(cache.TableAt(slot));
        Assert.Null(cache.FindTable(99));
        Assert.Null(cache.FindTable(10));
        var table = new KeyTable { Node = 99, Capacity = 64 };
        cache.Publish(slot, table);
        Assert.Same(table, cache.FindTable(99));
    }

    [Fact]
    public void A_table_left_in_a_free_slot_is_taken_out_when_the_slot_is_given_to_a_mapping()
    {
        var cache = new MapCache();

        // A reader that was slow installs its table in a slot that has been cleared since: the slot follows no mapping.
        cache.Install(0, new KeyTable { Node = 10, Capacity = 64 });
        Assert.Null(cache.FindTable(10));

        // The slot goes to mapping 50, and the leftover must not stay in it.
        int slot = cache.Charge(50, 5, out _);
        Assert.Equal(0, slot);
        Assert.Null(cache.TableAt(slot));
        Assert.Null(cache.FindTable(50));
        Assert.Null(cache.FindTable(10));
        var table = new KeyTable { Node = 50, Capacity = 64 };
        cache.Publish(slot, table);
        Assert.Same(table, cache.FindTable(50));
    }
}
