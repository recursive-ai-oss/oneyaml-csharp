using System.Text;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Views follow their nodes across edits: the way a view is used for several edits in a row, loops over items
/// that edit each item, and edits made elsewhere, by index, pointer, patch or merge patch.
/// </summary>
public class DocumentViewTests
{
    [Fact]
    public void One_view_takes_several_edits_in_a_row()
    {
        using YamlDocument doc = YamlDocument.Parse("spec:\n  replicas: 3   # keep\n  other: x\n");
        YamlNode spec = doc.Root["spec"];
        spec["replicas"] = 5;
        spec["paused"] = true;
        spec["other"] = "y";
        Assert.Equal("spec:\n  replicas: 5   # keep\n  other: y\n  paused: true\n", doc.ToString());
        Assert.Equal(3, spec.Count);
    }

    [Fact]
    public void A_loop_over_items_can_edit_every_item_through_the_item()
    {
        const string text = "containers:\n  - name: a   # first\n    image: a:1\n  - name: b   # second\n    image: b:1\n  - name: c\n    image: c:1\n";
        using YamlDocument doc = YamlDocument.Parse(text);
        foreach (YamlNode c in doc.Root["containers"].Items)
        {
            c["image"] = "new:2";
            c["pull"] = "always";
        }

        Assert.Equal(
            "containers:\n  - name: a   # first\n    image: new:2\n    pull: always\n  - name: b   # second\n    image: new:2\n    pull: always\n  - name: c\n    image: new:2\n    pull: always\n",
            doc.ToString());
    }

    [Fact]
    public void A_loop_over_entries_can_replace_values_with_collections()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\nc: 3\n");
        foreach (KeyValuePair<YamlNode, YamlNode> e in doc.Root.Entries)
        {
            e.Value.Set(new[] { e.Key.GetValue<string>(), "x" });
        }

        Assert.Equal("a:\n  - a\n  - x\nb:\n  - b\n  - x\nc:\n  - c\n  - x\n", doc.ToString());
    }

    [Fact]
    public void A_loop_that_removes_items_carries_on_by_position()
    {
        using YamlDocument doc = YamlDocument.Parse("- a\n- b\n- c\n- d\n");
        var seen = new List<string>();
        foreach (YamlNode item in doc.Root.Items)
        {
            seen.Add(item.GetValue<string>());
            if (seen.Count % 2 == 1)
            {
                item.Remove();
            }
        }

        // Removing the item just returned moves the next one into its place, so the enumerator, which goes by
        // position, skips it: a is removed and b is skipped, c is kept, d is removed.
        Assert.Equal(["a", "c", "d"], seen);
        Assert.Equal("- b\n- c\n", doc.ToString());
    }

    [Fact]
    public void Views_follow_insertions_and_removals_elsewhere()
    {
        using YamlDocument doc = YamlDocument.Parse("list:\n  - one\n  - two\n  - three\nmap:\n  a: 1\n  b: 2\n");
        YamlNode two = doc.Root["list"][1];
        YamlNode b = doc.Root["map"]["b"];
        YamlNode list = doc.Root["list"];
        doc.Add("/list/0", "zero");
        doc.Add("/list/-", "four");
        doc.Set("/map/c", 3);
        doc.Remove("/list/1");
        doc.Add("/new", 1);
        Assert.Equal("two", two.GetValue<string>());
        Assert.Equal(2, b.GetValue<int>());
        Assert.Equal(["zero", "two", "three", "four"], list.Items.Select(i => i.GetValue<string>()).ToArray());
        two.Set("TWO");
        b.Set(20);
        Assert.Equal("list:\n  - zero\n  - TWO\n  - three\n  - four\nmap:\n  a: 1\n  b: 20\n  c: 3\nnew: 1\n", doc.ToString());
    }

    [Fact]
    public void A_removed_node_and_what_was_inside_a_replaced_value_throw_before_any_write()
    {
        const string text = "a:\n  inner: 1\n  more: [1, 2]\nb: 2\nc: 3\n";
        using YamlDocument doc = YamlDocument.Parse(text);
        YamlNode a = doc.Root["a"];
        YamlNode inner = a["inner"];
        YamlNode more = a["more"];
        YamlNode b = doc.Root["b"];
        YamlNode c = doc.Root["c"];

        b.Remove();
        Assert.Throws<InvalidOperationException>(() => b.GetValue<int>());
        string afterRemove = doc.ToString();
        Assert.Throws<InvalidOperationException>(() => b.Set(9));
        Assert.Throws<InvalidOperationException>(() => b.Remove());
        Assert.Equal(afterRemove, doc.ToString());

        // The replaced node stays; what was inside it does not.
        a.Set("plain");
        Assert.Equal("plain", a.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => inner.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => more.Count);
        Assert.Equal(3, c.GetValue<int>());
        Assert.Equal("a: plain\nc: 3\n", doc.ToString());
    }

    [Fact]
    public void A_loop_with_a_removed_view_in_it_stops_before_a_partial_edit()
    {
        using YamlDocument doc = YamlDocument.Parse("items:\n  - name: a\n  - name: b\n  - name: c\n");
        YamlNode[] items = doc.Root["items"].Items.ToArray();
        doc.Remove("/items/1");
        string before = doc.ToString();
        int done = 0;
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (YamlNode item in items)
            {
                item["done"] = true;
                done++;
            }
        });

        Assert.Equal(1, done);
        Assert.Equal("items:\n  - name: a\n    done: true\n  - name: c\n", doc.ToString());
        Assert.NotEqual(before, doc.ToString());
    }

    [Fact]
    public void Collections_between_edits_do_not_disturb_views_old_or_new()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\n");
        YamlNode held = doc.Root["b"];
        for (int i = 0; i < 4; i++)
        {
            doc.Add("/n" + i, i);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.Equal(2, held.GetValue<int>());

        // With every view gone, edits note nothing; a view taken afterwards is followed from there on.
        YamlNode? gone = doc.Root["a"];
        gone = null;
        GC.KeepAlive(gone);
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            doc.Set("/a", i + 10);
        }

        YamlNode fresh = doc.Root["n1"];
        doc.Add("/z", 0);
        doc.Remove("/n0");
        GC.Collect();
        Assert.Equal(1, fresh.GetValue<int>());
        Assert.Equal(2, held.GetValue<int>());
    }

    [Fact]
    public void Removing_the_view_of_a_key_removes_its_entry()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\nc: 3\n");
        YamlNode[] keys = doc.Root.Entries.Select(e => e.Key).ToArray();
        YamlNode second = doc.Root["b"];
        keys[0].Remove();
        Assert.Equal("b: 2\nc: 3\n", doc.ToString());
        keys[2].Remove();
        Assert.Equal("b: 2\n", doc.ToString());
        Assert.Equal(2, second.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => keys[0].Kind);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => keys[1].Set("z")).Category);
        Assert.Equal("b: 2\n", doc.ToString());
    }

    [Fact]
    public void The_root_of_a_stream_with_no_content_takes_several_writes()
    {
        using YamlDocument doc = YamlDocument.Parse("# nothing yet\n");
        YamlNode root = doc.Root;
        root["a"] = 1;
        root["b"] = 2;
        Assert.Equal("# nothing yet\na: 1\nb: 2\n", doc.ToString());
        Assert.Equal(2, root.Count);
    }

    [Fact]
    public void Views_in_other_documents_of_the_stream_follow_too()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n---\nb: 2\n---\nc: 3\n");
        YamlNode second = doc.Documents[1];
        YamlNode third = doc.Documents[2];
        doc.Set("/a2", 5, document: 0);
        doc.Set("/b2", 6, document: 1);
        second["b3"] = 7;
        third["c2"] = 8;
        Assert.Equal("a: 1\na2: 5\n---\nb: 2\nb2: 6\nb3: 7\n---\nc: 3\nc2: 8\n", doc.ToString());
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public void A_view_reached_through_an_alias_is_valid_only_until_the_next_edit()
    {
        using YamlDocument doc = YamlDocument.Parse("m: &m {a: 1}\nalias: *m\nother: 1\n");
        YamlNode viaAlias = doc.Root["alias"]["a"];
        Assert.Equal(1, viaAlias.GetValue<int>());
        doc.Set("/other", 2);
        Assert.Throws<InvalidOperationException>(() => viaAlias.GetValue<int>());
        Assert.Equal(1, doc.Root["alias"]["a"].GetValue<int>());
    }

    [Fact]
    public void A_pointer_patch_edits_and_views_follow_it()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: [x, y]\nc: 3\nd: 4\n");
        YamlNode c = doc.Root["c"];
        YamlNode y = doc.Root["b"][1];
        YamlNode d = doc.Root["d"];
        doc.ApplyPatch("""[{"op":"add","path":"/b/0","value":"w"},{"op":"remove","path":"/a"},{"op":"add","path":"/e","value":5}]""");
        Assert.Equal(3, c.GetValue<int>());
        Assert.Equal("y", y.GetValue<string>());
        Assert.Equal(4, d.GetValue<int>());
        doc.ApplyMergePatch("""{"c":null,"d":40}""");
        Assert.Throws<InvalidOperationException>(() => c.GetValue<int>());
        Assert.Equal(40, d.GetValue<int>());
        Assert.Equal("y", y.GetValue<string>());
    }

    [Fact]
    public void Many_keys_removed_in_one_merge_patch_take_the_views_of_the_others_along()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            text.Append("key").Append(i).Append(": ").Append(i).Append('\n');
        }

        using YamlDocument doc = YamlDocument.Parse(text.ToString());
        YamlNode[] views = Enumerable.Range(0, 40).Select(i => doc.Root["key" + i]).ToArray();
        var patch = new StringBuilder("{");
        for (int i = 0; i < 40; i += 3)
        {
            patch.Append(i == 0 ? "" : ",").Append("\"key").Append(i).Append("\":null");
        }

        patch.Append('}');
        doc.ApplyMergePatch(patch.ToString());
        for (int i = 0; i < 40; i++)
        {
            if (i % 3 == 0)
            {
                Assert.Throws<InvalidOperationException>(() => views[i].GetValue<int>());
            }
            else
            {
                Assert.Equal(i, views[i].GetValue<int>());
            }
        }
    }

    [Fact]
    public void Equal_views_name_the_same_node_whatever_edits_they_have_seen()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\nb: 2\n");
        YamlNode first = doc.Root["b"];
        doc.Add("/z", 0);
        YamlNode second = doc.Root["b"];
        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.NotEqual(first, doc.Root["a"]);
    }

    [Fact]
    public void Using_a_view_after_the_document_is_disposed_says_so()
    {
        YamlNode view;
        using (YamlDocument doc = YamlDocument.Parse("a: [1, 2]\n"))
        {
            view = doc.Root["a"];
        }

        Assert.Throws<ObjectDisposedException>(() => view.Count);
        Assert.Throws<ObjectDisposedException>(() => view[0]);
        Assert.Throws<ObjectDisposedException>(() => view.Items.GetEnumerator());
        Assert.Throws<ObjectDisposedException>(() => view.ToString());
    }

    // ---- Views against a model, with unique values as the identity of every node ----

    private sealed class MNode
    {
        public char Kind; // s, m, q
        public string Value = string.Empty;
        public List<string> Keys = [];
        public List<MNode> Kids = [];
        public bool Alive = true;
        public YamlNode? View;
    }

    private static int s_counter;

    private static string Fresh(string prefix) => prefix + Interlocked.Increment(ref s_counter);

    private static MNode Scalar() => new() { Kind = 's', Value = Fresh("v") };

    private static MNode Make(Random r, int depth)
    {
        if (depth == 0 || r.Next(4) == 0)
        {
            return Scalar();
        }

        bool map = r.Next(2) == 0;
        var n = new MNode { Kind = map ? 'm' : 'q' };
        int count = 1 + r.Next(4);
        for (int i = 0; i < count; i++)
        {
            if (map)
            {
                n.Keys.Add(Fresh("k"));
            }

            n.Kids.Add(Make(r, depth - 1));
        }

        return n;
    }

    private static bool AllScalars(MNode n) => n.Kids.All(k => k.Kind == 's');

    private static void Render(StringBuilder sb, MNode n, int indent, Random r)
    {
        string pad = new(' ', indent);
        if (n.Kind == 'm')
        {
            for (int i = 0; i < n.Kids.Count; i++)
            {
                MNode v = n.Kids[i];
                if (v.Kind == 's')
                {
                    sb.Append(pad).Append(n.Keys[i]).Append(": ").Append(v.Value).Append(r.Next(5) == 0 ? "   # note" : string.Empty).Append('\n');
                }
                else if (AllScalars(v) && r.Next(3) == 0)
                {
                    sb.Append(pad).Append(n.Keys[i]).Append(": ");
                    Flow(sb, v);
                    sb.Append('\n');
                }
                else
                {
                    sb.Append(pad).Append(n.Keys[i]).Append(":\n");
                    Render(sb, v, indent + 2, r);
                }

                if (r.Next(8) == 0)
                {
                    sb.Append(pad).Append("# between\n");
                }
            }

            return;
        }

        foreach (MNode item in n.Kids)
        {
            if (item.Kind == 's')
            {
                sb.Append(pad).Append("- ").Append(item.Value).Append('\n');
            }
            else if (AllScalars(item) && r.Next(3) == 0)
            {
                sb.Append(pad).Append("- ");
                Flow(sb, item);
                sb.Append('\n');
            }
            else
            {
                var inner = new StringBuilder();
                Render(inner, item, indent + 2, r);
                sb.Append(pad).Append("- ").Append(inner.ToString(indent + 2, inner.Length - indent - 2));
            }
        }
    }

    private static void Flow(StringBuilder sb, MNode n)
    {
        sb.Append(n.Kind == 'm' ? '{' : '[');
        for (int i = 0; i < n.Kids.Count; i++)
        {
            sb.Append(i > 0 ? ", " : string.Empty);
            if (n.Kind == 'm')
            {
                sb.Append(n.Keys[i]).Append(": ");
            }

            sb.Append(n.Kids[i].Value);
        }

        sb.Append(n.Kind == 'm' ? '}' : ']');
    }

    private static string Sig(MNode n)
    {
        if (n.Kind == 's')
        {
            return n.Value;
        }

        var sb = new StringBuilder(n.Kind == 'm' ? "{" : "[");
        for (int i = 0; i < n.Kids.Count; i++)
        {
            sb.Append(n.Kind == 'm' ? n.Keys[i] + ":" : string.Empty).Append(Sig(n.Kids[i])).Append(',');
        }

        return sb.Append(n.Kind == 'm' ? '}' : ']').ToString();
    }

    private static string Sig(YamlNode v)
    {
        if (v.Kind == YamlNodeKind.Scalar)
        {
            return v.GetValue<string>();
        }

        var sb = new StringBuilder(v.Kind == YamlNodeKind.Mapping ? "{" : "[");
        if (v.Kind == YamlNodeKind.Mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> e in v.Entries)
            {
                sb.Append(e.Key.GetValue<string>()).Append(':').Append(Sig(e.Value)).Append(',');
            }
        }
        else
        {
            foreach (YamlNode i in v.Items)
            {
                sb.Append(Sig(i)).Append(',');
            }
        }

        return sb.Append(v.Kind == YamlNodeKind.Mapping ? '}' : ']').ToString();
    }

    private static object ToObject(MNode n)
    {
        if (n.Kind == 's')
        {
            return n.Value;
        }

        if (n.Kind == 'q')
        {
            return n.Kids.Select(ToObject).ToList();
        }

        var d = new Dictionary<string, object>();
        for (int i = 0; i < n.Kids.Count; i++)
        {
            d[n.Keys[i]] = ToObject(n.Kids[i]);
        }

        return d;
    }

    private static void Kill(MNode n)
    {
        n.Alive = false;
        foreach (MNode k in n.Kids)
        {
            Kill(k);
        }
    }

    private static void Capture(YamlNode view, MNode m, List<MNode> tracked)
    {
        if (m.View is null)
        {
            m.View = view;
            tracked.Add(m);
        }

        if (m.Kind == 'q')
        {
            int i = 0;
            foreach (YamlNode item in view.Items)
            {
                Capture(item, m.Kids[i++], tracked);
            }
        }
        else if (m.Kind == 'm')
        {
            int i = 0;
            foreach (KeyValuePair<YamlNode, YamlNode> e in view.Entries)
            {
                Capture(e.Value, m.Kids[i++], tracked);
            }
        }
    }

    private static string Nest(string path, string json)
    {
        string[] tokens = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            json = "{\"" + tokens[i] + "\":" + json + "}";
        }

        return json;
    }

    private static void All(MNode n, string path, MNode? parent, int index, List<(MNode Node, string Path, MNode? Parent, int Index)> into)
    {
        into.Add((n, path, parent, index));
        for (int i = 0; i < n.Kids.Count; i++)
        {
            All(n.Kids[i], path + "/" + (n.Kind == 'm' ? n.Keys[i] : i.ToString(System.Globalization.CultureInfo.InvariantCulture)), n, i, into);
        }
    }

    private static void CheckViews(List<MNode> tracked, string text)
    {
        foreach (MNode m in tracked)
        {
            if (m.Alive)
            {
                string expected = Sig(m);
                string actual;
                try
                {
                    actual = Sig(m.View!);
                }
                catch (InvalidOperationException ex)
                {
                    throw new Xunit.Sdk.XunitException($"A live view threw: {ex.Message}\nexpected {expected}\n{text}");
                }

                Assert.True(expected == actual, $"a view reads {actual}, expected {expected}\n{text}");
            }
            else
            {
                YamlNode v = m.View!;
                Assert.Throws<InvalidOperationException>(() => v.Kind);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Random_edits_keep_every_live_view_on_its_node_and_end_the_others(int seed)
    {
        var r = new Random(seed);
        for (int round = 0; round < 12; round++)
        {
            MNode root;
            do
            {
                root = Make(r, 3);
            }
            while (root.Kind == 's');
            var sb = new StringBuilder();
            Render(sb, root, 0, r);
            string initial = sb.ToString();
            using YamlDocument doc = YamlDocument.Parse(initial);
            var tracked = new List<MNode>();
            Capture(doc.Root, root, tracked);
            var log = new StringBuilder("initial:\n" + initial);
            for (int step = 0; step < 14; step++)
            {
                var nodes = new List<(MNode Node, string Path, MNode? Parent, int Index)>();
                All(root, string.Empty, null, 0, nodes);
                var pick = nodes[r.Next(nodes.Count)];
                MNode n = pick.Node;
                string op;
                switch (r.Next(8))
                {
                    case 0 when n.Kind == 's':
                        {
                            string v = Fresh("w");
                            if (r.Next(2) == 0)
                            {
                                op = $"doc.Set {pick.Path} = {v}";
                                doc.Set(pick.Path, v);
                            }
                            else
                            {
                                op = $"view.Set {pick.Path} = {v}";
                                n.View!.Set(v);
                            }

                            n.Value = v;
                            break;
                        }

                    case 1 when pick.Parent is not null:
                        {
                            MNode fresh = Make(r, 2);
                            op = $"doc.Set {pick.Path} = {Sig(fresh)}";
                            doc.Set(pick.Path, ToObject(fresh));
                            foreach (MNode k in n.Kids)
                            {
                                Kill(k);
                            }

                            n.Kind = fresh.Kind;
                            n.Value = fresh.Value;
                            n.Keys = fresh.Keys;
                            n.Kids = fresh.Kids;
                            break;
                        }

                    case 2 when n.Kind == 'm':
                        {
                            string key = Fresh("k");
                            MNode leaf = Scalar();
                            if (r.Next(2) == 0)
                            {
                                op = $"doc.Set {pick.Path}/{key} = {leaf.Value}";
                                doc.Set(pick.Path + "/" + key, leaf.Value);
                            }
                            else
                            {
                                op = $"view[{key}] = {leaf.Value} on {pick.Path}";
                                n.View![key] = leaf.Value;
                            }

                            n.Keys.Add(key);
                            n.Kids.Add(leaf);
                            break;
                        }

                    case 3 when n.Kind == 'q':
                        {
                            int at = r.Next(n.Kids.Count + 1);
                            MNode leaf = Scalar();
                            op = $"doc.Add {pick.Path}/{at} = {leaf.Value}";
                            doc.Add(pick.Path + "/" + at, leaf.Value);
                            n.Kids.Insert(at, leaf);
                            break;
                        }

                    case 4 when pick.Parent is not null:
                        {
                            if (r.Next(2) == 0)
                            {
                                op = $"doc.Remove {pick.Path}";
                                doc.Remove(pick.Path);
                            }
                            else
                            {
                                op = $"view.Remove {pick.Path}";
                                n.View!.Remove();
                            }

                            Kill(n);
                            if (pick.Parent.Kind == 'm')
                            {
                                pick.Parent.Keys.RemoveAt(pick.Index);
                            }

                            pick.Parent.Kids.RemoveAt(pick.Index);
                            break;
                        }

                    case 5 when n.Kind == 'm' && n.Kids.Count >= 3 && !pick.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(t => char.IsDigit(t[0])):
                        {
                            // A merge patch that deletes two keys and sets a third: the single-pass removal.
                            int a = r.Next(n.Kids.Count);
                            int b = (a + 1 + r.Next(n.Kids.Count - 1)) % n.Kids.Count;
                            int c = Enumerable.Range(0, n.Kids.Count).First(x => x != a && x != b);
                            string newValue = Fresh("m");
                            string json = $"{{\"{n.Keys[a]}\":null,\"{n.Keys[b]}\":null,\"{n.Keys[c]}\":\"{newValue}\"}}";
                            string patch = pick.Path.Length == 0 ? json : Nest(pick.Path, json);
                            op = $"merge {patch}";
                            doc.ApplyMergePatch(patch);
                            MNode target = n.Kids[c];
                            if (target.Kind != 's')
                            {
                                foreach (MNode k in target.Kids)
                                {
                                    Kill(k);
                                }

                                target.Kind = 's';
                                target.Keys = [];
                                target.Kids = [];
                            }

                            target.Value = newValue;
                            Kill(n.Kids[a]);
                            Kill(n.Kids[b]);
                            foreach (int index in new[] { a, b }.OrderByDescending(x => x))
                            {
                                n.Keys.RemoveAt(index);
                                n.Kids.RemoveAt(index);
                            }

                            break;
                        }

                    case 6 when n.Kind == 'q' && n.Kids.Count > 0:
                        {
                            // A two-operation patch: insert in front, then remove the item that is now second.
                            MNode leaf = Scalar();
                            op = $"patch add {pick.Path}/0 then remove {pick.Path}/1";
                            doc.ApplyPatch($"[{{\"op\":\"add\",\"path\":\"{pick.Path}/0\",\"value\":\"{leaf.Value}\"}},{{\"op\":\"remove\",\"path\":\"{pick.Path}/1\"}}]");
                            n.Kids.Insert(0, leaf);
                            Kill(n.Kids[1]);
                            n.Kids.RemoveAt(1);
                            break;
                        }

                    default:
                        continue;
                }

                log.Append("\n-- ").Append(op).Append('\n').Append(doc.ToString());

                // The document, read afresh, must be the model.
                using (YamlDocument fresh = YamlDocument.Parse(doc.ToString()))
                {
                    Assert.True(Sig(root) == Sig(fresh.Root), $"document differs from the model after: {op}\n{log}");
                }

                CheckViews(tracked, log.ToString());
                Capture(doc.Root, root, tracked);
            }
        }
    }
}
