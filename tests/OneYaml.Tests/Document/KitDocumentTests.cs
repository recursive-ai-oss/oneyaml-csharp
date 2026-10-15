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
    public static IEnumerable<object[]> RoundTripCases() => Corpus.Rows(KitCaseIds("roundtrip", _ => true));

    public static IEnumerable<object[]> EditCases() => Corpus.Rows(KitCaseIds("edit", _ => true));

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
    public void Round_trip_case_writes_the_input_back_byte_for_byte(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        byte[] input = File.ReadAllBytes(Path.Combine(dir, "input.yaml"));
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

        using YamlDocument doc = YamlDocument.Parse(input);
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
    public void Edit_case_gives_the_expected_bytes_or_error(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        using JsonDocument caseDoc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
        JsonElement root = caseDoc.RootElement;
        JsonElement operation = root.GetProperty("operation");
        if (operation.TryGetProperty("format", out _) || operation.GetProperty("patch").ValueKind != JsonValueKind.Array)
        {
            Assert.Skip("a merge patch: RFC 7396 belongs to the patch layer");
        }

        List<JsonElement> ops = operation.GetProperty("patch").EnumerateArray().ToList();
        string[] known = ["add", "remove", "replace"];
        if (ops.Any(o => !known.Contains(o.GetProperty("op").GetString())))
        {
            Assert.Skip("needs test, move or copy: RFC 6902 operations beyond add, remove and replace belong to the patch layer");
        }

        int index = operation.TryGetProperty("document", out JsonElement di) ? di.GetInt32() : 0;
        byte[] input = File.ReadAllBytes(Path.Combine(dir, "input.yaml"));
        JsonElement expect = root.GetProperty("expect");
        string kind = expect.GetProperty("kind").GetString()!;
        using YamlDocument doc = YamlDocument.Parse(input);
        List<Model> model = Model.FromReader(input);
        for (int i = 0; i < ops.Count; i++)
        {
            byte[] before = doc.ToBytes();
            try
            {
                ApplyOp(doc, model, ops[i], index);
            }
            catch (YamlException ex) when (kind == "error")
            {
                Assert.Equal(CategoryOf(expect.GetProperty("category").GetString()!), ex.Category);

                // The failing operation changed nothing. Atomicity across operations is the patch layer's.
                AssertEqualBytes(before, doc.ToBytes(), "document after the failed operation");
                if (ops.Count == 1)
                {
                    AssertEqualBytes(input, doc.ToBytes(), "document after the failed patch");
                }

                return;
            }
        }

        Assert.True(kind == "bytes", "an error was expected");
        byte[] expected = File.ReadAllBytes(Path.Combine(dir, expect.GetProperty("file").GetString()!));
        AssertEqualBytes(expected, doc.ToBytes(), "edited document");
        AssertStructure(model, doc.ToBytes());
        AssertSameModel(doc.ToBytes(), doc);
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

    private static void AssertStructure(List<Model> expected, byte[] output)
    {
        List<Model> actual = Model.FromReader(output);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            string? diff = Model.Diff(expected[i], actual[i]);
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
