using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneYaml.Tests.Document;

namespace OneYaml.Fuzzing;

/// <summary>
/// Random RFC 6902 and RFC 7396 patches over a mutated document. A patch that works gives the structure an
/// independent tree model says and re-parses; one that fails leaves every byte as it was.
/// </summary>
internal sealed class PatchTarget : FuzzTarget
{
    private static readonly string[] s_strings =
    [
        "", "x", "hello world", "yes", "null", "~", "true", "123", "1.5", "0x1F", "a: b", "- x", "# c", "a #b",
        " lead", "trail ", "multi\nline\n", "multi\nline", "\nstart", "tab\there", "quote's", "dq\"x", "back\\slash",
        "\u00e9", "\U0001F600", "{a}", "[b]", "---", "...", "&a", "*b", "!t", "|", ">", "%x", "@", "`", "a:b", ":", "?", "-",
        "key: |", "  two\n  lines\n", "x\n\n", "\n", "\u0001x", "last:", "'", "\"", "a\n b\n", "   ", "# not a comment\nreally",
        ".inf", "0o17", "1e3", "True", "NULL", "<<", "= ", "! x",
    ];

    private static readonly string[] s_keys = ["k", "", "a b", "k: v", "yes", "1", "\u00e9", "- d", "#h", "z", "<<", "a", "b"];

    // The patch text can nest as deep as the document does.
    private static readonly JsonDocumentOptions s_deep = new() { MaxDepth = 100_000 };

    private long _applied;
    private long _refused;
    private long _compared;

    public override string Name => "patch";

    public override bool Run(byte[] input, int hint, YamlLimits limits)
    {
        var options = new YamlDocumentOptions { AllowDuplicateKeys = true, Limits = limits };
        List<Model> models;
        try
        {
            models = Model.FromReader(input);
        }
        catch (YamlException)
        {
            return false;
        }

        if (models.Count == 0)
        {
            return false;
        }

        YamlDocument doc;
        try
        {
            using (Library())
            {
                doc = YamlDocument.Parse(input, options);
            }
        }
        catch (YamlException)
        {
            return false;
        }

        using (doc)
        {
            Random rng = Seeded(input, hint);
            bool hugeNumber = LongestDigitRun(input) > 4000;
            string history = string.Empty;
            for (int round = 0; round < 4; round++)
            {
                int d = rng.Next(models.Count);
                bool merge = round == 3;
                string patchText;
                Model work = models[d].Clone();
                bool oracleOk = true;
                bool moved = false;
                int failAt = -1;
                if (merge)
                {
                    patchText = MergePatch(rng, work);
                }
                else
                {
                    var ops = new List<string>();
                    BuildOps(rng, work, ops, ref oracleOk, ref moved);
                    if (rng.Next(2) == 0)
                    {
                        failAt = rng.Next(ops.Count + 1);
                        ops.Insert(failAt, FailingOp(rng));
                    }

                    patchText = "[" + string.Join(",", ops) + "]";
                }

                history += $"{(merge ? "merge" : "patch")} {patchText} on document {d}\n";
                byte[] before = doc.ToBytes();
                try
                {
                    using (Library())
                    {
                        Apply(doc, rng, patchText, merge, d);
                    }
                }
                catch (YamlException ex)
                {
                    _refused++;
                    CheckRefusal(ex, merge, failAt, hugeNumber, limits, patchText, history);
                    byte[] after = doc.ToBytes();
                    if (!before.AsSpan().SequenceEqual(after))
                    {
                        throw new FuzzFailure("patch-not-atomic", $"a failed patch changed the document\n{history}");
                    }

                    continue;
                }

                if (failAt >= 0)
                {
                    throw new FuzzFailure("patch-injected-failure-applied", $"the injected failing operation did not fail\n{history}");
                }

                _applied++;
                byte[] result = doc.ToBytes();
                if (Plant(input))
                {
                    result = [.. result, .. "\nplanted: 1\n"u8];
                }

                List<Model> actual;
                try
                {
                    using YamlDocument again = YamlDocument.Parse(result, new YamlDocumentOptions { AllowDuplicateKeys = true, Limits = YamlLimits.None });
                    if (!again.ToBytes().AsSpan().SequenceEqual(result))
                    {
                        throw new FuzzFailure("patch-result-roundtrip", $"the patched stream does not write back as it is\n{history}");
                    }

                    actual = Model.FromReader(result);
                }
                catch (YamlException ex)
                {
                    throw new FuzzFailure("patch-result-does-not-parse", $"{ex.Category}: {ex.Message}\n{history}\n--- after:\n{Encoding.UTF8.GetString(result)}");
                }

                if (!oracleOk)
                {
                    models = actual;
                    continue;
                }

                List<Model> expected = models.ToList();
                expected[d] = work;
                if (merge)
                {
                    expected[d] = models[d].Clone();
                    using JsonDocument json = JsonDocument.Parse(patchText, s_deep);
                    PatchOracle.Apply(expected, d, json.RootElement, merge: true);
                }

                if (actual.Count != expected.Count)
                {
                    throw new FuzzFailure("patch-document-count", $"{expected.Count} documents expected, {actual.Count} read\n{history}");
                }

                bool aliases = expected.Any(HasAlias);
                if (!(moved && aliases))
                {
                    for (int i = 0; i < expected.Count; i++)
                    {
                        string? diff = moved ? Model.Diff(Model.Expand(expected[i]), Model.Expand(actual[i])) : Model.Diff(expected[i], actual[i]);
                        if (diff is not null)
                        {
                            throw new FuzzFailure("patch-model-mismatch", $"{diff}\n{history}\n--- before:\n{Encoding.UTF8.GetString(before)}\n--- after:\n{Encoding.UTF8.GetString(result)}");
                        }
                    }

                    _compared++;
                }

                models = actual;
            }
        }

        return true;
    }

    public override string Stats() => $"applied={_applied} refused={_refused} compared={_compared}";

    private static void CheckRefusal(YamlException ex, bool merge, int failAt, bool hugeNumber, YamlLimits limits, string patchText, string history)
    {
        if (!merge && ex.OperationIndex is null)
        {
            throw new FuzzFailure("patch-error-without-operation-index", $"{ex.Category}: {ex.Message}\n{history}");
        }

        // A number past the digit limit is refused where it is read or compared, which may be before the injected operation.
        if (hugeNumber && ex.Category == YamlErrorCategory.Limit)
        {
            return;
        }

        if (failAt >= 0)
        {
            if (ex.OperationIndex > failAt)
            {
                throw new FuzzFailure("patch-failed-past-injected", $"failed at {ex.OperationIndex}, past the injected failure at {failAt}\n{history}");
            }

            return;
        }

        bool allowed = ex.Category is YamlErrorCategory.RefusedAlias or YamlErrorCategory.PatchPath or YamlErrorCategory.RefusedAmbiguous or YamlErrorCategory.PatchInvalid
            // A limit may bite any edit that makes the document larger; under the tight preset that is expected, and a
            // copy or a move may expand aliases past the budget under any preset.
            || (ex.Category == YamlErrorCategory.Limit && (limits != YamlLimits.Default || patchText.Contains("\"copy\"", StringComparison.Ordinal) || patchText.Contains("\"move\"", StringComparison.Ordinal)));
        if (!allowed)
        {
            throw new FuzzFailure("patch-unexpected-category-" + ex.Category, $"{ex.Category}: {ex.Message}\n{history}");
        }
    }

    private static void Apply(YamlDocument doc, Random rng, string patch, bool merge, int d)
    {
        switch (rng.Next(3))
        {
            case 0 when !merge:
                doc.ApplyPatch(patch, d);
                break;
            case 1 when !merge:
                doc.ApplyPatch(Encoding.UTF8.GetBytes(patch), d);
                break;
            case 2 when !merge:
                doc.ApplyPatch(JsonNode.Parse(patch, documentOptions: s_deep), d);
                break;
            case 0:
                doc.ApplyMergePatch(patch, d);
                break;
            case 1:
                doc.ApplyMergePatch(Encoding.UTF8.GetBytes(patch), d);
                break;
            default:
                doc.ApplyMergePatch(JsonNode.Parse(patch, documentOptions: s_deep), d);
                break;
        }
    }

    private static int LongestDigitRun(byte[] input)
    {
        int best = 0;
        int run = 0;
        foreach (byte b in input)
        {
            run = b is >= (byte)'0' and <= (byte)'9' ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    private static bool HasAlias(Model root)
    {
        var stack = new Stack<Model>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Model m = stack.Pop();
            if (m.Kind == "alias")
            {
                return true;
            }

            foreach (Model k in m.Kids)
            {
                stack.Push(k);
            }
        }

        return false;
    }

    private sealed record Target(string Path, Model Node);

    private static List<Target> Paths(Model root)
    {
        var result = new List<Target>();
        var stack = new Stack<(string Path, Model Node)>();
        stack.Push(("", root));
        while (stack.Count > 0)
        {
            (string path, Model node) = stack.Pop();
            result.Add(new Target(path, node));
            if (result.Count > 5000)
            {
                break;
            }

            if (node.Kind == "map")
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < node.Kids.Count; i += 2)
                {
                    if (node.Kids[i].Kind == "scalar")
                    {
                        counts[node.Kids[i].Raw] = counts.GetValueOrDefault(node.Kids[i].Raw) + 1;
                    }
                }

                for (int i = 0; i < node.Kids.Count; i += 2)
                {
                    Model k = node.Kids[i];
                    if (k.Kind == "scalar" && counts[k.Raw] == 1)
                    {
                        stack.Push((path + "/" + Escape(k.Raw), node.Kids[i + 1]));
                    }
                }
            }
            else if (node.Kind == "seq")
            {
                for (int i = 0; i < node.Kids.Count; i++)
                {
                    stack.Push((path + "/" + i.ToString(CultureInfo.InvariantCulture), node.Kids[i]));
                }
            }
        }

        return result;
    }

    private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Destination(Random rng, Target t)
    {
        if (t.Node.Kind == "map")
        {
            return t.Path + "/" + Escape(s_keys[rng.Next(s_keys.Length)]);
        }

        if (t.Node.Kind == "seq")
        {
            return t.Path + "/" + (rng.Next(3) == 0 ? "-" : rng.Next(t.Node.Kids.Count + 1).ToString(CultureInfo.InvariantCulture));
        }

        return t.Path;
    }

    private static void BuildOps(Random rng, Model work, List<string> ops, ref bool oracleOk, ref bool moved)
    {
        int count = rng.Next(1, 6);
        for (int i = 0; i < count; i++)
        {
            List<Target> targets = Paths(work);
            Target t = targets[rng.Next(targets.Count)];
            string op;
            try
            {
                switch (rng.Next(6))
                {
                    case 0:
                        {
                            object? v = RandomValue(rng, 0);
                            string path = Destination(rng, t);
                            op = Op("add", path, JsonOf(v));
                            Model.ApplyObject(work, "add", path, v);
                            break;
                        }

                    case 1:
                        {
                            object? v = RandomValue(rng, 0);
                            op = Op("replace", t.Path, JsonOf(v));
                            Model.ApplyObject(work, "replace", t.Path, v);
                            break;
                        }

                    case 2:
                        {
                            if (t.Path.Length == 0)
                            {
                                continue;
                            }

                            op = Op("remove", t.Path);
                            Model.ApplyTo(work, "remove", t.Path, default);
                            break;
                        }

                    case 3:
                        {
                            Target from = targets[rng.Next(targets.Count)];
                            string path = Destination(rng, t);
                            op = Op("copy", path, from: from.Path);
                            Model? value = Model.CopyValue(work, from.Path);
                            if (value is null)
                            {
                                oracleOk = false;
                            }
                            else
                            {
                                Model.ApplyModel(work, "add", path, value);
                            }

                            moved = true;
                            break;
                        }

                    case 4:
                        {
                            Target from = targets[rng.Next(targets.Count)];
                            if (from.Path.Length == 0)
                            {
                                continue;
                            }

                            string path = Destination(rng, t);
                            op = Op("move", path, from: from.Path);
                            if (Model.CopyValue(work, from.Path) is null)
                            {
                                oracleOk = false;
                            }
                            else
                            {
                                PatchOracle.Move(work, from.Path, path);
                            }

                            moved = true;
                            break;
                        }

                    default:
                        {
                            if (t.Node.Kind == "alias" || !TryJson(t.Node, out string? json))
                            {
                                continue;
                            }

                            op = Op("test", t.Path, json);
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException or FormatException)
            {
                // The model cannot follow this operation (a path an earlier one removed, say).
                oracleOk = false;
                break;
            }

            ops.Add(op);
            if (!oracleOk)
            {
                break;
            }
        }
    }

    private static string MergePatch(Random rng, Model work)
    {
        var members = new List<string>();
        int n = rng.Next(1, 4);
        var existing = new List<string>();
        if (work.Kind == "map")
        {
            for (int i = 0; i < work.Kids.Count; i += 2)
            {
                if (work.Kids[i].Kind == "scalar")
                {
                    existing.Add(work.Kids[i].Raw);
                }
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            string key = existing.Count > 0 && rng.Next(3) > 0 ? existing[rng.Next(existing.Count)] : s_keys[rng.Next(s_keys.Length)];
            if (!used.Add(key))
            {
                continue;
            }

            object? value = rng.Next(4) == 0 ? null : RandomValue(rng, 0);
            members.Add(JsonSerializer.Serialize(key) + ":" + JsonOf(value));
        }

        return "{" + string.Join(",", members) + "}";
    }

    private static string Op(string op, string path, string? valueJson = null, string? from = null)
    {
        var sb = new StringBuilder("{\"op\":\"").Append(op).Append("\",\"path\":").Append(JsonSerializer.Serialize(path));
        if (from is not null)
        {
            sb.Append(",\"from\":").Append(JsonSerializer.Serialize(from));
        }

        if (valueJson is not null)
        {
            sb.Append(",\"value\":").Append(valueJson);
        }

        return sb.Append('}').ToString();
    }

    private static string FailingOp(Random rng) => rng.Next(6) switch
    {
        0 => Op("test", string.Empty, "\"\\u0001 never the value\""),
        1 => Op("replace", "/no-such-member-\u00e9/x", "1"),
        2 => Op("remove", "/no-such-member-\u00e9"),
        3 => Op("move", "/b", from: "/no-such-member-\u00e9"),
        4 => Op("copy", "/b", from: "/no-such-member-\u00e9"),
        _ => Op("add", "no-leading-slash", "1"),
    };

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
                return s_strings[r.Next(s_strings.Length)];
            case 8:
            case 9:
                {
                    var d = new Dictionary<string, object?>();
                    int n = r.Next(0, 4);
                    for (int i = 0; i < n; i++)
                    {
                        d[s_keys[r.Next(s_keys.Length)]] = RandomValue(r, depth + 1);
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

    // A double keeps a fraction in the JSON text, so it reads as a float and not as an integer.
    private static string Double(double d)
    {
        string text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'e') >= 0 ? text : text + ".0";
    }

    private static string JsonOf(object? v)
    {
        var sb = new StringBuilder();
        WriteJson(sb, v);
        return sb.ToString();
    }

    private static void WriteJson(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(Double(d)); break;
            case string s: sb.Append(JsonSerializer.Serialize(s)); break;
            case Dictionary<string, object?> map:
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object?> kv in map)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    WriteJson(sb, kv.Value);
                }

                sb.Append('}');
                break;
            default:
                sb.Append('[');
                bool firstItem = true;
                foreach (object? item in (List<object?>)v)
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    WriteJson(sb, item);
                }

                sb.Append(']');
                break;
        }
    }

    // The value of a model node as JSON text, when JSON can say it (string keys, finite numbers, no tags).
    private static bool TryJson(Model m, out string? json)
    {
        json = null;
        if (m.Kind == "alias" || m.Tag is not null)
        {
            return false;
        }

        switch (m.Kind)
        {
            case "scalar":
                switch (m.Type)
                {
                    case "null": json = "null"; return true;
                    case "bool": json = m.Text == "true" ? "true" : "false"; return true;
                    case "str": json = JsonSerializer.Serialize(m.Text); return true;
                    case "int": json = m.Text; return true;
                    default:
                        if (m.Text is ".nan" or "Infinity" or "-Infinity" || !double.TryParse(m.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                        {
                            return false;
                        }

                        json = Double(d);
                        return true;
                }

            case "seq":
                {
                    var parts = new List<string>();
                    foreach (Model k in m.Kids)
                    {
                        if (!TryJson(k, out string? item))
                        {
                            return false;
                        }

                        parts.Add(item!);
                    }

                    json = "[" + string.Join(",", parts) + "]";
                    return true;
                }

            default:
                {
                    var parts = new List<string>();
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < m.Kids.Count; i += 2)
                    {
                        Model key = m.Kids[i];
                        // A tagged key is left out: a core tag that does not fit its text (!!int abc) makes a key that equals
                        // no value, and the model reads that as a string.
                        if (key.Kind != "scalar" || key.Type != "str" || key.Tag is not null || !keys.Add(key.Raw) || !TryJson(m.Kids[i + 1], out string? item))
                        {
                            return false;
                        }

                        parts.Add(JsonSerializer.Serialize(key.Raw) + ":" + item);
                    }

                    json = "{" + string.Join(",", parts) + "}";
                    return true;
                }
        }
    }
}
