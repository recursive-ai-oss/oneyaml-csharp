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
    public static IEnumerable<object[]> RoundTripCases() => Corpus.Rows(Corpus.Tier(KitCaseIds("roundtrip", _ => true), large: false), Corpus.KitVariable);

    public static IEnumerable<object[]> LargeRoundTripCases() =>
        // The kit has no round-trip case of that size at present, so an empty tier is not an error here.
        Corpus.Rows(Corpus.Tier(KitCaseIds("roundtrip", _ => true), large: true));

    // The edit cases, and the safety cases that apply a patch.
    private static IEnumerable<string> PatchableCaseIds() =>
        KitCaseIds("edit", _ => true).Concat(KitCaseIds("safety", c => c.GetProperty("operation").GetProperty("type").GetString() == "patch"));

    public static IEnumerable<object[]> EditCases() => Corpus.Rows(Corpus.Tier(PatchableCaseIds(), large: false), Corpus.KitVariable);

    public static IEnumerable<object[]> LargeEditCases() => Corpus.Rows(Corpus.Tier(PatchableCaseIds(), large: true), Corpus.KitVariable);

    public static IEnumerable<object[]> SuiteCases() => Corpus.Rows(Corpus.SuiteCaseIds(), Corpus.SuiteVariable);

    public static IEnumerable<object[]> WorkloadCases() => Corpus.Rows(Workloads().Select(d => Path.GetFileName(d)), Corpus.KitVariable, Corpus.WorkloadsVariable);

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

    /// <summary>Workload directories: the prepared set when ONEYAML_WORKLOADS names one, else the kit's real files.</summary>
    internal static IEnumerable<string> Workloads() => Corpus.Workloads();

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
            Assert.Skip($"{Corpus.KitVariable} is not set, or the kit has no round-trip case in this tier");
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
            // A prepared workload says in workload.json whether it has an edit script; one that claims a script and
            // has none is a broken data set, not a skip.
            string meta = Path.Combine(dir, "workload.json");
            if (File.Exists(meta))
            {
                using JsonDocument info = JsonDocument.Parse(File.ReadAllBytes(meta));
                if (info.RootElement.TryGetProperty("tasks", out JsonElement tasks) && tasks.EnumerateArray().Any(t => t.GetString() == "edit-script"))
                {
                    Assert.Fail($"{dir} lists an edit script but has none");
                }
            }

            Assert.Skip("this workload has no edit script");
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
        JsonElement patch = operation.TryGetProperty("patch_recipe", out JsonElement recipe) ? ExpandPatchRecipe(recipe, id) : operation.GetProperty("patch");
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
            // A bounded case allows the limit error, and, when the case lists one, the accepted result as well.
            // Any other error means the work was refused for another reason.
            JsonElement accept = default;
            bool acceptsResult = kind == "bounded" && expect.TryGetProperty("accept", out accept);
            if (kind == "error" || (kind == "bounded" && !acceptsResult))
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var ex = Assert.Throws<YamlException>(() => run(doc));
                // A case may name one category or a list of acceptable ones.
                JsonElement wanted = kind == "bounded" ? expect.GetProperty("error") : expect.GetProperty("category");
                YamlErrorCategory[] allowed = wanted.ValueKind == JsonValueKind.Array
                    ? wanted.EnumerateArray().Select(w => CategoryOf(w.GetString()!)).ToArray()
                    : [CategoryOf(wanted.GetString()!)];
                Assert.Contains(ex.Category, allowed);
                if (kind == "bounded")
                {
                    Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < expect.GetProperty("memory_mb").GetInt64() * 1024 * 1024, "the refusal allocated more than the bound");
                }

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

            if (acceptsResult)
            {
                try
                {
                    run(doc);
                }
                catch (YamlException ex)
                {
                    Assert.Equal(YamlErrorCategory.Limit, ex.Category);
                    AssertEqualBytes(input, doc.ToBytes(), "document after the refused patch");
                    continue;
                }

                byte[] want = accept.TryGetProperty("file", out JsonElement acceptFile)
                    ? File.ReadAllBytes(Path.Combine(dir, acceptFile.GetString()!))
                    : FromRecipe(accept.GetProperty("recipe"));
                AssertEqualBytes(want, doc.ToBytes(), "patched document");
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

    // A patch_recipe stands in for a patch of very many identical operations: parts of `ops` written one after another,
    // each `repeat` times. The count is checked before anything is built, and the result against the kit's pin.
    private const int MaxRecipeParts = 64;
    private const int MaxRecipeOperations = 1_000_000;

    private static JsonElement ExpandPatchRecipe(JsonElement recipe, string id)
    {
        if (recipe.ValueKind != JsonValueKind.Array || recipe.GetArrayLength() is 0 or > MaxRecipeParts)
        {
            throw new InvalidOperationException($"{id}: patch_recipe must be a list of 1 to {MaxRecipeParts} parts");
        }

        long total = 0;
        foreach (JsonElement part in recipe.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || part.EnumerateObject().Any(p => p.Name is not ("ops" or "repeat"))
                || !part.TryGetProperty("ops", out JsonElement ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"{id}: a patch_recipe part holds a non-empty list ops and an optional repeat");
            }

            if (ops.EnumerateArray().Any(o => o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("op", out JsonElement name) || name.ValueKind != JsonValueKind.String))
            {
                throw new InvalidOperationException($"{id}: patch_recipe operations must be objects with a string op");
            }

            int repeat = 1;
            if (part.TryGetProperty("repeat", out JsonElement r) && (!r.TryGetInt32(out repeat) || repeat < 0))
            {
                throw new InvalidOperationException($"{id}: patch_recipe repeat must be a non-negative integer");
            }

            total += (long)ops.GetArrayLength() * repeat;
            if (total > MaxRecipeOperations)
            {
                throw new InvalidOperationException($"{id}: patch_recipe builds more than {MaxRecipeOperations} operations");
            }
        }

        var text = new StringBuilder("[");
        var canonical = new StringBuilder("[");
        bool firstOp = true;
        foreach (JsonElement part in recipe.EnumerateArray())
        {
            int repeat = part.TryGetProperty("repeat", out JsonElement r) ? r.GetInt32() : 1;
            var rawOps = part.GetProperty("ops").EnumerateArray().Select(o => o.GetRawText()).ToArray();
            var canonicalOps = part.GetProperty("ops").EnumerateArray().Select(o => { var sb = new StringBuilder(); AppendCanonical(sb, o); return sb.ToString(); }).ToArray();
            for (int i = 0; i < repeat; i++)
            {
                for (int k = 0; k < rawOps.Length; k++)
                {
                    if (!firstOp)
                    {
                        text.Append(',');
                        canonical.Append(',');
                    }

                    firstOp = false;
                    text.Append(rawOps[k]);
                    canonical.Append(canonicalOps[k]);
                }
            }
        }

        text.Append(']');
        canonical.Append(']');

        // The kit pins the count and the digest of what it builds (JSON with sorted keys, compact, ASCII only).
        string pins = Path.Combine(Corpus.KitDirectory!, "tests", "pins.json");
        if (File.Exists(pins))
        {
            using JsonDocument pinDoc = JsonDocument.Parse(File.ReadAllBytes(pins));
            if (pinDoc.RootElement.GetProperty("own").TryGetProperty("generated_patches", out JsonElement table))
            {
                Assert.True(table.TryGetProperty(id, out JsonElement pin), $"{id}: the kit pins no generated patch for this case");
                Assert.Equal(pin.GetProperty("ops").GetInt64(), total);
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
                Assert.Equal(pin.GetProperty("sha256").GetString(), digest);
            }
        }

        using JsonDocument built = JsonDocument.Parse(text.ToString());
        return built.RootElement.Clone();
    }

    // Python's json.dumps(sort_keys=True, separators=(",", ":")) for the values a patch holds.
    private static void AppendCanonical(StringBuilder sb, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (JsonProperty p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    AppendCanonicalString(sb, p.Name);
                    sb.Append(':');
                    AppendCanonical(sb, p.Value);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    AppendCanonical(sb, item);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                AppendCanonicalString(sb, value.GetString()!);
                break;
            default:
                sb.Append(value.GetRawText());
                break;
        }
    }

    private static void AppendCanonicalString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ' or > '\u007f':
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
    }

    // The bytes a recipe describes: parts in order, each a text repeated some times or a run of numbers.
    private static byte[] FromRecipe(JsonElement recipe)
    {
        var bytes = new List<byte>();
        foreach (JsonElement part in recipe.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement text))
            {
                byte[] unit = Encoding.UTF8.GetBytes(text.GetString()!);
                int repeat = part.TryGetProperty("repeat", out JsonElement r) ? r.GetInt32() : 1;
                for (int i = 0; i < repeat; i++)
                {
                    bytes.AddRange(unit);
                }

                continue;
            }

            JsonElement run = part.GetProperty("numbers");
            int count = run.GetProperty("count").GetInt32();
            int start = run.TryGetProperty("start", out JsonElement st) ? st.GetInt32() : 0;
            string prefix = run.TryGetProperty("prefix", out JsonElement pf) ? pf.GetString()! : string.Empty;
            string suffix = run.TryGetProperty("suffix", out JsonElement sf) ? sf.GetString()! : string.Empty;
            int width = run.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(prefix + (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0') + suffix));
            }
        }

        return [.. bytes];
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
        "limit" => YamlErrorCategory.Limit,
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
