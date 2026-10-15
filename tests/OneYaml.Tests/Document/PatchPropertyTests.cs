using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>
/// Random multi-operation patches over real documents. A patch that works must give exactly the
/// structure an independent tree model says; a patch that fails, anywhere in its operations, must
/// leave every byte of the stream as it was.
/// </summary>
public class PatchPropertyTests
{
    private static readonly int Patches = int.TryParse(Environment.GetEnvironmentVariable("ONEYAML_FUZZ_STEPS"), out int n) ? Math.Max(n, 1) * 2 : 8;

    private static readonly string[] NewKeys = ["k", "", "a b", "yes", "1", "z"];

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 12).Select(i => new object[] { i });

    private static JsonNode? ToJson(object? v)
    {
        switch (v)
        {
            case null: return null;
            case bool b: return JsonValue.Create(b);
            case long l: return JsonValue.Create(l);
            case double d: return JsonValue.Create(d);
            case string s: return JsonValue.Create(s);
            case IDictionary<string, object?> dict:
                {
                    var o = new JsonObject();
                    foreach (KeyValuePair<string, object?> kv in dict)
                    {
                        o[kv.Key] = ToJson(kv.Value);
                    }

                    return o;
                }

            default:
                {
                    var a = new JsonArray();
                    foreach (object? item in (IEnumerable<object?>)v)
                    {
                        a.Add(ToJson(item));
                    }

                    return a;
                }
        }
    }

    // The value of a model node as JSON, when JSON can say it (string keys, finite numbers).
    private static bool TryJson(Model root, Model m, out JsonNode? json)
    {
        json = null;
        if (m.Kind == "alias")
        {
            return false;
        }

        if (m.Tag is not null)
        {
            // A tag may not fit its text, and then the document refuses to read the value.
            return false;
        }

        switch (m.Kind)
        {
            case "scalar":
                switch (m.Type)
                {
                    case "null": return true;
                    case "bool": json = JsonValue.Create(m.Text == "true"); return true;
                    case "str": json = JsonValue.Create(m.Text); return true;
                    case "int": json = JsonNode.Parse(m.Text); return true;
                    default:
                        if (m.Text is ".nan" or "Infinity" or "-Infinity" || !double.TryParse(m.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                        {
                            return false;
                        }

                        json = JsonValue.Create(d);
                        return true;
                }

            case "seq":
                {
                    var a = new JsonArray();
                    foreach (Model k in m.Kids)
                    {
                        if (!TryJson(root, k, out JsonNode? item))
                        {
                            return false;
                        }

                        a.Add(item);
                    }

                    json = a;
                    return true;
                }

            default:
                {
                    var o = new JsonObject();
                    for (int i = 0; i < m.Kids.Count; i += 2)
                    {
                        Model key = m.Kids[i];
                        if (key.Kind != "scalar" || key.Type != "str" || o.ContainsKey(key.Raw) || !TryJson(root, m.Kids[i + 1], out JsonNode? item))
                        {
                            return false;
                        }

                        o[key.Raw] = item;
                    }

                    json = o;
                    return true;
                }
        }
    }

    // JSON text in which a double always keeps a fraction or exponent, so that it stays a float when read back.
    private static void WriteJson(System.Text.StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                bool firstMember = true;
                foreach (KeyValuePair<string, JsonNode?> kv in o)
                {
                    if (!firstMember)
                    {
                        sb.Append(',');
                    }

                    firstMember = false;
                    sb.Append(JsonValue.Create(kv.Key)!.ToJsonString()).Append(':');
                    WriteJson(sb, kv.Value);
                }

                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteJson(sb, a[i]);
                }

                sb.Append(']');
                break;
            default:
                if (node.AsValue().TryGetValue(out JsonElement e))
                {
                    sb.Append(e.GetRawText());
                }
                else if (node.AsValue().TryGetValue(out double d))
                {
                    string text = d.ToString("R", CultureInfo.InvariantCulture);
                    sb.Append(text.AsSpan().IndexOfAny('.', 'E', 'e') >= 0 ? text : text + ".0");
                }
                else
                {
                    sb.Append(node.ToJsonString());
                }

                break;
        }
    }

    private static string JsonText(JsonNode? node)
    {
        var sb = new System.Text.StringBuilder();
        WriteJson(sb, node);
        return sb.ToString();
    }

    private static string Destination(Random rng, DocumentEditFuzzTests.Target t)
    {
        if (t.Node.Kind == "map")
        {
            return t.Path + "/" + DocumentEditFuzzTests.Escape(NewKeys[rng.Next(NewKeys.Length)]);
        }

        if (t.Node.Kind == "seq")
        {
            return t.Path + "/" + (rng.Next(3) == 0 ? "-" : rng.Next(t.Node.Kids.Count + 1).ToString(CultureInfo.InvariantCulture));
        }

        return t.Path;
    }

    private static JsonObject Op(string op, string path, JsonNode? value = null, bool hasValue = false, string? from = null)
    {
        var o = new JsonObject { ["op"] = op, ["path"] = path };
        if (from is not null)
        {
            o["from"] = from;
        }

        if (hasValue)
        {
            o["value"] = value;
        }

        return o;
    }

    private static JsonObject FailingOp(Random rng) => rng.Next(6) switch
    {
        0 => Op("test", string.Empty, JsonValue.Create("\u0001 never the value"), hasValue: true),
        1 => Op("replace", "/no-such-member-\u00e9/x", JsonValue.Create(1), hasValue: true),
        2 => Op("remove", "/no-such-member-\u00e9"),
        3 => Op("move", "/b", from: "/no-such-member-\u00e9"),
        4 => Op("copy", "/b", from: "/no-such-member-\u00e9"),
        _ => Op("add", "no-leading-slash", JsonValue.Create(1), hasValue: true),
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_patches_are_atomic_and_give_the_structure_the_model_says(int seed)
    {
        List<byte[]> inputs = DocumentEditFuzzTests.LoadInputs();
        var rng = new Random((seed * 104729) + 17 + (int.TryParse(Environment.GetEnvironmentVariable("ONEYAML_FUZZ_SALT"), out int salt) ? salt * 7919 : 0));
        var options = new YamlDocumentOptions { AllowDuplicateKeys = true };
        int succeeded = 0;
        int refused = 0;
        int injected = 0;
        int compared = 0;
        foreach (byte[] input in inputs.Where((_, i) => i % 12 == seed))
        {
            using YamlDocument doc = YamlDocument.Parse(input, options);
            List<Model> models = Model.FromReader(input);
            if (models.Count == 0)
            {
                continue;
            }

            var history = new System.Text.StringBuilder();
            history.AppendLine("input:").AppendLine(System.Text.Encoding.UTF8.GetString(input));
            for (int round = 0; round < Patches; round++)
            {
                int d = rng.Next(models.Count);
                Model work = models[d].Clone();
                var ops = new JsonArray();
                bool oracleOk = true;
                bool moved = false;
                int count = rng.Next(1, 6);
                for (int i = 0; i < count; i++)
                {
                    List<DocumentEditFuzzTests.Target> targets = DocumentEditFuzzTests.Paths(work);
                    DocumentEditFuzzTests.Target t = targets[rng.Next(targets.Count)];
                    JsonObject op;
                    try
                    {
                        switch (rng.Next(6))
                        {
                            case 0:
                                {
                                    object? v = DocumentEditFuzzTests.RandomValue(rng, 0);
                                    string path = Destination(rng, t);
                                    op = Op("add", path, ToJson(v), hasValue: true);
                                    Model.ApplyObject(work, "add", path, v);
                                    break;
                                }

                            case 1:
                                {
                                    object? v = DocumentEditFuzzTests.RandomValue(rng, 0);
                                    op = Op("replace", t.Path, ToJson(v), hasValue: true);
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
                                    DocumentEditFuzzTests.Target from = targets[rng.Next(targets.Count)];
                                    string path = Destination(rng, t);
                                    op = Op("copy", path, from: from.Path);
                                    Model? value = Model.CopyValue(work, from.Path);
                                    if (value is null)
                                    {
                                        // An alias in the copy may stay an alias or be written out; the model does not follow.
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
                                    DocumentEditFuzzTests.Target from = targets[rng.Next(targets.Count)];
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
                                    if (!TryJson(work, t.Node, out JsonNode? json) || t.Node.Kind == "alias")
                                    {
                                        continue;
                                    }

                                    op = Op("test", t.Path, json, hasValue: true);
                                    break;
                                }
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException or FormatException)
                    {
                        // The model cannot follow this operation (a path that the earlier ones removed, say).
                        oracleOk = false;
                        break;
                    }

                    ops.Add(op);
                    if (!oracleOk)
                    {
                        break;
                    }
                }

                int failAt = -1;
                if (rng.Next(2) == 0)
                {
                    failAt = rng.Next(ops.Count + 1);
                    ops.Insert(failAt, FailingOp(rng));
                    injected++;
                }

                byte[] before = doc.ToBytes();
                JsonNode patch = ops;
                history.AppendLine("patch " + JsonText(patch) + " on document " + d);
                try
                {
                    if (rng.Next(2) == 0)
                    {
                        doc.ApplyPatch(JsonText(patch), d);
                    }
                    else
                    {
                        doc.ApplyPatch(patch, d);
                    }
                }
                catch (YamlException ex)
                {
                    refused++;
                    string what = $"{JsonText(patch)} on document {d} of:\n{System.Text.Encoding.UTF8.GetString(before)}\n--- history\n{history}";
                    Assert.True(ex.OperationIndex is not null, $"{ex.Category}: {ex.Message}\n{what}");
                    if (failAt >= 0)
                    {
                        Assert.True(ex.OperationIndex <= failAt, $"failed at {ex.OperationIndex}, past the injected failure at {failAt}\n{what}");
                    }
                    else
                    {
                        // A copy or move writes out what an alias stands for. A value that expands past the alias
                        // budget is refused by design, and the nested-alias documents in the corpus have such values.
                        bool expands = ops.Any(o => o?["op"]?.GetValue<string>() is "copy" or "move");
                        Assert.True(
                            ex.Category is YamlErrorCategory.RefusedAlias or YamlErrorCategory.PatchPath or YamlErrorCategory.RefusedAmbiguous or YamlErrorCategory.PatchInvalid
                            || (expands && ex.Category == YamlErrorCategory.Limit),
                            $"{ex.Category}: {ex.Message}\n{what}");
                    }

                    byte[] after = doc.ToBytes();
                    Assert.True(before.AsSpan().SequenceEqual(after), "a failed patch changed the document\n" + what);
                    KitDocumentTests.AssertSameModel(after, doc);
                    continue;
                }

                string applied = $"{JsonText(patch)} on document {d} of:\n{System.Text.Encoding.UTF8.GetString(before)}\n--- history\n{history}";
                Assert.True(failAt < 0, "the injected failing operation did not fail\n" + applied);
                succeeded++;
                byte[] result = doc.ToBytes();
                KitDocumentTests.AssertSameModel(result, doc);
                if (!oracleOk)
                {
                    models = Model.FromReader(result);
                    continue;
                }

                models[d] = work;
                List<Model> actual = Model.FromReader(result);
                Assert.Equal(models.Count, actual.Count);
                for (int i = 0; i < models.Count; i++)
                {
                    string? diff = moved ? Model.Diff(Model.Expand(models[i]), Model.Expand(actual[i])) : Model.Diff(models[i], actual[i]);
                    Assert.True(diff is null, $"{diff}\n{applied}\n--- after:\n{System.Text.Encoding.UTF8.GetString(result)}");
                }

                compared++;
            }

            // The model follows only patches that worked; start over from what the document holds.
            models = Model.FromReader(doc.ToBytes());
        }

        Assert.True(succeeded > 0 && injected > 0 && compared > 0, "the run exercised no patch");
        Console.WriteLine($"patch property seed {seed}: {succeeded} applied ({compared} compared with the model), {refused} failed ({injected} with an injected failing operation)");
    }

    [Fact]
    public void Moving_a_value_that_expands_past_the_alias_budget_is_a_limit_error_and_changes_nothing()
    {
        // Each level holds ten references to the one before, so the last value stands for about 10^8 nodes.
        // An anchor defined later cannot be referenced from an earlier place, so the move has to write the value out.
        var sb = new System.Text.StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
        for (char c = 'b'; c <= 'i'; c++)
        {
            char prev = (char)(c - 1);
            sb.Append(c).Append(": &").Append(c).Append(" [").Append(string.Join(", ", Enumerable.Repeat("*" + prev, 10))).Append("]\n");
        }

        sb.Append("j: [1, 2, 3, 4, 5, *i]\n");
        byte[] before = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        using YamlDocument doc = YamlDocument.Parse(before);
        var ex = Assert.Throws<YamlException>(() => doc.ApplyPatch("[{\"op\":\"move\",\"path\":\"/a/2\",\"from\":\"/j/5\"}]"));
        Assert.Equal(YamlErrorCategory.Limit, ex.Category);
        Assert.Equal(0, ex.OperationIndex);
        Assert.True(before.AsSpan().SequenceEqual(doc.ToBytes()));
    }
}
