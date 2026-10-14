using System.Text.Json;
using Xunit;

namespace OneYaml.Tests;

/// <summary>Loads every valid yaml-test-suite case generically and compares it with the suite's in.json.</summary>
public class YamlSuiteValueTests
{
    // Cases whose value the generic model does not hold, with the reason. Each is checked to fail in exactly
    // that way, so a case cannot join the list unnoticed. The model keeps a mapping key's source text,
    // and a mapping or sequence used as a key is refused with a pointer to YamlDocument.
    private static readonly Dictionary<string, string> ComplexKey = new()
    {
    };

    public static IEnumerable<object[]> Cases()
    {
        string? dir = Corpus.SuiteDirectory;
        if (dir is null)
        {
            yield return [string.Empty];
            yield break;
        }

        foreach (string id in Corpus.SuiteCaseIds())
        {
            string caseDir = Path.Combine(dir, id.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(caseDir, "in.json")) && !File.Exists(Path.Combine(caseDir, "error")))
            {
                yield return [id];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generic_load_equals_the_suite_value(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string caseDir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        byte[] yaml = File.ReadAllBytes(Path.Combine(caseDir, "in.yaml"));
        List<JsonDocument> expected = Corrected(id) ?? ValueModel.ParseValues(File.ReadAllBytes(Path.Combine(caseDir, "in.json")));
        List<object?> actual;
        try
        {
            actual = YamlSerializer.DeserializeAll<object>(Convert(yaml));
        }
        catch (YamlException ex) when (ex.Message.Contains("complex keys", StringComparison.Ordinal))
        {
            Assert.True(ComplexKey.ContainsKey(id), $"{id} is refused for a complex key but is not listed");
            return;
        }

        Assert.False(ComplexKey.ContainsKey(id), $"{id} is listed as refused but loads");
        Assert.Equal(expected.Count, actual.Count);
        var report = new ValueModel.Report();
        for (int i = 0; i < actual.Count; i++)
        {
            string? diff = ValueModel.Diff(actual[i], expected[i].RootElement, report);
            Assert.True(diff is null, $"{id} document {i}: {diff}");
        }
    }

    public static IEnumerable<object[]> ValuelessCases()
    {
        string? dir = Corpus.SuiteDirectory;
        if (dir is null)
        {
            yield return [string.Empty];
            yield break;
        }

        foreach (string id in Corpus.SuiteCaseIds())
        {
            string caseDir = Path.Combine(dir, id.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(Path.Combine(caseDir, "in.json")) && !File.Exists(Path.Combine(caseDir, "error")))
            {
                yield return [id];
            }
        }
    }

    // Valid cases that ship no value (mostly complex keys and empty streams) must still load, or be refused
    // for a complex key and nothing else (2JQS has the empty key twice).
    [Theory]
    [MemberData(nameof(ValuelessCases))]
    public void Valid_case_without_a_value_loads_or_is_refused_for_a_complex_key(string id)
    {
        if (id.Length == 0)
        {
            Assert.Skip($"{Corpus.SuiteVariable} is not set");
        }

        string caseDir = Path.Combine(Corpus.SuiteDirectory!, id.Replace('/', Path.DirectorySeparatorChar));
        string yaml = Convert(File.ReadAllBytes(Path.Combine(caseDir, "in.yaml")));
        try
        {
            YamlSerializer.DeserializeAll<object>(yaml);
        }
        catch (YamlException ex) when (id == "2JQS")
        {
            // Two empty keys: valid syntax, but the same (null) key twice.
            Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        }
        catch (YamlException ex)
        {
            Assert.Contains("complex keys", ex.Message, StringComparison.Ordinal);
        }
    }

    // The suite's text, as a string, so the string entry point is the one under test.
    private static string Convert(byte[] utf8) => new System.Text.UTF8Encoding(false, false).GetString(utf8);

    private static List<JsonDocument>? Corrected(string id)
    {
        string? kit = Corpus.KitDirectory;
        if (kit is null)
        {
            return null;
        }

        string file = Path.Combine(kit, "suites", "yaml-test-suite", "corrections.json");
        if (!File.Exists(file))
        {
            return null;
        }

        using JsonDocument corrections = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (JsonElement entry in corrections.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty("case").GetString() == "yts:" + id + "@value" && entry.TryGetProperty("value", out JsonElement value))
            {
                var docs = new List<JsonDocument>();
                foreach (JsonElement doc in value.EnumerateArray())
                {
                    docs.Add(JsonDocument.Parse(doc.GetRawText()));
                }

                return docs;
            }
        }

        return null;
    }
}
