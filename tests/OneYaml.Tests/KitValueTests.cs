using System.Text.Json;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Runs the kit's own cases that ask for a value, through the generic load.</summary>
public class KitValueTests
{
    // Cases whose expected value needs a mapping keyed by something other than text. The generic model keeps a
    // key's source text and refuses a collection as a key, so these end in the error named here; the document
    // type reads them. Each is checked to fail that way, so none can join the list unnoticed.
    private static readonly Dictionary<string, (YamlErrorCategory Category, string Message)> s_modelDeviations = new()
    {
        ["schema/mapping-as-a-mapping-key"] = (YamlErrorCategory.Binding, "complex keys"),
        ["schema/sequence-as-a-mapping-key"] = (YamlErrorCategory.Binding, "complex keys"),
        ["schema/quoted-and-plain-keys-differ-by-type"] = (YamlErrorCategory.DuplicateKey, "appears twice in the mapping"),
    };

    public static IEnumerable<object[]> Cases() => Corpus.Rows(Corpus.Tier(ValueCaseIds("schema", "safety", "parse", "diagnostics"), large: false), Corpus.KitVariable);

    public static IEnumerable<object[]> LargeCases() => Corpus.Rows(Corpus.Tier(ValueCaseIds("schema", "safety", "parse", "diagnostics"), large: true), Corpus.KitVariable);

    private static IEnumerable<string> ValueCaseIds(params string[] categories)
    {
        string? kit = Corpus.KitDirectory;
        if (kit is null)
        {
            yield break;
        }

        foreach (string category in categories)
        {
            string root = Path.Combine(kit, "suites", "own", "cases", category);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string caseFile = Path.Combine(dir, "case.json");
                if (!File.Exists(caseFile))
                {
                    continue;
                }

                using JsonDocument doc = Corpus.ReadJson(caseFile);
                if (doc.RootElement.GetProperty("operation").GetProperty("type").GetString() == "value")
                {
                    yield return category + "/" + Path.GetFileName(dir);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_gives_the_expected_value_or_error(string id) => Run(id);

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(LargeCases))]
    public void Large_generated_case_gives_the_expected_value_or_error(string id) => Run(id);

    private static void Run(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.KitVariable} is not set");
        }

        string dir = Corpus.KitCaseDirectory(id);
        using JsonDocument doc = Corpus.ReadJson(Path.Combine(dir, "case.json"));
        JsonElement root = doc.RootElement;
        byte[] input = KitInput.Read(dir, root);
        JsonElement expect = root.GetProperty("expect");
        if (s_modelDeviations.TryGetValue(id, out (YamlErrorCategory Category, string Message) deviation))
        {
            YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.DeserializeAll<object>(input));
            Assert.Equal(deviation.Category, ex.Category);
            Assert.Contains(deviation.Message, ex.Message, StringComparison.Ordinal);
            return;
        }

        switch (expect.GetProperty("kind").GetString())
        {
            case "value":
                {
                    List<object?> actual = YamlSerializer.DeserializeAll<object>(input);
                    AssertValue(actual, ReadExpected(dir, expect));
                    break;
                }

            case "error":
                {
                    YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.DeserializeAll<object>(input));
                    AssertCategory(ex, expect);
                    break;
                }

            case "bounded":
                RunBounded(dir, input, expect);
                break;
            default:
                Assert.Fail("unknown expectation " + expect);
                break;
        }
    }

    private static void RunBounded(string dir, byte[] input, JsonElement expect)
    {
        long memory = expect.GetProperty("memory_mb").GetInt64() * 1024 * 1024;
        long before = GC.GetAllocatedBytesForCurrentThread();
        List<object?>? actual = null;
        YamlException? error = null;
        try
        {
            actual = YamlSerializer.DeserializeAll<object>(input);
        }
        catch (YamlException ex)
        {
            error = ex;
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < memory, "allocated more than the bound");
        if (error is not null)
        {
            // The allowed refusals are one category name or a list of names.
            var allowed = new List<string>();
            if (expect.TryGetProperty("error", out JsonElement want))
            {
                allowed.AddRange(want.ValueKind == JsonValueKind.Array ? want.EnumerateArray().Select(x => x.GetString()!) : [want.GetString()!]);
            }

            Assert.True(allowed.Any(name => CategoryOf(name) == error.Category),
                $"expected a value or one of [{string.Join(", ", allowed)}], got {error.Category}: {error.Message}");
            return;
        }

        Assert.True(expect.TryGetProperty("accept", out JsonElement accept), "the case has no accepted value but the input loaded");
        AssertValue(actual!, ReadExpected(dir, accept));
    }

    private static JsonElement ReadExpected(string dir, JsonElement expect)
    {
        if (expect.TryGetProperty("file", out JsonElement file))
        {
            return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, file.GetString()!)), ValueModel.Deep).RootElement;
        }

        return expect.GetProperty("value");
    }

    private static void AssertValue(List<object?> actual, JsonElement expected)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        var report = new ValueModel.Report();
        int i = 0;
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string? diff = ValueModel.Diff(actual[i], item, report);
            Assert.True(diff is null, $"document {i}: {diff}");
            i++;
        }
    }

    private static YamlErrorCategory CategoryOf(string category) => category switch
    {
        "syntax" => YamlErrorCategory.Syntax,
        "encoding" => YamlErrorCategory.Encoding,
        "limit" => YamlErrorCategory.Limit,
        "duplicate-key" or "duplicate_key" or "duplicate" => YamlErrorCategory.DuplicateKey,
        _ => throw new InvalidOperationException("unknown error category " + category),
    };

    private static void AssertCategory(YamlException ex, JsonElement expect)
    {
        string category = expect.TryGetProperty("category", out JsonElement c) ? c.GetString()! : "any";
        YamlErrorCategory? want = category == "any" ? null : CategoryOf(category);
        if (want is not null)
        {
            Assert.Equal(want, ex.Category);
        }

        if (expect.TryGetProperty("line", out JsonElement line))
        {
            Assert.Equal(line.GetInt32(), ex.Line);
        }

        if (expect.TryGetProperty("column", out JsonElement column))
        {
            Assert.Equal(column.GetInt32(), ex.Column);
        }
    }
}
