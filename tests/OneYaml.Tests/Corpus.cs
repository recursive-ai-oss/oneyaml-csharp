using System.Text.Json;

namespace OneYaml.Tests;

/// <summary>
/// Locates the shared conformance data. The paths come from the environment so the repository
/// holds no machine-specific locations; tests that need them skip when a variable is unset.
/// </summary>
internal static class Corpus
{
    internal const string SuiteVariable = "ONEYAML_YTS_DIR";
    internal const string KitVariable = "ONEYAML_KIT";

    internal static string? SuiteDirectory => Existing(Environment.GetEnvironmentVariable(SuiteVariable));

    internal static string? KitDirectory => Existing(Environment.GetEnvironmentVariable(KitVariable));

    private static string? Existing(string? path) => !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;

    /// <summary>The ids of suite cases, "ID" or "ID/NN" for numbered variants.</summary>
    internal static IEnumerable<string> SuiteCaseIds()
    {
        string? dir = SuiteDirectory;
        if (dir is null)
        {
            yield break;
        }

        foreach (string caseDir in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string id = Path.GetFileName(caseDir);
            if (File.Exists(Path.Combine(caseDir, "in.yaml")))
            {
                yield return id;
                continue;
            }

            foreach (string sub in Directory.EnumerateDirectories(caseDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(sub, "in.yaml")))
                {
                    yield return id + "/" + Path.GetFileName(sub);
                }
            }
        }
    }

    /// <summary>
    /// The ids ("category/name") of the kit's own cases in the given categories that ask for events.
    /// Cases that ask for values are for the layers above the reader.
    /// </summary>
    internal static IEnumerable<string> KitEventCaseIds(params string[] categories)
    {
        string? kit = KitDirectory;
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

                using JsonDocument doc = ReadJson(caseFile);
                if (doc.RootElement.GetProperty("operation").GetProperty("type").GetString() == "events")
                {
                    yield return category + "/" + Path.GetFileName(dir);
                }
            }
        }
    }

    internal static string KitCaseDirectory(string id) => Path.Combine(KitDirectory!, "suites", "own", "cases", id);

    internal static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Keeps the ids of the cases that belong to one tier: cases whose generated input is a megabyte or more run in
    /// the slow tier, the rest in the fast one.
    /// </summary>
    internal static IEnumerable<string> Tier(IEnumerable<string> ids, bool large) =>
        ids.Where(id =>
        {
            using JsonDocument doc = ReadJson(Path.Combine(KitCaseDirectory(id), "case.json"));
            return (KitInput.RecipeSize(doc.RootElement) >= KitInput.LargeBytes) == large;
        });

    /// <summary>Wraps ids for a Theory; a lone placeholder row lets the test report a skip when the data is missing.</summary>
    internal static IEnumerable<object[]> Rows(IEnumerable<string> ids)
    {
        bool any = false;
        foreach (string id in ids)
        {
            any = true;
            yield return [id];
        }

        if (!any)
        {
            yield return [string.Empty];
        }
    }
}
