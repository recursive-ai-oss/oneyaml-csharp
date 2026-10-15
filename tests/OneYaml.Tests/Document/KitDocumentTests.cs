using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using OneYaml.Documents;
using Xunit;

namespace OneYaml.Tests.Document;

/// <summary>Runs the kit's round-trip and edit cases, and the corpora, through the document.</summary>
public class KitDocumentTests
{
    public static IEnumerable<object[]> RoundTripCases() => Corpus.Rows(Corpus.Tier(KitCaseIds("roundtrip", _ => true), large: false));

    public static IEnumerable<object[]> LargeRoundTripCases() => Corpus.Rows(Corpus.Tier(KitCaseIds("roundtrip", _ => true), large: true));

    public static IEnumerable<object[]> EditCases() => Corpus.Rows(Corpus.Tier(KitCaseIds("edit", _ => true), large: false));

    public static IEnumerable<object[]> LargeEditCases() => Corpus.Rows(Corpus.Tier(KitCaseIds("edit", _ => true), large: true));

    public static IEnumerable<object[]> SuiteCases() => Corpus.Rows(Corpus.SuiteCaseIds());

    public static IEnumerable<object[]> WorkloadCases() => Corpus.Rows(Workloads().Select(d => Path.GetFileName(d)));

    private static IEnumerable<string> KitCaseIds(string category, Func<JsonElement, bool> keep)
    {
        string? kit = Corpus.KitDirectory;
        if (kit is null)
        {
            yield break;
        }

        string root = Path.Combine(kit, "suites", "own", "cases", category);
        foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            using JsonDocument doc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
            if (keep(doc.RootElement))
            {
                yield return category + "/" + Path.GetFileName(dir);
            }
        }
    }

    /// <summary>Workload directories: prepared ones when ONEYAML_WORKLOADS names them, else the kit's real files.</summary>
    internal static IEnumerable<string> Workloads()
    {
        string? prepared = Environment.GetEnvironmentVariable("ONEYAML_WORKLOADS");
        if (!string.IsNullOrEmpty(prepared) && Directory.Exists(Path.Combine(prepared, "workloads")))
        {
            return Directory.EnumerateDirectories(Path.Combine(prepared, "workloads")).Where(HasInput).OrderBy(d => d, StringComparer.Ordinal);
        }

        string? kit = Corpus.KitDirectory;
        string real = kit is null ? string.Empty : Path.Combine(kit, "bench", "workloads", "real");
        return Directory.Exists(real) ? Directory.EnumerateDirectories(real).Where(HasInput).OrderBy(d => d, StringComparer.Ordinal) : [];
    }

    private static bool HasInput(string dir) => File.Exists(Path.Combine(dir, "input.yaml"));

    private static void AssertEqualBytes(byte[] expected, byte[] actual, string what)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            int at = 0;
            while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
            {
                at++;
            }

            Assert.Fail($"{what}: bytes differ at {at} (expected {expected.Length} bytes, got {actual.Length})\n--- expected\n{Show(expected)}\n--- actual\n{Show(actual)}");
        }
    }

    private static string Show(byte[] b) => Encoding.UTF8.GetString(b).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    internal static void AssertSameModel(byte[] bytes, YamlDocument doc)
    {
        List<Model> fromReader = Model.FromReader(bytes);
        List<Model> fromTree = Model.FromState(doc.State);
        if (fromReader.Count == 0)
        {
            // A stream with no document still has one, empty.
            Assert.Single(fromTree);
            Assert.Equal("null", fromTree[0].Text);
            return;
        }

        Assert.Equal(fromReader.Count, fromTree.Count);
        for (int i = 0; i < fromReader.Count; i++)
        {
            string? diff = Model.Diff(fromReader[i], fromTree[i]);
            Assert.True(diff is null, $"document {i}: the tree differs from the reader's events: {diff}");
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Round_trip_case_writes_the_input_back_byte_for_byte(string id) => RunRoundTrip(id);

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(LargeRoundTripCases))]
    public void Large_generated_round_trip_case_writes_the_input_back_byte_for_byte(string id) => RunRoundTrip(id);

    private static void RunRoundTrip(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        using JsonDocument caseDoc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
        byte[] input = KitInput.Read(dir, caseDoc.RootElement);
        byte[] expected = File.ReadAllBytes(Path.Combine(dir, "expected.yaml"));
        using YamlDocument doc = YamlDocument.Parse(input);
        AssertEqualBytes(expected, doc.ToBytes(), "round trip");
        AssertEqualBytes(expected, ToViaStream(doc), "WriteTo(Stream)");
        AssertSameModel(input, doc);
    }

    private static byte[] ToViaStream(YamlDocument doc)
    {
        using var ms = new MemoryStream();
        doc.WriteTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(SuiteCases))]
    public void Suite_input_that_is_valid_round_trips_and_has_the_readers_structure(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string dir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        byte[] input = File.ReadAllBytes(Path.Combine(dir, "in.yaml"));
        EventText.Result events = EventText.Read(input);
        if (events.Error is not null)
        {
            // Rejected input: the document must reject it too, with a YamlException.
            Assert.ThrowsAny<YamlException>(() => YamlDocument.Parse(input));
            return;
        }

        // Some valid suite inputs repeat a key (an empty one, say); lossless editing allows that on request.
        using YamlDocument doc = YamlDocument.Parse(input, new YamlDocumentOptions { AllowDuplicateKeys = true });
        AssertEqualBytes(input, doc.ToBytes(), "round trip");
        AssertSameModel(input, doc);
    }

    [Theory]
    [MemberData(nameof(WorkloadCases))]
    public void Workload_round_trips_and_has_the_readers_structure(string name)
    {
        if (name.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Workloads().First(d => Path.GetFileName(d) == name);
        byte[] input = File.ReadAllBytes(Path.Combine(dir, "input.yaml"));
        using YamlDocument doc = YamlDocument.Parse(input);
        AssertEqualBytes(input, doc.ToBytes(), "round trip");
        AssertSameModel(input, doc);
    }

    [Theory]
    [MemberData(nameof(WorkloadCases))]
    public void Workload_edit_script_gives_the_expected_bytes_and_structure(string name)
    {
        if (name.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Workloads().First(d => Path.GetFileName(d) == name);
        string editFile = Path.Combine(dir, "edit.json");
        if (!File.Exists(editFile))
        {
            Assert.Skip("this workload has no edit script (set ONEYAML_WORKLOADS to prepared workloads)");
        }

        byte[] input = File.ReadAllBytes(Path.Combine(dir, "input.yaml"));
        byte[] expected = File.ReadAllBytes(Path.Combine(dir, "edit.expected.yaml"));
        using JsonDocument edit = JsonDocument.Parse(File.ReadAllBytes(editFile));
        using YamlDocument doc = YamlDocument.Parse(input);
        List<Model> model = Model.FromReader(input);
        foreach (JsonElement op in edit.RootElement.GetProperty("ops").EnumerateArray())
        {
            ApplyOp(doc, model, op, 0);
        }

        AssertEqualBytes(expected, doc.ToBytes(), "edit script");
        AssertStructure(model, doc.ToBytes());
    }

    // ---- Edit cases ----

    [Theory]
    [MemberData(nameof(EditCases))]
    public void Edit_case_gives_the_expected_bytes_or_error(string id) => RunEdit(id);

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(LargeEditCases))]
    public void Large_generated_edit_case_gives_the_expected_bytes_or_error(string id) => RunEdit(id);

    private static void RunEdit(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        using JsonDocument caseDoc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
        JsonElement root = caseDoc.RootElement;
        JsonElement operation = root.GetProperty("operation");
        bool merge = operation.TryGetProperty("format", out JsonElement format) && format.GetString() == "merge-patch";
        JsonElement patch = operation.GetProperty("patch");
        int index = operation.TryGetProperty("document", out JsonElement di) ? di.GetInt32() : 0;
        byte[] input = KitInput.Read(dir, root);
        JsonElement expect = root.GetProperty("expect");
        string kind = expect.GetProperty("kind").GetString()!;
        var options = new YamlDocumentOptions { AllowDuplicateKeys = true };

        // The same patch in every form the document accepts must give the same result.
        var forms = new List<Action<YamlDocument>>
        {
            d => Apply(d, patch.GetRawText(), merge, index),
            d => Apply(d, Encoding.UTF8.GetBytes(patch.GetRawText()), merge, index),
            d => Apply(d, System.Text.Json.Nodes.JsonNode.Parse(patch.GetRawText()), merge, index),
        };
        if (!merge)
        {
            forms.Add(d => d.ApplyPatch(YamlPatch.Parse(patch.GetRawText()), index));
        }

        byte[]? first = null;
        foreach (Action<YamlDocument> run in forms)
        {
            using YamlDocument doc = YamlDocument.Parse(input, options);
            if (kind == "error")
            {
                var ex = Assert.Throws<YamlException>(() => run(doc));
                Assert.Equal(CategoryOf(expect.GetProperty("category").GetString()!), ex.Category);
                if (!merge && patch.ValueKind == JsonValueKind.Array && patch.GetArrayLength() > 0 && index < doc.Documents.Count)
                {
                    Assert.NotNull(ex.OperationIndex);
                    Assert.True(ex.OperationIndex < patch.GetArrayLength());
                    if (patch.GetArrayLength() == 1)
                    {
                        Assert.Equal(0, ex.OperationIndex);
                    }
                }

                // A failed patch leaves the whole stream as it was, and the views still work.
                AssertEqualBytes(input, doc.ToBytes(), "document after the failed patch");
                AssertSameModel(input, doc);
                _ = doc.Root.Kind;
                continue;
            }

            run(doc);
            byte[] expected = File.ReadAllBytes(Path.Combine(dir, expect.GetProperty("file").GetString()!));
            byte[] actual = doc.ToBytes();
            AssertEqualBytes(expected, actual, "patched document");
            first ??= actual;
            AssertEqualBytes(first, actual, "the patch in another form");
            AssertSameModel(actual, doc);
            if (ReferenceEquals(run, forms[0]))
            {
                List<Model> model = Model.FromReader(input);
                PatchOracle.Apply(model, index, patch, merge);
                AssertStructure(model, actual, expand: PatchOracle.NeedsExpansion(patch, merge));
            }
        }
    }

    private static void Apply(YamlDocument doc, string json, bool merge, int index)
    {
        if (merge)
        {
            doc.ApplyMergePatch(json, index);
        }
        else
        {
            doc.ApplyPatch(json, index);
        }
    }

    private static void Apply(YamlDocument doc, byte[] json, bool merge, int index)
    {
        if (merge)
        {
            doc.ApplyMergePatch(json, index);
        }
        else
        {
            doc.ApplyPatch(json, index);
        }
    }

    private static void Apply(YamlDocument doc, System.Text.Json.Nodes.JsonNode? json, bool merge, int index)
    {
        if (merge)
        {
            doc.ApplyMergePatch(json, index);
        }
        else
        {
            doc.ApplyPatch(json, index);
        }
    }

    private static YamlErrorCategory CategoryOf(string name) => name switch
    {
        "patch-path" => YamlErrorCategory.PatchPath,
        "patch-test" => YamlErrorCategory.PatchTest,
        "patch-invalid" => YamlErrorCategory.PatchInvalid,
        "refused-alias" => YamlErrorCategory.RefusedAlias,
        "refused-ambiguous" => YamlErrorCategory.RefusedAmbiguous,
        _ => throw new InvalidOperationException(name),
    };

    private static void AssertStructure(List<Model> expected, byte[] output, bool expand = false)
    {
        List<Model> actual = Model.FromReader(output);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            // Expanding aliases compares the data model: a copy may keep an alias or write the value out.
            string? diff = expand ? Model.Diff(Model.Expand(expected[i]), Model.Expand(actual[i])) : Model.Diff(expected[i], actual[i]);
            Assert.True(diff is null, $"document {i} differs from the expected model: {diff}");
        }
    }

    // Applies one operation to the document and to the expected model.
    internal static void ApplyOp(YamlDocument doc, List<Model> models, JsonElement op, int document)
    {
        string name = op.GetProperty("op").GetString()!;
        string path = op.GetProperty("path").GetString()!;
        if (name != "remove" && !op.TryGetProperty("value", out _))
        {
            Assert.Skip("an operation without a value: the patch layer validates operations");
        }

        object? value = op.TryGetProperty("value", out JsonElement v) ? ToObject(v) : null;
        switch (name)
        {
            case "add":
                doc.Add(path, value, document);
                break;
            case "replace":
                doc.Write(path, value, document, PointerOps.Mode.Replace);
                break;
            default:
                doc.Remove(path, document);
                break;
        }

        // The operation worked, so the model can follow it.
        Model.ApplyTo(models[document], name, path, op.TryGetProperty("value", out JsonElement mv) ? mv : default);
    }

    internal static object? ToObject(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                {
                    string raw = e.GetRawText();
                    if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                    {
                        return e.GetDouble();
                    }

                    return e.TryGetInt64(out long l) ? l : BigInteger.Parse(raw, CultureInfo.InvariantCulture);
                }

            case JsonValueKind.Array:
                return e.EnumerateArray().Select(ToObject).ToList();
            default:
                {
                    var d = new Dictionary<string, object?>();
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        d[p.Name] = ToObject(p.Value);
                    }

                    return d;
                }
        }
    }
}
