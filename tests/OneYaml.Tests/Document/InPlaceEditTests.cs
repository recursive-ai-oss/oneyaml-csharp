using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Single edits made through the document's own methods work on the document's state instead of a copy of it. A loop of
/// them then costs what the edits touch, and a failed edit must still leave the document exactly as it was.
/// </summary>
public class InPlaceEditTests
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

        sb.Append("tail:\n  - one\n  - two: 2\n    three: [3, 4]\nflow: {p: 1, q: [1, 2]}\n");
        return sb.ToString();
    }

    private static string Reference(YamlDocument doc) => Encoding.UTF8.GetString(doc.ToUtf8Bytes());

    [Fact]
    public void A_loop_of_single_edits_copies_no_state()
    {
        const int Keys = 3_000;
        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        YamlNode big = doc.Root["big"];
        var rnd = new Random(4);
        EditWork.StateClones = 0;
        for (int i = 0; i < 400; i++)
        {
            int k = rnd.Next(Keys);
            switch (i % 8)
            {
                case 0:
                    doc.Set("/big/key" + k, "pointer " + new string('x', rnd.Next(30)));
                    break;
                case 1:
                    big["key" + k] = i;
                    break;
                case 2:
                    big["added" + i] = "new";
                    break;
                case 3:
                    doc.Remove("/big/added" + (i - 1));
                    break;
                case 4:
                    big["key" + k].Set(new[] { 1, 2, i });
                    break;
                case 5:
                    doc.Root["tail"][1]["two"] = i;
                    break;
                case 6:
                    doc.Add("/tail/-", "item " + i);
                    break;
                default:
                    doc.Root["tail"][doc.Root["tail"].Count - 1].Remove();
                    break;
            }
        }

        Assert.Equal(0, EditWork.StateClones);
        Assert.False(doc.State.Exclusive);
    }

    [Fact]
    public void A_loop_of_single_edits_gives_the_bytes_of_one_parse_per_edit_and_views_that_agree()
    {
        const int Keys = 2_500;
        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        using YamlDocument reference = YamlDocument.Parse(Mapping(Keys));
        YamlNode big = doc.Root["big"];
        YamlNode refBig = reference.Root["big"];
        YamlNode two = doc.Root["tail"][1]["two"];
        YamlNode refTwo = reference.Root["tail"][1]["two"];
        var rnd = new Random(77);
        EditWork.StateClones = 0;
        for (int i = 0; i < 300; i++)
        {
            int k = rnd.Next(Keys);
            string text = "v" + i + new string('y', rnd.Next(40));
            Action<YamlDocument, YamlNode> op = (i % 6) switch
            {
                0 => (d, _) => d.Set("/big/key" + k, text),
                1 => (_, b) => b["key" + k] = text,
                2 => (d, _) => d.Set("/big/new" + i, new Dictionary<string, object?> { ["a"] = i, ["b"] = new[] { "x", text } }),
                3 => (d, _) => d.Remove("/big/new" + (i - 1)),
                4 => (_, b) => b["key" + k].Set(i % 2 == 0 ? text : (object)i),
                _ => (d, _) => d.Add("/flow/r" + i, text),
            };

            op(doc, big);

            // The reference parses the whole text again after every edit.
            DocState.NoSplice = true;
            try
            {
                op(reference, refBig);
            }
            finally
            {
                DocState.NoSplice = false;
            }

            Assert.Equal(Reference(reference), Reference(doc));
            Assert.Equal(refBig.Position, big.Position);
            Assert.Equal(refTwo.Position, two.Position);
            Assert.Equal(refBig.Count, big.Count);
        }

        Assert.Equal(0, EditWork.StateClones);
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

    private static readonly int[] Stages = [1, 2, 3];
    private static readonly int[] OneTwo = [1, 2];
    private static readonly int[] OneTwoThree = [1, 2, 3];

    private const string Small = "# head\nname: demo  # c\nitems:\n  - a: 1\n    b: 2\n  - a: 3\n    b: [x, y]\nmap: {p: 1, q: 2}\nseq: [1, 2, 3]\nlong: |\n  text\n  more\nlast: end\n";
    private const string Anchored = "base: &b {x: 1}\nuse: *b\nother: 2\nlist:\n  - &i 5\n  - *i\n";

    private static IEnumerable<(string Name, string Text, Action<YamlDocument> Op)> Edits()
    {
        yield return ("replace a scalar", Small, d => d.Set("/name", "another value that is longer"));
        yield return ("replace a scalar by one as long", Small, d => d.Set("/name", "ABCD"));
        yield return ("replace a scalar by a collection", Small, d => d.Set("/name", new Dictionary<string, object?> { ["a"] = 1, ["b"] = OneTwo }));
        yield return ("replace a collection by a scalar", Small, d => d.Set("/items", "none"));
        yield return ("replace a nested scalar", Small, d => d.Set("/items/0/b", 22));
        yield return ("replace a block scalar", Small, d => d.Set("/long", "one line"));
        yield return ("add a member", Small, d => d.Set("/extra", 1));
        yield return ("add a nested member", Small, d => d.Set("/items/1/c", true));
        yield return ("add a collection member", Small, d => d.Add("/extra", OneTwoThree));
        yield return ("add to a flow mapping", Small, d => d.Set("/map/r", 3));
        yield return ("append to a flow sequence", Small, d => d.Add("/seq/-", 4));
        yield return ("insert into a flow sequence", Small, d => d.Add("/seq/1", 9));
        yield return ("insert into a block sequence", Small, d => d.Add("/items/0", new Dictionary<string, object?> { ["a"] = 0 }));
        yield return ("remove a member", Small, d => d.Remove("/name"));
        yield return ("remove the last member", Small, d => d.Remove("/last"));
        yield return ("remove a block scalar member", Small, d => d.Remove("/long"));
        yield return ("remove from a flow mapping", Small, d => d.Remove("/map/p"));
        yield return ("remove a sequence item", Small, d => d.Remove("/items/0"));
        yield return ("indexer replace", Small, d => d.Root["name"] = "x");
        yield return ("indexer add", Small, d => d.Root["fresh"] = 1);
        yield return ("indexer in a sequence", Small, d => d.Root["items"][0]["a"] = 5);
        yield return ("indexer append to a sequence", Small, d => d.Root["items"][2] = "third");
        yield return ("view set", Small, d => d.Root["items"][1]["a"].Set(OneTwo));
        yield return ("view remove", Small, d => d.Root["items"][1].Remove());
        yield return ("view remove of a key", Small, d =>
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in d.Root["map"].Entries)
            {
                entry.Key.Remove();
                break;
            }
        }
        );
        yield return ("anchored replace", Anchored, d => d.Set("/other", 3));
        yield return ("anchored replace inside", Anchored, d => d.Set("/base/x", 2));
        yield return ("anchored add", Anchored, d => d.Set("/more", "m"));
        yield return ("anchored remove", Anchored, d => d.Remove("/other"));
        yield return ("anchored sequence", Anchored, d => d.Add("/list/-", 6));
        yield return ("the first member of an empty stream", string.Empty, d => d.Root["a"] = 1);
        yield return ("the first member by pointer", "# only a comment\n", d => d.Set("/a", 1));
        yield return ("a document marker alone", "---\n", d => d.Root["a"] = 1);
    }

    [Fact]
    public void An_edit_that_fails_at_any_step_leaves_the_document_as_it_was_and_usable()
    {
        var hits = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0 };
        foreach ((string name, string text, Action<YamlDocument> op) in Edits())
        {
            foreach (int stage in Stages)
            {
                using YamlDocument doc = YamlDocument.Parse(text);
                using YamlDocument reference = YamlDocument.Parse(text);
                YamlNode root = doc.Root;
                string rootText = root.ToString();
                string before = Reference(doc);
                int edits = doc.EditCount;

                bool injected = false;
                DocState.FaultStage = stage;
                try
                {
                    op(doc);
                }
                catch (InvalidOperationException e) when (e.Message.StartsWith("Injected failure", StringComparison.Ordinal))
                {
                    injected = true;
                }
                finally
                {
                    DocState.FaultStage = 0;
                }

                string what = $"{name}, step {stage}";
                if (injected)
                {
                    hits[stage]++;
                    Assert.True(before == Reference(doc), what + ": the bytes changed");
                    Assert.True(edits == doc.EditCount, what + ": the edit was counted");
                    Assert.True(StateEquality.Diff(doc.State, reference.State) is null, what + ": " + StateEquality.Diff(doc.State, reference.State));
                    Assert.False(doc.State.Exclusive, what);
                    Assert.Equal(rootText, root.ToString());
                }

                // Whether or not the failure was reached, the same edit on this document and on a fresh one agrees.
                if (injected)
                {
                    op(doc);
                }

                op(reference);
                Assert.True(Reference(reference) == Reference(doc), what + ": the edit made after the failure differs");
                Assert.True(StateEquality.Diff(doc.State, reference.State) is null, what + ": " + StateEquality.Diff(doc.State, reference.State));
            }
        }

        // Every step is reached by most kinds of edit; an edit that parses the whole text again does not reach them.
        Assert.True(hits[1] >= 25 && hits[2] >= 25 && hits[3] >= 25, $"the failure was injected {hits[1]}, {hits[2]} and {hits[3]} times");
    }

    [Fact]
    public void A_failed_edit_on_a_large_mapping_keeps_its_key_index_right()
    {
        const int Keys = 3_000;
        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        using YamlDocument reference = YamlDocument.Parse(Mapping(Keys));

        // Searching the mapping a few times builds its index.
        for (int round = 0; round < 6; round++)
        {
            for (int k = 0; k < Keys; k += 211)
            {
                Assert.Equal("value" + k, doc.Get<string>("/big/key" + k));
            }
        }

        Assert.True(doc.State.BuiltIndexes > 0);
        foreach (int stage in Stages)
        {
            DocState.FaultStage = stage;
            try
            {
                Assert.Throws<InvalidOperationException>(() => doc.Set("/big/key17", "a different and longer value"));
                Assert.Throws<InvalidOperationException>(() => doc.Set("/big/newkey" + stage, 1));
                Assert.Throws<InvalidOperationException>(() => doc.Remove("/big/key18"));
            }
            finally
            {
                DocState.FaultStage = 0;
            }

            Assert.Equal(Reference(reference), Reference(doc));
        }

        doc.Set("/big/key17", "a different and longer value");
        doc.Set("/big/newkey", 1);
        doc.Remove("/big/key18");
        reference.Set("/big/key17", "a different and longer value");
        reference.Set("/big/newkey", 1);
        reference.Remove("/big/key18");
        Assert.Equal(Reference(reference), Reference(doc));
        for (int k = 0; k < Keys; k += 53)
        {
            Assert.Equal(reference.TryGet("/big/key" + k, out string? want) ? want : null, doc.TryGet("/big/key" + k, out string? got) ? got : null);
        }
    }

    [Fact]
    public void An_edit_that_fails_before_any_change_leaves_the_state_shared_with_readers()
    {
        using YamlDocument doc = YamlDocument.Parse(Small);
        Assert.Throws<YamlException>(() => doc.Set("/name/deeper", 1));
        Assert.False(doc.State.Exclusive);
        Assert.Throws<KeyNotFoundException>(() => doc.Root["missing"]);
        Assert.Throws<YamlException>(() => doc.Remove("/nothing"));
        Assert.False(doc.State.Exclusive);
        Assert.Equal(0, doc.EditCount);
    }

    [Fact]
    public void Edits_made_in_turn_by_two_threads_write_the_line_breaks_one_thread_would()
    {
        // The thread that made an edit remembers where the line it edited ends. Another thread's memory of the same line
        // must not survive an edit it did not see: thread 0 edits, thread 1 edits, thread 0 edits the same line again.
        string src = "a: 1\nc:\n  k: " + new string('a', 44) + "\n  m: 2\n";
        Action<YamlDocument>[] sequence =
        [
            d => d.Set("/c/k", new string('b', 44)),
            d => d.Set("/a", "1234"),
            d => d.Set("/c/k", new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 }),
        ];
        using YamlDocument alone = YamlDocument.Parse(src);
        foreach (Action<YamlDocument> step in sequence)
        {
            step(alone);
        }

        using YamlDocument shared = YamlDocument.Parse(src);
        SemaphoreSlim[] turn = [new(1), new(0)];
        int next = 0;
        Exception? failure = null;
        Thread Worker(int me) => new(() =>
        {
            while (true)
            {
                turn[me].Wait();
                if (next >= sequence.Length)
                {
                    turn[1 - me].Release();
                    return;
                }

                try
                {
                    sequence[next++](shared);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    next = sequence.Length;
                }

                turn[1 - me].Release();
            }
        });
        // Steps run in order, and the turns alternate: the first and third on thread 0, the second on thread 1.
        Thread a = Worker(0), b = Worker(1);
        a.Start();
        b.Start();
        a.Join();
        b.Join();
        Assert.Null(failure);
        Assert.Equal(Reference(alone), Reference(shared));
        Assert.DoesNotContain('\r', Reference(shared));
    }

    [Fact]
    public void A_single_edit_after_a_patch_holds_no_move_back()
    {
        const int Keys = 3_000;
        using YamlDocument doc = YamlDocument.Parse(Mapping(Keys));
        doc.ApplyPatch("""[{"op":"replace","path":"/big/key1","value":"a"},{"op":"replace","path":"/big/key2","value":"bb"},{"op":"replace","path":"/big/key3","value":"ccc"}]""");
        EditWork.ChunksMoved = 0;
        doc.Set("/big/key10", "a longer value than before");
        doc.Set("/big/key11", "x");
        Assert.Equal(0, EditWork.ChunksMoved);
    }

    [Fact]
    public void A_document_whose_failed_edit_could_not_be_undone_for_lack_of_memory_refuses_further_use_and_the_caller_gets_the_edit_error()
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(50));
        YamlNode root = doc.Root;
        DocState.FaultStage = 2;
        DocState.FaultInRestore = new InsufficientMemoryException();
        try
        {
            InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => doc.Set("/big/key3", "different"));
            Assert.StartsWith("Injected failure at step 2", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            DocState.FaultStage = 0;
            DocState.FaultInRestore = null;
        }

        Assert.Contains("could not be restored", Assert.Throws<InvalidOperationException>(() => doc.ToUtf8Bytes()).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.Get<string>("/name"));
        Assert.Throws<InvalidOperationException>(() => root.ToString());
        Assert.Throws<InvalidOperationException>(() => doc.Set("/name", "x"));
    }

    [Fact]
    public void Any_other_failure_while_a_failed_edit_is_undone_is_not_hidden_and_the_document_refuses_further_use()
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(50));
        DocState.FaultStage = 2;
        DocState.FaultInRestore = new ArithmeticException("restore defect");
        try
        {
            ArithmeticException e = Assert.Throws<ArithmeticException>(() => doc.Set("/big/key3", "different"));
            Assert.Equal("restore defect", e.Message);
        }
        finally
        {
            DocState.FaultStage = 0;
            DocState.FaultInRestore = null;
        }

        Assert.Contains("could not be restored", Assert.Throws<InvalidOperationException>(() => doc.ToUtf8Bytes()).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.Set("/name", "x"));
    }
}
