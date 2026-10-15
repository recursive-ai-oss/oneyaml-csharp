using System.Text.Json;

namespace OneYaml.Tests;

/// <summary>
/// Locates the shared conformance data. The paths come from the environment so the repository
/// holds no machine-specific locations. A test that needs a variable skips when it is unset; a variable
/// that names a missing directory, or a data set that turns out empty, fails the test instead, so a
/// wrong path can never pass as a skip.
/// </summary>
internal static class Corpus
{
    internal const string SuiteVariable = "ONEYAML_YTS_DIR";
    internal const string KitVariable = "ONEYAML_KIT";
    internal const string WorkloadsVariable = "ONEYAML_WORKLOADS";

    internal static string? SuiteDirectory => Existing(SuiteVariable);

    internal static string? KitDirectory => Existing(KitVariable);

    /// <summary>
    /// The workload directories (each holds input.yaml). ONEYAML_WORKLOADS names a prepared set, laid out as the kit
    /// writes it (a workloads folder with one directory per workload); without it the kit's real-world files are used.
    /// </summary>
    internal static IEnumerable<string> Workloads()
    {
        string? prepared = Existing(WorkloadsVariable);
        string root;
        if (prepared is not null)
        {
            root = Path.Combine(prepared, "workloads");
        }
        else if (KitDirectory is { } kit)
        {
            root = Path.Combine(kit, "bench", "workloads", "real");
        }
        else
        {
            return [];
        }

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"the workload directory {root} does not exist");
        }

        return Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "input.yaml"))).OrderBy(d => d, StringComparer.Ordinal);
    }

    private static string? Existing(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"{variable} names a directory that does not exist: {path}");
        }

        return path;
    }

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

    /// <summary>
    /// Wraps ids for a Theory. With none of the variables set there is no data and a lone placeholder row lets the
    /// test report a skip; with one set, an empty data set is an error.
    /// </summary>
    internal static IEnumerable<object[]> Rows(IEnumerable<string> ids, params string[] variables)
    {
        bool any = false;
        foreach (string id in ids)
        {
            any = true;
            yield return [id];
        }

        if (!any)
        {
            foreach (string variable in variables)
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
                {
                    throw new InvalidOperationException($"{variable} is set but no test data was found under it");
                }
            }

            yield return [string.Empty];
        }
    }
}
