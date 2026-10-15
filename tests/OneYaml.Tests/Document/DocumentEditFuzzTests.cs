using System.Globalization;
using System.Numerics;
using System.Text;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Random edits over real documents. After each edit the output is parsed again and compared, in
/// full, with the model the edit should have produced; a refused edit must leave the bytes alone.
/// </summary>
public class DocumentEditFuzzTests
{
    private static readonly string[] Strings =
    [
        "", "x", "hello world", "yes", "no", "null", "~", "true", "123", "1.5", "0x1F", "a: b", "- x", "# c", "a #b",
        " lead", "trail ", "multi\nline\n", "multi\nline", "\nstart", "tab\there", "quote's", "dq\"x", "back\\slash",
        "é", "\U0001F600", "{a}", "[b]", "---", "...", "&a", "*b", "!t", "|", ">", "%x", "@", "`", "a:b", ":", "?", "-",
        "key: |", "  two\n  lines\n", "x\n\n", "\n", "\u0001x", "cr\r\nlf", "last:", "'", "\"", "a\n b\n", "   ",
        "line one\n\nline three\n", "# not a comment\nreally", "x   \ny", ".inf", "0o17", "1e3", "True", "NULL",
    ];

    private static readonly int Steps = int.TryParse(Environment.GetEnvironmentVariable("ONEYAML_FUZZ_STEPS"), out int n) ? n : 4;

    private static readonly string[] Keys = ["k", "", "a b", "k: v", "yes", "1", "é", "- d", "#h", "z"];

    private static object? RandomValue(Random r, int depth)
    {
        switch (r.Next(depth > 1 ? 8 : 11))
        {
            case 0: return null;
            case 1: return true;
            case 2: return false;
            case 3: return (long)r.Next(-5, 100000);
            case 4: return new[] { 1.5, -0.0, 1e300, 0.1, 100.0 }[r.Next(5)];
            case 5:
            case 6:
            case 7:
                return Strings[r.Next(Strings.Length)];
            case 8:
            case 9:
                {
                    var d = new Dictionary<string, object?>();
                    int n = r.Next(0, 4);
                    for (int i = 0; i < n; i++)
                    {
                        d[Keys[r.Next(Keys.Length)]] = RandomValue(r, depth + 1);
                    }

                    return d;
                }

            default:
                {
                    var l = new List<object?>();
                    int n = r.Next(0, 4);
                    for (int i = 0; i < n; i++)
                    {
                        l.Add(RandomValue(r, depth + 1));
                    }

                    return l;
                }
        }
    }

    private sealed record Target(string Path, Model Node, Model? Parent, Model? Key);

    // Every node reachable by a pointer that does not go through an alias, with unique keys only.
    private static List<Target> Paths(Model root)
    {
        var result = new List<Target>();
        var stack = new Stack<(string Path, Model Node, Model? Parent, Model? Key)>();
        stack.Push(("", root, null, null));
        while (stack.Count > 0)
        {
            (string path, Model node, Model? parent, Model? key) = stack.Pop();
            result.Add(new Target(path, node, parent, key));
            if (node.Kind == "map")
            {
                for (int i = 0; i < node.Kids.Count; i += 2)
                {
                    Model k = node.Kids[i];
                    if (k.Kind != "scalar" || node.Kids.Where((m, j) => j % 2 == 0 && m.Kind == "scalar" && m.Raw == k.Raw).Count() != 1)
                    {
                        continue;
                    }

                    stack.Push((path + "/" + Escape(k.Raw), node.Kids[i + 1], node, k));
                }
            }
            else if (node.Kind == "seq")
            {
                for (int i = 0; i < node.Kids.Count; i++)
                {
                    stack.Push((path + "/" + i.ToString(CultureInfo.InvariantCulture), node.Kids[i], node, null));
                }
            }
        }

        return result;
    }

    private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 12).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_edits_give_exactly_the_expected_structure_or_leave_the_bytes_alone(int seed)
    {
        string? yts = Corpus.SuiteDirectory;
        if (yts is null)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        var inputs = new List<byte[]>();
        foreach (string id in Corpus.SuiteCaseIds())
        {
            string dir = Path.Combine(yts, id.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(dir, "error")))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "in.yaml"));
            if (EventText.Read(bytes).Error is null)
            {
                inputs.Add(bytes);
            }
        }

        if (Corpus.KitDirectory is string kit)
        {
            foreach (string category in new[] { "edit", "roundtrip" })
            {
                foreach (string dir in Directory.EnumerateDirectories(Path.Combine(kit, "suites", "own", "cases", category)).OrderBy(d => d, StringComparer.Ordinal))
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "input.yaml"));
                    if (EventText.Read(bytes).Error is null)
                    {
                        inputs.Add(bytes);
                    }
                }
            }
        }

        foreach (string wl in KitDocumentTests.Workloads())
        {
            if (new FileInfo(Path.Combine(wl, "input.yaml")).Length < 20_000)
            {
                inputs.Add(File.ReadAllBytes(Path.Combine(wl, "input.yaml")));
            }
        }

        var rng = new Random((seed * 7919) + 1 + (int.TryParse(Environment.GetEnvironmentVariable("ONEYAML_FUZZ_SALT"), out int salt) ? salt * 104729 : 0));
        int applied = 0;
        int refused = 0;
        foreach (byte[] input in inputs.Where((_, i) => i % 12 == seed))
        {
            using YamlDocument doc = YamlDocument.Parse(input);
            List<Model> models = Model.FromReader(input);
            if (models.Count == 0)
            {
                continue;
            }

            for (int step = 0; step < Steps; step++)
            {
                int d = rng.Next(models.Count);
                List<Target> targets = Paths(models[d]);
                Target t = targets[rng.Next(targets.Count)];
                string op = new[] { "replace", "add", "remove" }[rng.Next(3)];
                object? value = RandomValue(rng, 0);
                string path = t.Path;
                if (op == "add" && t.Node.Kind is "map" or "seq")
                {
                    path = t.Node.Kind == "map" ? t.Path + "/" + Escape(Keys[rng.Next(Keys.Length)]) : t.Path + "/" + (rng.Next(3) == 0 ? "-" : rng.Next(t.Node.Kids.Count + 1).ToString(CultureInfo.InvariantCulture));
                }
                else if (op == "add")
                {
                    op = "replace";
                }

                if (op == "remove" && t.Path.Length == 0)
                {
                    continue;
                }

                byte[] before = doc.ToBytes();
                string what = $"{op} {path} <- {Describe(value)} in document {d} of:\n{Show(before)}";
                try
                {
                    switch (op)
                    {
                        case "add": doc.Add(path, value, d); break;
                        case "replace": doc.Write(path, value, d, PointerOps.Mode.Replace); break;
                        default: doc.Remove(path, d); break;
                    }
                }
                catch (YamlException ex)
                {
                    refused++;
                    Assert.True(ex.Category is YamlErrorCategory.RefusedAlias or YamlErrorCategory.PatchPath or YamlErrorCategory.RefusedAmbiguous or YamlErrorCategory.PatchInvalid, $"{ex.Category}: {ex.Message}\n{what}");
                    Assert.True(before.AsSpan().SequenceEqual(doc.ToBytes()), "a refused edit changed the document\n" + what);
                    if (ex.Category == YamlErrorCategory.RefusedAlias)
                    {
                        Assert.True(HasAnchorsInUse(models, d, t, op), $"an alias refusal without a live anchor (target {t.Path} {t.Node.Kind} anchor={t.Node.Anchor})\n" + what);
                    }

                    continue;
                }

                applied++;
                Model.ApplyObject(models[d], op, path, value);
                byte[] after = doc.ToBytes();
                List<Model> actual = Model.FromReader(after);
                Assert.Equal(models.Count, actual.Count);
                for (int i = 0; i < models.Count; i++)
                {
                    string? diff = Model.Diff(models[i], actual[i]);
                    Assert.True(diff is null, $"{diff}\n{what}\n--- after:\n{Show(after)}");
                }

                KitDocumentTests.AssertSameModel(after, doc);
            }
        }

        Assert.True(applied > 0, "no edit was applied");
        _ = refused;
    }

    private static string Show(byte[] b) => Encoding.UTF8.GetString(b);

    private static string Describe(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s.Replace("\n", "\\n", StringComparison.Ordinal) + "\"",
        IDictionary<string, object?> d => "{" + string.Join(", ", d.Select(kv => kv.Key + ": " + Describe(kv.Value))) + "}",
        IEnumerable<object?> l => "[" + string.Join(", ", l.Select(Describe)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // Whether the subtree an operation would destroy holds an anchor that an alias outside it uses.
    private static bool HasAnchorsInUse(List<Model> models, int d, Target t, string op)
    {
        Model subtree = t.Node;
        if (op == "add" || (op == "remove" && t.Key is not null))
        {
            subtree = new Model { Kind = "seq", Kids = t.Key is null ? [t.Node] : [t.Key, t.Node] };
        }

        if (op == "add")
        {
            return true;
        }

        var inside = new HashSet<string>();
        Walk(subtree, m => { if (m.Anchor is not null) { inside.Add(m.Anchor); } });
        var outside = new List<string>();
        Walk(models[d], m => { if (m.Kind == "alias") { outside.Add(m.Text); } });
        var within = new List<string>();
        Walk(subtree, m => { if (m.Kind == "alias") { within.Add(m.Text); } });
        return inside.Any(a => outside.Count(o => o == a) > within.Count(o => o == a));
    }

    private static void Walk(Model root, Action<Model> visit)
    {
        var stack = new Stack<Model>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Model m = stack.Pop();
            visit(m);
            foreach (Model k in m.Kids)
            {
                stack.Push(k);
            }
        }
    }
}
