using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Many operations on one large mapping: the work for each operation must not grow with the number of keys,
/// the index and the remembered layout must follow every kind of edit, and the results must be those of
/// operations that start from fresh text each time.
/// </summary>
public class LargeMappingTests
{
    private static string Mapping(int keys, string header = "# settings\nbig:\n")
    {
        var sb = new StringBuilder(header);
        for (int i = 0; i < keys; i++)
        {
            sb.Append("  key").Append(i).Append(": value").Append(i).Append('\n');
        }

        return sb.ToString();
    }

    private static string Replaces(int keys, int ops, string verb)
    {
        var sb = new StringBuilder("[");
        var rnd = new Random(7);
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

            sb.Append("{\"op\":\"").Append(verb).Append("\",\"path\":\"/big/key").Append(k).Append('"');
            if (verb == "replace")
            {
                sb.Append(",\"value\":\"patched-").Append(i).Append('"');
            }
            else if (verb == "test")
            {
                sb.Append(",\"value\":\"value").Append(k).Append('"');
            }

            sb.Append('}');
        }

        return sb.Append(']').ToString();
    }

    // The keys looked at and the layout steps taken while a patch of the given operations runs on a mapping of the given size.
    private static (long Keys, long Layout) Work(int keys, int ops, string verb)
    {
        string yaml = Mapping(keys);
        string patch = Replaces(keys, ops, verb);
        using YamlDocument doc = YamlDocument.Parse(yaml);
        EditWork.KeysExamined = 0;
        EditWork.LayoutSteps = 0;
        doc.ApplyPatch(patch);
        return (EditWork.KeysExamined, EditWork.LayoutSteps);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("remove")]
    [InlineData("test")]
    public void The_work_per_operation_does_not_grow_with_the_number_of_keys(string verb)
    {
        // Each extra operation costs the same on a mapping of 1,500 keys as on one of 24,000: it looks at a
        // few keys, not at half of them. The work of getting ready (the first operations, the index) is
        // what the two sizes differ in, and it is taken out by measuring two operation counts per size.
        const int Few = 100;
        const int Many = 400;
        foreach (int keys in new[] { 1_500, 24_000 })
        {
            (long keysFew, long layoutFew) = Work(keys, Few, verb);
            (long keysMany, long layoutMany) = Work(keys, Many, verb);
            double keysPerOp = (keysMany - keysFew) / (double)(Many - Few);
            double layoutPerOp = (layoutMany - layoutFew) / (double)(Many - Few);
            Assert.True(keysPerOp <= 20, $"{verb} on {keys} keys looked at {keysPerOp:F1} keys per operation");
            Assert.True(layoutPerOp <= 20, $"{verb} on {keys} keys took {layoutPerOp:F1} layout steps per operation");
        }
    }

    [Fact]
    public void A_document_that_is_only_parsed_or_read_once_holds_no_index()
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(5_000));
        Assert.Equal("value10", doc.Get<string>("/big/key10"));
        Assert.Equal(0, doc.State.BuiltIndexes);
        Assert.Equal("value20", doc.Get<string>("/big/key20"));
        Assert.Equal(0, doc.State.BuiltIndexes);

        // The index is built once the searches have cost a few times the size of the mapping, not before.
        for (int i = 0; i < 12 && doc.State.BuiltIndexes == 0; i++)
        {
            Assert.Equal("value" + (4_000 + i), doc.Get<string>("/big/key" + (4_000 + i)));
        }

        Assert.Equal(1, doc.State.BuiltIndexes);
    }

    [Fact]
    public void Several_threads_reading_one_large_document_get_the_right_values_and_leave_it_sound()
    {
        const int Maps = 8;
        const int Keys = 600;
        var sb = new StringBuilder();
        for (int m = 0; m < Maps; m++)
        {
            sb.Append('m').Append(m).Append(":\n");
            for (int i = 0; i < Keys; i++)
            {
                sb.Append("  key").Append(i).Append(": v").Append(m).Append('_').Append(i).Append('\n');
            }
        }

        using YamlDocument doc = YamlDocument.Parse(sb.ToString());
        using var start = new ManualResetEventSlim(false);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Thread[] threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            start.Wait();
            for (int r = 0; r < 400; r++)
            {
                int m = (r + t) % Maps;
                int k = ((r * 37) + (t * 101)) % Keys;
                string got = doc.Get<string>("/m" + m + "/key" + k);
                if (got != "v" + m + "_" + k)
                {
                    failures.Add($"thread {t}: /m{m}/key{k} gave {got}");
                }

                if (doc.TryGet<string>("/m" + m + "/nokey" + k, out _))
                {
                    failures.Add($"thread {t}: a missing key was found");
                }
            }
        })).ToArray();
        foreach (Thread th in threads)
        {
            th.Start();
        }

        start.Set();
        foreach (Thread th in threads)
        {
            th.Join();
        }

        Assert.Empty(failures);

        // The document is as sound afterwards as before: every key is found, and an edit still writes the right bytes.
        for (int m = 0; m < Maps; m++)
        {
            Assert.Equal("v" + m + "_599", doc.Get<string>("/m" + m + "/key599"));
        }

        doc.Set("/m3/key10", "changed");
        using YamlDocument again = YamlDocument.Parse(doc.ToUtf8Bytes());
        Assert.Equal("changed", again.Get<string>("/m3/key10"));
        Assert.Equal("v3_11", again.Get<string>("/m3/key11"));
        // An array returned to the shared pool twice would hand the same buffer to two renters; rent a few and look.
        var rented = new List<byte[]>();
        try
        {
            for (int i = 0; i < 64; i++)
            {
                rented.Add(System.Buffers.ArrayPool<byte>.Shared.Rent(4096));
            }

            Assert.Equal(rented.Count, rented.Distinct().Count());
        }
        finally
        {
            foreach (byte[] b in rented)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(b);
            }
        }
    }

    // ---- The results are those of one operation after another on fresh text ----

    // Applies each operation to a document that keeps its state, and to a document parsed again from the bytes
    // before every operation (so nothing is remembered); the bytes, or the error, must agree after each one.
    private static void AssertSameAsFresh(string yaml, IEnumerable<string> operations, YamlDocumentOptions? options = null)
    {
        using YamlDocument kept = YamlDocument.Parse(yaml, options);
        byte[] fresh = kept.ToUtf8Bytes();
        foreach (string op in operations)
        {
            string patch = "[" + op + "]";
            string? keptError = Try(() => kept.ApplyPatch(patch));
            using YamlDocument again = YamlDocument.Parse(fresh, options);
            string? freshError = Try(() => again.ApplyPatch(patch));
            Assert.Equal(freshError, keptError);
            Assert.Equal(again.ToUtf8Bytes(), kept.ToUtf8Bytes());
            fresh = again.ToUtf8Bytes();
        }
    }

    private static string? Try(Action act)
    {
        try
        {
            act();
            return null;
        }
        catch (YamlException ex)
        {
            return ex.Category + " " + ex.Line + ":" + ex.Column + " " + ex.Message;
        }
    }

    [Fact]
    public void Adds_removes_replaces_and_moves_in_a_row_find_each_key_where_it_is()
    {
        var ops = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            ops.Add($"{{\"op\":\"replace\",\"path\":\"/big/key{i * 7}\",\"value\":\"r{i}\"}}");
            ops.Add($"{{\"op\":\"add\",\"path\":\"/big/added{i}\",\"value\":{{\"n\":{i}}}}}");
            ops.Add($"{{\"op\":\"remove\",\"path\":\"/big/key{(i * 11) + 3}\"}}");
            ops.Add($"{{\"op\":\"replace\",\"path\":\"/big/added{i}/n\",\"value\":[{i}, \"x\"]}}");
            ops.Add($"{{\"op\":\"move\",\"from\":\"/big/key{(i * 13) + 1}\",\"path\":\"/big/moved{i}\"}}");
            ops.Add($"{{\"op\":\"add\",\"path\":\"/big/key{(i * 11) + 3}\",\"value\":\"back\"}}");
            ops.Add($"{{\"op\":\"test\",\"path\":\"/big/moved{i}\",\"value\":\"value{(i * 13) + 1}\"}}");
            ops.Add($"{{\"op\":\"remove\",\"path\":\"/big/added{i}\"}}");
            ops.Add($"{{\"op\":\"copy\",\"from\":\"/big/key{i}\",\"path\":\"/big/copied{i}\"}}");
            ops.Add($"{{\"op\":\"remove\",\"path\":\"/big/key{i * 7}\"}}");
            ops.Add($"{{\"op\":\"remove\",\"path\":\"/big/key{i * 7}\"}}");
        }

        AssertSameAsFresh(Mapping(400) + "after: 1\n", ops);
    }

    [Fact]
    public void One_patch_of_many_operations_gives_the_bytes_of_the_operations_one_by_one()
    {
        var ops = new List<string>();
        for (int i = 0; i < 300; i++)
        {
            ops.Add(i % 3 == 0
                ? $"{{\"op\":\"remove\",\"path\":\"/big/key{i}\"}}"
                : i % 3 == 1
                    ? $"{{\"op\":\"replace\",\"path\":\"/big/key{i}\",\"value\":{{\"deep\":[{i}]}}}}"
                    : $"{{\"op\":\"add\",\"path\":\"/big/new{i}\",\"value\":\"v\"}}");
        }

        string yaml = Mapping(400);
        using YamlDocument whole = YamlDocument.Parse(yaml);
        whole.ApplyPatch("[" + string.Join(",", ops) + "]");

        using YamlDocument oneByOne = YamlDocument.Parse(yaml);
        foreach (string op in ops)
        {
            oneByOne.ApplyPatch("[" + op + "]");
        }

        Assert.Equal(oneByOne.ToUtf8Bytes(), whole.ToUtf8Bytes());
    }

    [Fact]
    public void A_failed_patch_leaves_the_bytes_and_the_next_patch_finds_every_key()
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(300));
        byte[] before = doc.ToUtf8Bytes();
        var bad = new StringBuilder("[");
        for (int i = 0; i < 100; i++)
        {
            bad.Append("{\"op\":\"remove\",\"path\":\"/big/key").Append(i).Append("\"},");
        }

        bad.Append("{\"op\":\"test\",\"path\":\"/big/key150\",\"value\":\"other\"}]");
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch(bad.ToString()));
        Assert.Equal(YamlErrorCategory.PatchTest, ex.Category);
        Assert.Equal(before, doc.ToUtf8Bytes());
        Assert.Equal("value99", doc.Get<string>("/big/key99"));
        doc.ApplyPatch("[{\"op\":\"remove\",\"path\":\"/big/key99\"},{\"op\":\"remove\",\"path\":\"/big/key98\"},{\"op\":\"remove\",\"path\":\"/big/key97\"}]");
        Assert.False(doc.Contains("/big/key99"));
        Assert.Equal("value96", doc.Get<string>("/big/key96"));
    }

    [Fact]
    public void Keys_with_the_same_text_stay_ambiguous_however_many_keys_there_are()
    {
        // 80 and "80" are different keys of the document and the same text for a pointer.
        string yaml = Mapping(300, "big:\n  80: a\n  \"80\": b\n") + "  other: 1\n";
        using YamlDocument doc = YamlDocument.Parse(yaml);
        for (int round = 0; round < 4; round++)
        {
            YamlException ex = Assert.Throws<YamlException>(() => doc.Get<string>("/big/80"));
            Assert.Equal(YamlErrorCategory.RefusedAmbiguous, ex.Category);
            Assert.Equal("value5", doc.Get<string>("/big/key5"));
            doc.Set("/big/key" + round, "edited" + round);
        }

        Assert.Equal("edited3", doc.Get<string>("/big/key3"));
        AssertSameAsFresh(yaml, ["{\"op\":\"replace\",\"path\":\"/big/80\",\"value\":1}", "{\"op\":\"remove\",\"path\":\"/big/key9\"}", "{\"op\":\"add\",\"path\":\"/big/80\",\"value\":2}", "{\"op\":\"test\",\"path\":\"/big/other\",\"value\":1}"]);
    }

    [Fact]
    public void An_added_key_with_the_text_of_another_key_is_refused_or_ambiguous_as_before()
    {
        string yaml = Mapping(300);
        using YamlDocument doc = YamlDocument.Parse(yaml);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("value" + i, doc.Get<string>("/big/key" + i));
        }

        // A second 80 in another spelling is a different key; a plain repeat of key7 is a duplicate.
        doc.Set("/big/80", 1);
        doc.Root["big"]["extra"] = "e";
        Assert.Equal("e", doc.Get<string>("/big/extra"));
        AssertSameAsFresh(yaml, ["{\"op\":\"add\",\"path\":\"/big/80\",\"value\":1}", "{\"op\":\"add\",\"path\":\"/big/key7\",\"value\":\"again\"}", "{\"op\":\"replace\",\"path\":\"/big/80\",\"value\":3}"]);
    }

    [Fact]
    public void Duplicate_keys_allowed_by_an_option_are_ambiguous_for_a_pointer()
    {
        string yaml = Mapping(300) + "  key17: twice\n";
        var options = new YamlDocumentOptions { AllowDuplicateKeys = true };
        using YamlDocument doc = YamlDocument.Parse(yaml, options);
        for (int i = 0; i < 4; i++)
        {
            YamlException ex = Assert.Throws<YamlException>(() => doc.Get<string>("/big/key17"));
            Assert.Equal(YamlErrorCategory.RefusedAmbiguous, ex.Category);
            Assert.Equal("value" + (i + 40), doc.Get<string>("/big/key" + (i + 40)));
        }

        AssertSameAsFresh(yaml, ["{\"op\":\"replace\",\"path\":\"/big/key17\",\"value\":1}", "{\"op\":\"remove\",\"path\":\"/big/key3\"}", "{\"op\":\"remove\",\"path\":\"/big/key17\"}"], options);
    }

    [Fact]
    public void The_layout_of_new_text_follows_the_document_through_edits_that_add_collections()
    {
        // The nested mapping is indented by four; once the first nested collection is removed, the step is read
        // again from what is left.
        var sb = new StringBuilder("first:\n    nested:\n        deep: 1\n    list:\n      - a\n");
        for (int i = 0; i < 200; i++)
        {
            sb.Append('k').Append(i).Append(": v").Append(i).Append('\n');
        }

        sb.Append("last:\n  inner:\n    x: 1\n");
        var ops = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            ops.Add($"{{\"op\":\"replace\",\"path\":\"/k{i}\",\"value\":\"r{i}\"}}");
            ops.Add($"{{\"op\":\"add\",\"path\":\"/m{i}\",\"value\":{{\"a\":{{\"b\":[{i}]}}}}}}");
            if (i == 10)
            {
                ops.Add("{\"op\":\"remove\",\"path\":\"/first\"}");
            }

            if (i == 20)
            {
                ops.Add("{\"op\":\"remove\",\"path\":\"/last\"}");
            }

            ops.Add($"{{\"op\":\"replace\",\"path\":\"/k{i + 50}\",\"value\":\"multi\\nline\\n\"}}");
        }

        AssertSameAsFresh(sb.ToString(), ops);
    }

    [Fact]
    public void Edits_through_views_and_indexers_keep_working_on_a_large_mapping()
    {
        using YamlDocument doc = YamlDocument.Parse(Mapping(2_000));
        var expected = new Dictionary<string, string>();
        for (int i = 0; i < 2_000; i++)
        {
            expected["key" + i] = "value" + i;
        }

        YamlNode big = doc.Root["big"];
        YamlNode watched = big["key1500"];
        for (int i = 0; i < 60; i++)
        {
            big["key" + (i * 3)] = "set" + i;
            expected["key" + (i * 3)] = "set" + i;
            big["added" + i] = "new" + i;
            expected["added" + i] = "new" + i;
            doc.Remove("/big/key" + ((i * 3) + 1));
            expected.Remove("key" + ((i * 3) + 1));
        }

        Assert.Equal("value1500", watched.GetValue<string>());
        Assert.Equal(expected.Count, big.Count);
        foreach (KeyValuePair<string, string> pair in expected)
        {
            Assert.Equal(pair.Value, big[pair.Key].GetValue<string>());
        }

        using YamlDocument again = YamlDocument.Parse(doc.ToUtf8Bytes());
        foreach (KeyValuePair<string, string> pair in expected)
        {
            Assert.Equal(pair.Value, again.Get<string>("/big/" + pair.Key));
        }
    }
}

/// <summary>
/// The tests of the rest of the assembly lower the thresholds so that small documents use the same code as large
/// ones. These run alone, at the numbers a user gets, with every check on.
/// </summary>
[CollectionDefinition(nameof(ProductionThresholds), DisableParallelization = true)]
public sealed class ProductionThresholdsGroup;

[Collection(nameof(ProductionThresholds))]
public class ProductionThresholds
{
    [Fact]
    public void A_large_mapping_edited_at_the_default_thresholds_keeps_its_index_and_layout_right()
    {
        int indexMin = DocState.IndexMinNodes;
        int layoutMin = DocState.LayoutMinSteps;
        bool verify = DocState.VerifyCaches;
        try
        {
            DocState.IndexMinNodes = 512;
            DocState.LayoutMinSteps = 2048;
            DocState.VerifyCaches = true;

            var sb = new StringBuilder("# settings\nbig:\n");
            for (int i = 0; i < 3_000; i++)
            {
                sb.Append("  key").Append(i).Append(": value").Append(i).Append('\n');
            }

            using YamlDocument doc = YamlDocument.Parse(sb.ToString());
            var ops = new StringBuilder("[");
            for (int i = 0; i < 60; i++)
            {
                if (i > 0)
                {
                    ops.Append(',');
                }

                ops.Append((i % 3) switch
                {
                    0 => $"{{\"op\":\"replace\",\"path\":\"/big/key{i * 17}\",\"value\":\"r{i}\"}}",
                    1 => $"{{\"op\":\"add\",\"path\":\"/big/new{i}\",\"value\":{{\"a\":[1,2]}}}}",
                    _ => $"{{\"op\":\"remove\",\"path\":\"/big/key{(i * 13) + 1}\"}}",
                });
            }

            doc.ApplyPatch(ops.Append(']').ToString());
            Assert.True(doc.State.BuiltIndexes >= 1);

            using YamlDocument fresh = YamlDocument.Parse(doc.ToUtf8Bytes());
            Assert.Equal(fresh.Get<string>("/big/key17"), doc.Get<string>("/big/key17"));
            Assert.Equal("r3", doc.Get<string>("/big/key51"));
            Assert.False(doc.TryGet<string>("/big/key27", out _));
            doc.Set("/big/key2000", "set");
            doc.Remove("/big/key2001");
            Assert.Equal("set", doc.Get<string>("/big/key2000"));
            Assert.False(doc.TryGet<string>("/big/key2001", out _));
            Assert.Equal("value2002", doc.Get<string>("/big/key2002"));
        }
        finally
        {
            DocState.IndexMinNodes = indexMin;
            DocState.LayoutMinSteps = layoutMin;
            DocState.VerifyCaches = verify;
        }
    }

    [Fact]
    public void Reading_more_large_mappings_than_the_cache_follows_allocates_no_more_than_without_a_cache()
    {
        const int Maps = 20;
        var sb = new StringBuilder();
        for (int m = 0; m < Maps; m++)
        {
            sb.Append('m').Append(m).Append(":\n");
            for (int i = 0; i < 400; i++)
            {
                sb.Append("  key").Append(i).Append(": v").Append(i).Append('\n');
            }
        }

        string[] paths = Enumerable.Range(0, Maps).Select(m => "/m" + m + "/key217").ToArray();
        int indexMin = DocState.IndexMinNodes;
        bool verify = DocState.VerifyCaches;
        try
        {
            // The checks of the other tests would be counted as allocations.
            DocState.VerifyCaches = false;
            DocState.IndexMinNodes = int.MaxValue;
            using YamlDocument plain = YamlDocument.Parse(sb.ToString());
            long without = Allocated(plain, paths);

            DocState.IndexMinNodes = 512;
            using YamlDocument cached = YamlDocument.Parse(sb.ToString());
            for (int r = 0; r < 60; r++)
            {
                foreach (string path in paths)
                {
                    Assert.True(cached.TryGet<string>(path.Replace("217", ((r * 31) % 400).ToString(System.Globalization.CultureInfo.InvariantCulture)), out _));
                }
            }

            Assert.True(cached.State.BuiltIndexes > 0);
            long with = Allocated(cached, paths);
            Assert.True(with <= without, $"reads with a cache allocated {with} bytes, without {without}");
        }
        finally
        {
            DocState.IndexMinNodes = indexMin;
            DocState.VerifyCaches = verify;
        }
    }

    private static long Allocated(YamlDocument doc, string[] paths)
    {
        foreach (string path in paths)
        {
            doc.TryGet<string>(path, out _);
        }

        long bytes = 0;
        for (int round = 0; round < 5; round++)
        {
            foreach (string path in paths)
            {
                long one = GC.GetAllocatedBytesForCurrentThread();
                doc.TryGet<string>(path, out _);
                bytes += GC.GetAllocatedBytesForCurrentThread() - one;
            }
        }

        return bytes;
    }
}
