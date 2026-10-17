using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// A run of edits on one large document: the offsets of the nodes behind an edit are not all rewritten each
/// time, and the result is still what a fresh parse of the same bytes gives.
/// </summary>
public class HeldBackMoveTests
{
    private static string Mapping(int keys)
    {
        var sb = new StringBuilder("# settings\nname: demo\nbig:\n");
        for (int i = 0; i < keys; i++)
        {
            sb.Append("  key").Append(i).Append(": value").Append(i).Append('\n');
            if (i % 97 == 0)
            {
                sb.Append("  # a note after ").Append(i).Append('\n');
            }
        }

        sb.Append("tail:\n  - one\n  - two: 2\n    three: [3, 4]\n");
        return sb.ToString();
    }

    private static string Patch(int keys, int ops, int seed, params string[] extra)
    {
        var sb = new StringBuilder("[");
        var rnd = new Random(seed);
        var used = new HashSet<int>();
        for (int i = 0; used.Count < ops; i++)
        {
            int k = rnd.Next(keys);
            if (!used.Add(k))
            {
                continue;
            }

            if (sb.Length > 1)
            {
                sb.Append(',');
            }

            // Values of every length around the old one, so that nodes behind the edit move both ways.
            sb.Append("{\"op\":\"replace\",\"path\":\"/big/key").Append(k).Append("\",\"value\":\"").Append('x', 1 + ((i * 7) % 23)).Append("\"}");
        }

        foreach (string e in extra)
        {
            sb.Append(',').Append(e);
        }

        return sb.Append(']').ToString();
    }

    // Offsets rewritten one by one while a patch of replaces runs on a mapping of the given size.
    private static long NodesMoved(int keys, int ops)
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(keys));
        string patch = Patch(keys, ops, 11);
        EditWork.NodesMoved = 0;
        doc.ApplyPatch(patch);
        return EditWork.NodesMoved;
    }

    [Fact]
    public void The_offsets_rewritten_per_replace_do_not_grow_with_the_size_of_the_document()
    {
        const int Few = 100;
        const int Many = 400;

        // The check of the caches after each edit reads every node of a small mapping, which brings everything up to date.
        bool verify = DocState.VerifyCaches;
        DocState.VerifyCaches = false;
        try
        {
            foreach (int keys in new[] { 3_000, 40_000 })
            {
                double perOp = (NodesMoved(keys, Many) - NodesMoved(keys, Few)) / (double)(Many - Few);

                // A binary search and a handful of neighbours bring a few chunks of nodes up to date; the other 80,000 nodes
                // of the large document are not touched.
                Assert.True(perOp <= 2_500, $"{perOp:F0} node offsets rewritten per replace on {keys} keys");
            }
        }
        finally
        {
            DocState.VerifyCaches = verify;
        }
    }

    [Theory]
    [InlineData(3_000, 200)]
    [InlineData(20_000, 600)]
    public void The_state_after_a_run_of_replaces_is_that_of_a_fresh_parse_of_its_bytes(int keys, int ops)
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(keys));
        doc.ApplyPatch(Patch(keys, ops, 5));
        DocState fresh = DocState.Build(doc.State.Text, doc.State.Encoding, doc.State.HasByteOrderMark, YamlLimits.Default, doc.State.AllowDuplicateKeys);
        try
        {
            Assert.Null(StateEquality.Diff(doc.State, fresh));
        }
        finally
        {
            fresh.Dispose();
        }
    }

    [Fact]
    public void Replaces_mixed_with_removes_adds_and_collection_values_give_the_bytes_of_one_parse_per_operation()
    {
        const int Keys = 6_000;
        string extra1 = "{\"op\":\"remove\",\"path\":\"/big/key17\"}";
        string extra2 = "{\"op\":\"add\",\"path\":\"/big/fresh\",\"value\":{\"a\":[1,2,{\"b\":null}]}}";
        string extra3 = "{\"op\":\"replace\",\"path\":\"/big/key5000\",\"value\":[\"p\",\"q\"]}";
        string extra4 = "{\"op\":\"replace\",\"path\":\"/tail/1/two\",\"value\":\"a considerably longer value than before\"}";
        string extra5 = "{\"op\":\"replace\",\"path\":\"/big/key5999\",\"value\":\"z\"}";
        string patch = Patch(Keys, 300, 21, extra1, extra2, extra3, extra4, extra5);

        using YamlDocument kept = YamlDocument.Parse(Mapping(Keys));
        kept.ApplyPatch(patch);

        // The reference parses the whole text again after every operation.
        using YamlDocument reference = YamlDocument.Parse(Mapping(Keys));
        DocState.NoSplice = true;
        try
        {
            reference.ApplyPatch(patch);
        }
        finally
        {
            DocState.NoSplice = false;
        }

        Assert.Equal(Encoding.UTF8.GetString(reference.ToUtf8Bytes()), Encoding.UTF8.GetString(kept.ToUtf8Bytes()));
        Assert.Equal("a considerably longer value than before", kept.Get<string>("/tail/1/two"));
        Assert.Equal("z", kept.Get<string>("/big/key5999"));
        Assert.Equal("q", kept.Get<string>("/big/key5000/1"));
    }

    [Fact]
    public void A_patch_that_fails_after_many_replaces_leaves_the_document_as_it_was_and_usable()
    {
        const int Keys = 5_000;
        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        byte[] before = doc.ToUtf8Bytes();
        string failing = Patch(Keys, 250, 3, "{\"op\":\"test\",\"path\":\"/name\",\"value\":\"not demo\"}");
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(failing));
        Assert.Equal(250, ex.OperationIndex);
        Assert.Equal(before, doc.ToUtf8Bytes());

        // The same patch without the failing step, on the document that saw the failure and on a fresh one.
        string good = Patch(Keys, 250, 3);
        doc.ApplyPatch(good);
        using YamlDocument fresh = YamlDocument.Parse(Mapping(Keys));
        fresh.ApplyPatch(good);
        Assert.Equal(fresh.ToUtf8Bytes(), doc.ToUtf8Bytes());
    }

    [Fact]
    public void Views_taken_before_a_run_of_replaces_read_the_right_values_afterwards()
    {
        const int Keys = 8_000;
        string first = Patch(Keys, 400, 9);
        string second = Patch(Keys, 300, 13, "{\"op\":\"replace\",\"path\":\"/tail/1/two\",\"value\":\"changed\"}");

        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        YamlNode last = doc.Root["big"]["key7999"];
        YamlNode tail = doc.Root["tail"];
        YamlNode three = doc.Root["tail"][1]["three"];
        doc.ApplyPatch(first);
        Assert.Equal("value7999", last.GetValue<string>());
        Assert.Equal(2, tail[1]["two"].GetValue<int>());

        // The reference parses the whole text again after every operation.
        using YamlDocument reference = YamlDocument.Parse(Mapping(Keys));
        YamlNode refLast = reference.Root["big"]["key7999"];
        YamlNode refTail = reference.Root["tail"];
        YamlNode refThree = reference.Root["tail"][1]["three"];
        DocState.NoSplice = true;
        try
        {
            reference.ApplyPatch(first);
        }
        finally
        {
            DocState.NoSplice = false;
        }

        // Positions and whole values come from the offsets of the nodes, which a move held back must not leave behind.
        Assert.Equal(refLast.Position, last.Position);
        Assert.Equal(refTail.Position, tail.Position);
        Assert.Equal(refThree.Position, three.Position);
        Assert.Equal(Canon(refTail.Deserialize<object>()), Canon(tail.Deserialize<object>()));
        Assert.Equal(Canon(reference.Root["big"].Deserialize<object>()), Canon(doc.Root["big"].Deserialize<object>()));

        // A second patch on the published document must write where a fresh parse would.
        doc.ApplyPatch(second);
        DocState.NoSplice = true;
        try
        {
            reference.ApplyPatch(second);
        }
        finally
        {
            DocState.NoSplice = false;
        }

        Assert.Equal(Encoding.UTF8.GetString(reference.ToUtf8Bytes()), Encoding.UTF8.GetString(doc.ToUtf8Bytes()));
        Assert.Equal(refLast.Position, last.Position);
        Assert.Equal(Canon(reference.Root.Deserialize<object>()), Canon(doc.Root.Deserialize<object>()));
    }

    private static string Canon(object? value) => System.Text.Json.JsonSerializer.Serialize(value);
}
